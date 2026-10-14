from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
import subprocess
import sys
import uuid
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

from book_manifest import resolve_book_manifest
from prepare_audio_text import prepare_chapter
from spoken_text import has_spoken_content
from pronunciations import load_pronunciations, pronunciation_fingerprint
from chat_label_recovery import recover_chat_labels
from context_pronunciations import prepare_context_pronunciations, digest as context_digest

SYNTHESIS_RESULT_PREFIX = "__STORYCAST_SYNTHESIS_RESULT__"


class FragmentRewriteUnavailable(RuntimeError):
    """The model could not supply a usable replacement; audio can still proceed."""


class PersistentSynthesisWorker:
    def __init__(self, script_path: Path) -> None:
        self.process = subprocess.Popen(
            [
                sys.executable,
                str(script_path),
                "--persistent-worker",
            ],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            text=True,
            bufsize=1,
        )

    def run(
        self,
        name: str,
        arguments: list[str],
    ) -> int:
        if self.process.stdin is None or self.process.stdout is None:
            raise RuntimeError("Synthesis worker pipes are unavailable.")

        print()
        print(f"===== {name} =====", flush=True)
        request = {
            "arguments": arguments[2:],
        }
        self.process.stdin.write(json.dumps(request) + "\n")
        self.process.stdin.flush()

        for line in self.process.stdout:
            if line.startswith(SYNTHESIS_RESULT_PREFIX):
                response = json.loads(
                    line[len(SYNTHESIS_RESULT_PREFIX):]
                )

                if not response.get("ok"):
                    raise RuntimeError(
                        "Persistent synthesis failed: " +
                        response.get("error", "unknown error")
                    )

                exit_code = int(response.get("exitCode", 1))
                if exit_code != 0:
                    raise RuntimeError(
                        f"{name} failed with exit code {exit_code}."
                    )

                return exit_code

            print(line, end="", flush=True)

        raise RuntimeError(
            "Persistent synthesis worker exited unexpectedly."
        )

    def close(self) -> None:
        if self.process.poll() is not None:
            return

        if self.process.stdin is not None:
            try:
                self.process.stdin.write(
                    json.dumps({"command": "stop"}) + "\n"
                )
                self.process.stdin.flush()
            except BrokenPipeError:
                pass

        try:
            self.process.wait(timeout=30)
        except subprocess.TimeoutExpired:
            self.process.terminate()
            self.process.wait(timeout=10)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Produce and verify one complete StoryCast chapter."
        )
    )
    parser.add_argument(
        "book",
        type=Path,
    )
    parser.add_argument(
        "voice_library",
        type=Path,
    )
    parser.add_argument(
        "--chapter",
        required=True,
    )
    parser.add_argument(
        "--whisper-model",
        default="small.en",
    )
    parser.add_argument(
        "--pause",
        type=float,
        default=0.18,
    )
    run_directory_group = (
        parser.add_mutually_exclusive_group()
    )
    run_directory_group.add_argument(
        "--run-directory",
        type=Path,
        default=None,
    )
    run_directory_group.add_argument(
        "--resume-run-directory",
        type=Path,
        default=None,
    )
    parser.add_argument(
        "--max-segment-attempts",
        type=int,
        default=3,
        help=(
            "Verification attempts per wording (default: 3)."
        ),
    )
    parser.add_argument(
        "--rewrite-model",
        default="qwen3.8:27b",
    )
    parser.add_argument("--audio-preparation-model", help="Defaults to the selected rewrite model.")
    parser.add_argument(
        "--ollama-url",
        default=(
            "http://host.docker.internal:11434/"
        ),
    )
    parser.add_argument(
        "--max-fragment-rewrites",
        type=int,
        default=3,
    )
    parser.add_argument("--allow-review", action="store_true")
    parser.add_argument("--no-ambience", action="store_true", help="Export clean narration without SoundForge ambience.")
    parser.add_argument("--soundforge-url", default="http://host.docker.internal:7861")
    parser.add_argument("--ambience-gain-db", type=float, default=-22)
    parser.add_argument("--remix", action="store_true", help="Allow adding or changing ambience on a completed --resume-run-directory.")
    return parser.parse_args()


def create_run_path(book: Path) -> Path:
    timestamp = datetime.now(
        timezone.utc
    ).strftime("%Y%m%d-%H%M%S-%f")[:-3]

    run_id = (
        f"{timestamp}-production-"
        f"{uuid.uuid4().hex[:8]}"
    )

    return (
        book / "output" / run_id
    ).resolve()


def run_stage(
    name: str,
    arguments: list[str],
    allowed_exit_codes: tuple[int, ...] = (0,),
) -> int:
    print()
    print(f"===== {name} =====", flush=True)

    result = subprocess.run(
        arguments,
        check=False,
    )

    if result.returncode not in allowed_exit_codes:
        raise RuntimeError(
            f"{name} failed with exit code "
            f"{result.returncode}."
        )

    return result.returncode


def load_json(path: Path) -> dict:
    with path.open(
        "r",
        encoding="utf-8-sig",
    ) as stream:
        return json.load(stream)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()

    with path.open("rb") as stream:
        for block in iter(
            lambda: stream.read(1024 * 1024),
            b"",
        ):
            digest.update(block)

    return digest.hexdigest()


def describe_files(
    paths: list[Path],
) -> list[dict]:
    return [
        {
            "path": str(path.resolve()),
            "sha256": sha256_file(
                path.resolve()
            ),
        }
        for path in paths
    ]


def create_stage_checkpoint(
    input_paths: list[Path],
    settings: dict,
    output_paths: list[Path],
) -> dict:
    return {
        "completedUtc": datetime.now(
            timezone.utc
        ).isoformat(),
        "inputs": describe_files(input_paths),
        "settings": settings,
        "outputs": describe_files(output_paths),
    }


def stage_checkpoint_is_reusable(
    checkpoint: dict | None,
    input_paths: list[Path],
    settings: dict,
    output_paths: list[Path],
) -> bool:
    if checkpoint is None:
        return False

    if checkpoint.get("settings") != settings:
        return False

    try:
        inputs = describe_files(input_paths)
        outputs = describe_files(output_paths)
    except (FileNotFoundError, OSError):
        return False

    return (
        checkpoint.get("inputs") == inputs and
        checkpoint.get("outputs") == outputs
    )


def write_run_manifest(
    path: Path,
    report: dict,
) -> None:
    path.parent.mkdir(
        parents=True,
        exist_ok=True,
    )

    temporary_path = path.with_name(
        f".{path.name}.tmp"
    )

    with temporary_path.open(
        "w",
        encoding="utf-8",
        newline="\n",
    ) as stream:
        json.dump(
            report,
            stream,
            indent=2,
            ensure_ascii=False,
        )
        stream.write("\n")

    temporary_path.replace(path)


def write_json_atomic(
    path: Path,
    value: dict,
) -> None:
    """Write JSON without exposing a partially written file."""
    path.parent.mkdir(
        parents=True,
        exist_ok=True,
    )
    temporary_path = path.with_name(
        f".{path.name}.tmp"
    )

    with temporary_path.open(
        "w",
        encoding="utf-8",
        newline="\n",
    ) as stream:
        json.dump(
            value,
            stream,
            indent=2,
            ensure_ascii=False,
        )
        stream.write("\n")

    temporary_path.replace(path)


def rewrite_short_fragment(
    chapter_manifest: Path,
    segment_index: int,
    rewrite_number: int,
    model: str,
    ollama_url: str,
) -> str:
    manifest = load_json(chapter_manifest)
    segments = sorted(
        manifest["segments"],
        key=lambda item: int(item["index"]),
    )

    matching_positions = [
        position
        for position, segment in enumerate(segments)
        if int(segment["index"]) == segment_index
    ]

    if len(matching_positions) != 1:
        raise RuntimeError(
            "Expected exactly one segment for AI rewrite: "
            f"{segment_index}"
        )

    position = matching_positions[0]
    target = segments[position]
    source_text = str(target["sourceText"])
    if not has_spoken_content(source_text):
        raise ValueError(f"Segment {segment_index} contains no spoken text; refusing to invent dialogue. Regenerate the production script.")
    previous_spoken_text = str(
        target.get("spokenText", source_text)
    )

    context_start = max(0, position - 2)
    context_end = min(
        len(segments),
        position + 3,
    )

    context = [
        {
            "index": int(segment["index"]),
            "speakerId": str(segment["speakerId"]),
            "text": str(
                segment.get(
                    "spokenText",
                    segment["sourceText"],
                )
            ),
            "target": int(segment["index"]) ==
                segment_index,
        }
        for segment in segments[
            context_start:context_end
        ]
    ]

    previous_rewrites = list(
        target.get("fragmentRewrites", [])
    )

    system_prompt = (
        "You minimally rewrite a narration or dialogue segment "
        "that repeatedly failed text-to-speech synthesis. "
        "Preserve the speaker's immediate intent, emotional tone, "
        "tense, viewpoint, and scene continuity. This is an adult "
        "fiction audiobook. Natural profanity and insults such as "
        "asshole, dickhead, or fuck are explicitly permitted when "
        "supported by the surrounding context. Do not censor adult "
        "language. Add or substitute no more than four spoken words. "
        "Do not introduce new facts, names, actions, or exposition. "
        "Return one natural replacement fragment and no commentary."
    )

    prompt = json.dumps(
        {
            "task": (
                "Rewrite the target fragment so it is easier "
                "for speech synthesis while preserving intent."
            ),
            "originalText": source_text,
            "previousSpokenText": previous_spoken_text,
            "rewriteNumber": rewrite_number,
            "previousRewrites": previous_rewrites,
            "context": context,
        },
        ensure_ascii=False,
        indent=2,
    )

    schema = {
        "type": "object",
        "properties": {
            "spokenText": {
                "type": "string",
            },
        },
        "required": ["spokenText"],
        "additionalProperties": False,
    }

    endpoint = ollama_url.rstrip("/") + "/api/generate"
    rejected = []
    spoken_text = ""
    for response_attempt in range(1, 4):
        attempt_prompt = prompt
        if rejected:
            attempt_prompt += "\nPrevious responses were invalid:\n" + "\n".join(rejected)
        attempt_prompt += (
            "\nReturn different spoken words from the original, current wording, "
            "and earlier rewrites. Changing only punctuation or spacing is not a rewrite."
        )
        request = urllib.request.Request(
            endpoint,
            data=json.dumps({
                "model": model, "system": system_prompt, "prompt": attempt_prompt,
                "format": schema, "stream": False, "think": False,
                "options": {"temperature": 0.4, "num_ctx": 8192, "num_predict": max(128, len(source_text) + 64)},
            }).encode("utf-8"),
            headers={"Content-Type": "application/json"}, method="POST",
        )
        try:
            with urllib.request.urlopen(request, timeout=300) as response:
                response_body = response.read().decode("utf-8")
        except urllib.error.URLError as error:
            raise FragmentRewriteUnavailable(f"Ollama fragment rewrite failed: {error}") from error

        result = None
        try:
            envelope = json.loads(response_body)
            result = json.loads(envelope["response"])
            spoken_text = result["spokenText"]
            if not isinstance(spoken_text, str) or not spoken_text.strip():
                raise ValueError("Return a nonempty spokenText string.")
            spoken_text = spoken_text.strip()
            def words(text):
                return re.findall(r"[A-Za-z0-9]+(?:'[A-Za-z0-9]+)?", text.casefold())
            spoken_words = words(spoken_text)
            if not spoken_words:
                raise ValueError("The rewritten fragment contains no words.")
            if len(spoken_words) > len(words(source_text)) + 4:
                raise ValueError("The rewritten fragment added more than four words.")
            forbidden = [source_text, previous_spoken_text] + [
                str(item.get("spokenText", "")) for item in previous_rewrites
            ]
            if any(spoken_words == words(value) for value in forbidden):
                raise ValueError("The suggestion repeats unchanged or previously attempted spoken words.")
            break
        except (ValueError, KeyError, TypeError) as error:
            rejected.append(f"Response {response_attempt}: {result if result is not None else response_body[:1000]}\nCorrection: {error}")
            print(f"Fragment rewrite {response_attempt}/3 rejected: {error}")
    else:
        raise FragmentRewriteUnavailable(
            f"Ollama fragment rewrite failed after 3 corrective attempts for segment {segment_index}. "
            "Saved chapter audio and retry progress are preserved. " + rejected[-1]
        )

    rewrite_record = {
        "rewriteNumber": rewrite_number,
        "sourceText": source_text,
        "previousSpokenText": previous_spoken_text,
        "spokenText": spoken_text,
        "model": model,
        "reason":
            "repeated-segment-verification-failure",
    }

    target["spokenText"] = spoken_text
    target["fragmentRewrite"] = rewrite_record
    target.setdefault(
        "fragmentRewrites",
        [],
    ).append(rewrite_record)

    manifest["segments"] = segments

    temporary_manifest_path = (
        chapter_manifest.with_name(
            f".{chapter_manifest.name}.tmp"
        )
    )

    with temporary_manifest_path.open(
        "w",
        encoding="utf-8",
        newline="\n",
    ) as stream:
        json.dump(
            manifest,
            stream,
            indent=2,
            ensure_ascii=False,
        )
        stream.write("\n")

    temporary_manifest_path.replace(
        chapter_manifest
    )

    print()
    print(
        f"AI fragment rewrite {rewrite_number}: "
        f"segment {segment_index}",
        flush=True,
    )
    print(
        f"  Original: {source_text}",
        flush=True,
    )
    print(
        f"  Spoken:   {spoken_text}",
        flush=True,
    )

    return spoken_text


def align_restored_candidate_manifest(chapter_manifest: Path, state: dict) -> None:
    """Keep a restored clip's expected wording and hashes aligned with audio."""
    manifest = load_json(chapter_manifest)
    changed = False
    for segment in manifest.get("segments", []):
        best = state.get("bestCandidates", {}).get(str(segment["index"]))
        if not best:
            continue
        candidate_path = Path(best["audioPath"])
        destination = Path(segment["audioPath"])
        if not candidate_path.is_file() or not destination.is_file() or sha256_file(candidate_path) != sha256_file(destination):
            continue
        verification = best["verification"]
        spoken = verification.get("expectedText")
        if not isinstance(spoken, str):
            continue
        segment["spokenText"] = spoken
        segment["restoredCandidateAttempt"] = best["attempt"]
        for chunk, verified in zip(segment.get("chunks", []), verification.get("chunks", [])):
            chunk["sourceText"] = verified["expectedText"]
            chunk["synthesisText"] = verified["expectedText"]
            if Path(chunk["audioPath"]).is_file():
                chunk["sha256"] = sha256_file(Path(chunk["audioPath"]))
        changed = True
    if changed:
        write_json_atomic(chapter_manifest, manifest)


def assemble_verified_audio(tools_directory: Path, chapter_manifest: Path, pause: float, allow_review: bool = False) -> int:
    return run_stage("Assembly", [sys.executable, str(tools_directory / "assemble_chapter_probe.py"),
                                 str(chapter_manifest), "--pause", str(pause),
                                 *(["--allow-review"] if allow_review else [])])


def verify_segments_with_retries(
    tools_directory: Path,
    book: Path,
    voice_library: Path,
    chapter_id: str,
    run_directory: Path,
    chapter_manifest: Path,
    whisper_model: str,
    max_attempts: int,
    attempt_history: list[dict],
    rewrite_model: str = "qwen3.8:27b",
    ollama_url: str = (
        "http://host.docker.internal:11434/"
    ),
    max_fragment_rewrites: int = 3,
    synthesis_runner=None,
    allow_review: bool = False,
) -> None:
    verification_path = chapter_manifest.parent / "verification.json"
    retry_state_path = chapter_manifest.parent / "segment-retry-state.json"
    candidate_directory = (
        chapter_manifest.parent / "short-utterance-candidates"
    )
    total_attempts = max_attempts * (max_fragment_rewrites + 1)

    if retry_state_path.is_file():
        state = load_json(retry_state_path)

        expected_settings = {
            "maxAttemptsPerWording": max_attempts,
            "maxFragmentRewrites": max_fragment_rewrites,
            "totalAttempts": total_attempts,
        }

        if state.get("settings") != expected_settings:
            raise RuntimeError(
                "Segment retry settings do not match the persisted state."
            )
    else:
        state = {
            "schemaVersion": 1,
            "settings": {
                "maxAttemptsPerWording": max_attempts,
                "maxFragmentRewrites": max_fragment_rewrites,
                "totalAttempts": total_attempts,
            },
            "attempt": 1,
            "pendingAction": "verify",
            "unresolvedSegmentIndexes": None,
            "rewriteCounts": {},
            "bestCandidates": {},
            "completed": False,
        }

        legacy_reports: list[tuple[int, Path]] = []

        for report_path in chapter_manifest.parent.glob(
            "verification-attempt-*.json"
        ):
            match = re.fullmatch(
                r"verification-attempt-(\d+)\.json",
                report_path.name,
            )

            if match:
                legacy_reports.append((int(match.group(1)), report_path))

        legacy_reports.sort()

        if legacy_reports:
            completed_attempt, latest_report_path = legacy_reports[-1]

            if completed_attempt > total_attempts:
                raise RuntimeError(
                    "Existing verification attempts exceed the configured "
                    "short-fragment retry schedule."
                )

            latest_report = load_json(latest_report_path)
            unresolved = [
                int(segment["segmentIndex"])
                for segment in latest_report.get("segments", [])
                if segment.get("status") == "fail"
            ]
            state["attempt"] = completed_attempt
            state["unresolvedSegmentIndexes"] = unresolved
            state["pendingAction"] = (
                "complete"
                if not unresolved
                else (
                    "rewrite"
                    if completed_attempt % max_attempts == 0
                    and completed_attempt < total_attempts
                    else "synthesize"
                )
            )
            state["completed"] = not unresolved

            legacy_best: dict[str, dict] = {}

            for report_attempt, report_path in legacy_reports:
                report = load_json(report_path)

                for segment in report.get("segments", []):
                    expected_word_count = int(
                        segment.get("expectedWordCount", 0)
                    )

                    if not 0 < expected_word_count <= 5:
                        continue

                    segment_index = int(segment["segmentIndex"])
                    candidate_path = candidate_directory / (
                        f"segment-{segment_index:04d}-"
                        f"attempt-{report_attempt:02d}.wav"
                    )

                    if not candidate_path.is_file():
                        continue

                    score = [
                        float(segment.get("wordErrorRate", float("inf"))),
                        int(segment.get("editDistance", 2147483647)),
                        abs(
                            int(segment.get("transcribedWordCount", 0)) -
                            expected_word_count
                        ),
                    ]
                    current = legacy_best.get(str(segment_index))

                    if current is None or tuple(score) < tuple(current["score"]):
                        legacy_best[str(segment_index)] = {
                            "attempt": report_attempt,
                            "score": score,
                            "audioPath": str(candidate_path),
                            "verification": dict(segment),
                        }

            state["bestCandidates"] = legacy_best

        write_json_atomic(retry_state_path, state)

    # Changed applicable pronunciations give previously exhausted audio a new
    # bounded verification cycle. Preserve earlier diagnostics before reuse.
    manifest_for_pronunciations = load_json(chapter_manifest)
    pronunciation_text = "\n".join(str(s.get("sourceText", "")) for s in manifest_for_pronunciations.get("segments", []))
    pronunciation_entries = load_pronunciations(book)
    current_pronunciation_hash = pronunciation_fingerprint(pronunciation_text, pronunciation_entries)
    contextual = [segment.get("contextPronunciationFingerprint", "") for segment in manifest_for_pronunciations.get("segments", [])]
    if any(contextual):
        current_pronunciation_hash = context_digest([current_pronunciation_hash, contextual])
    empty_pronunciation_hash = pronunciation_fingerprint(pronunciation_text, [])
    previous_pronunciation_hash = state.get("pronunciationFingerprint", empty_pronunciation_hash)
    if current_pronunciation_hash != previous_pronunciation_hash:
        archive = chapter_manifest.parent / "verification-history" / uuid.uuid4().hex
        archive.mkdir(parents=True)
        for previous in [retry_state_path, verification_path, *chapter_manifest.parent.glob("verification-attempt-*.json")]:
            if previous.is_file():
                shutil.copy2(previous, archive / previous.name)
        state.update(attempt=1, pendingAction="verify", unresolvedSegmentIndexes=None,
                     unresolvedChunks=[], bestCandidates={}, completed=False)
        print("Applicable pronunciations changed; restarting bounded verification. Previous reports: " + str(archive), flush=True)
    state["pronunciationFingerprint"] = current_pronunciation_hash

    # Grant an exhausted legacy chunk run one bounded cycle under the new
    # selective policy. Persist the version so ordinary restarts cannot grant
    # unlimited retries. Preserve every previous report and passed audio chunk.
    if state.get("selectiveChunkRetryVersion", 0) < 1 and not state.get("completed") and int(state.get("attempt", 1)) >= total_attempts and verification_path.is_file():
        previous_report = load_json(verification_path)
        rejected = [chunk for chunk in previous_report.get("chunks", []) if chunk.get("status") != "pass"]
        if rejected:
            archive = chapter_manifest.parent / "verification-history" / uuid.uuid4().hex
            archive.mkdir(parents=True)
            for previous in [retry_state_path, verification_path, *chapter_manifest.parent.glob("verification-attempt-*.json")]:
                if previous.is_file():
                    shutil.copy2(previous, archive / previous.name)
            state.update(attempt=1, pendingAction="verify", completed=False,
                         unresolvedSegmentIndexes=sorted({int(chunk["segmentIndex"]) for chunk in rejected}),
                         unresolvedChunks=[], bestCandidates={})
            state["recognitionRecoveryVersion"] = 1
            print("Selective chunk retry policy upgraded; restarting one bounded verification cycle. Previous reports: " + str(archive), flush=True)
    state["selectiveChunkRetryVersion"] = 1
    if state.get("recognitionRecoveryVersion", 0) < 1 and not state.get("completed") and int(state.get("attempt", 1)) >= total_attempts and verification_path.is_file():
        previous_report = load_json(verification_path)
        rejected = [chunk for chunk in previous_report.get("chunks", []) if chunk.get("status") != "pass"]
        if rejected:
            archive = chapter_manifest.parent / "verification-history" / uuid.uuid4().hex
            archive.mkdir(parents=True)
            for previous in [retry_state_path, verification_path, *chapter_manifest.parent.glob("verification-attempt-*.json")]:
                if previous.is_file():
                    shutil.copy2(previous, archive / previous.name)
            state["recognitionRecoveryPriorAttempts"] = state.get("attempt", 1)
            state.update(attempt=1, pendingAction="verify", completed=False,
                         unresolvedSegmentIndexes=sorted({int(chunk["segmentIndex"]) for chunk in rejected}),
                         unresolvedChunks=[], bestCandidates={})
            print("Independent recognition recovery added; rechecking saved audio once under the new policy. Previous reports: " + str(archive), flush=True)
    state["recognitionRecoveryVersion"] = 1
    write_json_atomic(retry_state_path, state)

    if state.get("completed"):
        align_restored_candidate_manifest(chapter_manifest, state)
        saved_verification = load_json(verification_path)
        unresolved = [int(segment["segmentIndex"])
                      for segment in saved_verification.get("segments", [])
                      if segment["status"] != "pass"]
        if not unresolved:
            return
        state["completed"] = False
        state["pendingAction"] = "verify"
        state["unresolvedSegmentIndexes"] = unresolved
        state["unresolvedChunks"] = []
        write_json_atomic(retry_state_path, state)

    # The chapter manifest is authoritative if the process stopped after an
    # atomic rewrite but before its retry-state checkpoint was written.
    manifest = load_json(chapter_manifest)
    manifest_rewrite_counts = {
        int(segment["index"]): max(
            len(segment.get("fragmentRewrites", [])),
            max((int(item["rewriteNumber"]) for item in segment.get("fragmentRewriteFailures", [])), default=0),
        )
        for segment in manifest.get("segments", [])
    }
    rewrite_counts = {
        int(index): int(count)
        for index, count in state.get("rewriteCounts", {}).items()
    }
    for segment_index, count in manifest_rewrite_counts.items():
        rewrite_counts[segment_index] = max(
            rewrite_counts.get(segment_index, 0),
            count,
        )

    best_short_candidates: dict[int, dict] = {}
    for index, candidate in state.get("bestCandidates", {}).items():
        candidate_value = dict(candidate)
        candidate_value["score"] = tuple(candidate_value["score"])
        candidate_value["audioPath"] = Path(candidate_value["audioPath"])
        best_short_candidates[int(index)] = candidate_value

    unresolved_chunks = state.get("unresolvedChunks", [])
    unresolved_value = state.get("unresolvedSegmentIndexes")
    unresolved_indexes = (
        None
        if unresolved_value is None
        else [int(index) for index in unresolved_value]
    )

    def save_state(
        attempt: int,
        pending_action: str,
        completed: bool = False,
    ) -> None:
        state["attempt"] = attempt
        state["pendingAction"] = pending_action
        state["unresolvedSegmentIndexes"] = unresolved_indexes
        state["unresolvedChunks"] = unresolved_chunks
        state["rewriteCounts"] = {
            str(index): count
            for index, count in rewrite_counts.items()
        }
        state["bestCandidates"] = {
            str(index): {
                **candidate,
                "score": list(candidate["score"]),
                "audioPath": str(candidate["audioPath"]),
            }
            for index, candidate in best_short_candidates.items()
        }
        state["completed"] = completed
        write_json_atomic(retry_state_path, state)

    attempt = int(state.get("attempt", 1))
    pending_action = str(state.get("pendingAction", "verify"))

    # A checkpoint interrupted after its final synthesis must verify that new
    # audio before returning; falling out of this loop is never success.
    while attempt <= total_attempts or (pending_action == "verify" and attempt == total_attempts + 1):
        if pending_action == "rewrite":
            desired_rewrite_number = attempt // max_attempts

            # Older checkpoints scheduled whole-segment rewrites for chunk
            # failures. Resume those as selective retries under this policy.
            if verification_path.is_file():
                saved_report = load_json(verification_path)
                if "chunks" in saved_report:
                    large_indexes = {
                        int(segment["segmentIndex"])
                        for segment in saved_report.get("segments", [])
                        if int(segment.get("expectedWordCount", 0)) > 5
                        and int(segment["segmentIndex"]) in (unresolved_indexes or [])
                    }
                    unresolved_indexes = [index for index in unresolved_indexes or [] if index not in large_indexes]
                    unresolved_chunks = sorted({
                        *(tuple(chunk) for chunk in unresolved_chunks),
                        *((int(chunk["segmentIndex"]), int(chunk["chunkIndex"]))
                          for chunk in saved_report["chunks"]
                          if int(chunk["segmentIndex"]) in large_indexes and chunk["status"] != "pass"),
                    })

            for segment_index in unresolved_indexes or []:
                existing_count = rewrite_counts.get(segment_index, 0)

                if existing_count >= desired_rewrite_number:
                    continue

                try:
                    rewrite_short_fragment(
                        chapter_manifest, segment_index, desired_rewrite_number,
                        rewrite_model, ollama_url,
                    )
                except FragmentRewriteUnavailable as error:
                    manifest = load_json(chapter_manifest)
                    target = next(s for s in manifest["segments"] if int(s["index"]) == segment_index)
                    target["synthesisMaxCharacters"] = max(60, 160 // desired_rewrite_number)
                    target.setdefault("fragmentRewriteFailures", []).append({
                        "rewriteNumber": desired_rewrite_number, "model": rewrite_model,
                        "reason": str(error), "fallback": "smaller-synthesis-chunks",
                        "maxCharacters": target["synthesisMaxCharacters"],
                    })
                    write_json_atomic(chapter_manifest, manifest)
                    print(f"Segment {segment_index}: rewrite unavailable; preserving wording and synthesizing in chunks of at most {target['synthesisMaxCharacters']} characters.", flush=True)
                rewrite_counts[segment_index] = desired_rewrite_number
                save_state(attempt, "rewrite")

            pending_action = "synthesize"
            save_state(attempt, pending_action)
            continue

        if pending_action == "synthesize":
            prepare_context_pronunciations(book, chapter_id, load_json(chapter_manifest)["segments"], rewrite_model, ollama_url)
            synthesis_arguments = [
                sys.executable,
                str(tools_directory / "synthesize_chapter_probe.py"),
                str(book),
                str(voice_library),
                "--chapter",
                chapter_id,
                "--run-directory",
                str(run_directory),
                "--attempt",
                str(attempt),
            ]

            for chunk in unresolved_chunks:
                synthesis_arguments.extend(["--chunk", f"{chunk[0]}:{chunk[1]}"])
            for segment_index in unresolved_indexes or []:
                synthesis_arguments.extend(
                    ["--segment-index", str(segment_index)]
                )

            (synthesis_runner or run_stage)(
                f"Selective synthesis retry {attempt}",
                synthesis_arguments,
            )
            attempt += 1
            pending_action = "verify"
            save_state(attempt, pending_action)
            continue

        verification_arguments = [
            sys.executable,
            str(tools_directory / "verify_chapter_probe.py"),
            str(chapter_manifest),
            "--model",
            whisper_model,
        ]

        if unresolved_indexes is not None:
            for segment_index in sorted(set(unresolved_indexes) | {int(c[0]) for c in unresolved_chunks}):
                verification_arguments.extend(
                    [
                        "--segment-index",
                        str(segment_index),
                    ]
                )

        exit_code = run_stage(
            f"Segment verification attempt {attempt}",
            verification_arguments,
            allowed_exit_codes=(0, 2),
        )

        verification = load_json(verification_path)

        for segment in verification["segments"]:
            segment_index = int(
                segment["segmentIndex"]
            )

            if (
                unresolved_indexes is not None
                and segment_index not in
                unresolved_indexes
            ):
                continue

            expected_word_count = int(
                segment.get(
                    "expectedWordCount",
                    0,
                )
            )

            if not 0 < expected_word_count <= 5:
                continue

            audio_path = Path(
                segment["audioPath"]
            ).resolve()

            if not audio_path.is_file():
                raise FileNotFoundError(
                    "Short-utterance candidate was "
                    f"not found: {audio_path}"
                )

            candidate_directory.mkdir(
                parents=True,
                exist_ok=True,
            )

            candidate_path = candidate_directory / (
                f"segment-{segment_index:04d}-attempt-{attempt:02d}.wav"
            )

            shutil.copy2(
                audio_path,
                candidate_path,
            )

            score = (
                float(
                    segment.get(
                        "wordErrorRate",
                        float("inf"),
                    )
                ),
                int(
                    segment.get(
                        "editDistance",
                        2147483647,
                    )
                ),
                abs(
                    int(
                        segment.get(
                            "transcribedWordCount",
                            0,
                        )
                    ) -
                    expected_word_count
                ),
            )

            current_best = (
                best_short_candidates.get(
                    segment_index
                )
            )

            if (
                current_best is None
                or score < current_best["score"]
            ):
                best_short_candidates[
                    segment_index
                ] = {
                    "attempt": attempt,
                    "score": score,
                    "audioPath": candidate_path,
                    "verification": dict(segment),
                }

        rejected_chunks = [
            chunk
            for chunk in verification.get("chunks", [])
            if chunk["status"] != "pass"
        ]
        rejected_segments = (
            []
            if "chunks" in verification
            else [
                segment
                for segment in verification["segments"]
                if segment["status"] == "fail"
            ]
        )

        attempt_report_path = chapter_manifest.parent / (
            f"verification-attempt-{attempt}.json"
        )

        shutil.copy2(
            verification_path,
            attempt_report_path,
        )

        attempt_history.append(
            {
                "attempt": attempt,
                "verificationPath":
                    str(attempt_report_path),
                "passed":
                    verification["summary"]["passed"],
                "review":
                    verification["summary"]["review"],
                "failed":
                    verification["summary"]["failed"],
                "rejectedSegmentIndexes": [
                    segment["segmentIndex"]
                    for segment in rejected_segments
                ],
                "rejectedChunks": [
                    {
                        "segmentIndex": chunk["segmentIndex"],
                        "chunkIndex": chunk["chunkIndex"],
                    }
                    for chunk in rejected_chunks
                ],
            }
        )

        if allow_review and any(item["status"] == "review" for item in verification.get("chunks", verification["segments"])) and not any(
            item["status"] == "fail" for item in verification.get("chunks", verification["segments"])
        ):
            save_state(attempt, "complete", completed=True)
            print("Review audio retained for final export; verification findings remain in the report.", flush=True)
            return

        if exit_code == 0:
            if rejected_segments or rejected_chunks:
                raise RuntimeError(
                    "Verification returned success "
                    "while reporting rejected segments."
                )

            save_state(attempt, "complete", completed=True)
            return

        if not rejected_segments and not rejected_chunks:
            raise RuntimeError(
                "Verification returned failure without "
                "identifying rejected segments."
            )

        if attempt < total_attempts and (attempt >= max_attempts or state.get("recognitionRecoveryPriorAttempts", 0) >= max_attempts):
            recovery_manifest = load_json(chapter_manifest)
            recovered = recover_chat_labels(recovery_manifest, verification, pronunciation_entries)
            if recovered:
                write_json_atomic(chapter_manifest, recovery_manifest)
                unresolved_indexes = []
                unresolved_chunks = [(int(chunk["segmentIndex"]), int(chunk["chunkIndex"])) for chunk in rejected_chunks]
                pending_action = "synthesize"
                save_state(attempt, pending_action)
                print(f"Recovering ambiguous chat labels by literal handle spelling: {recovered}. Message text and passing chunks are preserved.", flush=True)
                continue

        retry_segments = [
            segment for segment in verification["segments"]
            if segment["status"] == "fail"
            or ("chunks" in verification and segment["status"] == "review")
        ]
        short_indexes = {int(seg["segmentIndex"]) for seg in retry_segments if 0 < int(seg.get("expectedWordCount", 0)) <= 5}
        non_short_rejections = [
            segment
            for segment in retry_segments
            if not (
                0 <
                int(
                    segment.get(
                        "expectedWordCount",
                        0,
                    )
                ) <=
                5
            )
        ]

        if (
            attempt >= total_attempts
            and non_short_rejections
        ):
            if allow_review:
                save_state(attempt, "complete", completed=True)
                print("Recognition retry budget exhausted; final audio will be exported for review with failed findings preserved.", flush=True)
                return
            indexes = [
                segment["segmentIndex"]
                for segment in non_short_rejections
            ]

            raise RuntimeError(
                f"Segments {indexes} did not pass after "
                f"{total_attempts} verification attempts. "
                "Rejected chunks: " + "; ".join(
                    f"{chunk['segmentIndex']}:{chunk['chunkIndex']} "
                    f"({chunk['status']}) expected {chunk.get('expectedText', '')!r}; "
                    f"heard {chunk.get('transcription', '')!r}"
                    for chunk in rejected_chunks
                ) + f". Full report: {verification_path}."
            )

        if attempt >= total_attempts:
            for rejected in retry_segments:
                segment_index = int(
                    rejected["segmentIndex"]
                )

                best = best_short_candidates.get(
                    segment_index
                )

                if best is None:
                    raise RuntimeError(
                        "No short-utterance candidate "
                        "was preserved for segment "
                        f"{segment_index}."
                    )

                destination_path = Path(
                    rejected["audioPath"]
                ).resolve()

                shutil.copy2(
                    best["audioPath"],
                    destination_path,
                )

                selected = dict(
                    best["verification"]
                )

                selected["status"] = "review"
                selected["verificationMode"] = (
                    "best-of-short-utterance-candidates"
                )
                selected["selectedCandidateAttempt"] = (
                    best["attempt"]
                )
                selected["candidateAttempts"] = (
                    total_attempts
                )
                selected["audioPath"] = str(
                    destination_path
                )
                selected["candidateAudioPath"] = str(
                    best["audioPath"]
                )
                for selected_chunk in selected.get("chunks", []):
                    selected_chunk["status"] = "review"
                    selected_chunk["verificationMode"] = selected["verificationMode"]

                for chunk in verification.get("chunks", []):
                    if int(chunk["segmentIndex"]) == segment_index:
                        if len(rejected.get("chunks", [])) == 1:
                            shutil.copy2(best["audioPath"], Path(chunk["audioPath"]))
                        chunk["status"] = "review"
                        chunk["verificationMode"] = selected["verificationMode"]
                for position, existing in enumerate(
                    verification["segments"]
                ):
                    if (
                        int(existing["segmentIndex"]) ==
                        segment_index
                    ):
                        verification[
                            "segments"
                        ][position] = selected
                        break

            verification["summary"] = {
                "total": len(
                    verification.get("chunks", verification["segments"])
                ),
                "passed": sum(
                    segment["status"] == "pass"
                    for segment in
                    verification.get("chunks", verification["segments"])
                ),
                "review": sum(
                    segment["status"] == "review"
                    for segment in
                    verification.get("chunks", verification["segments"])
                ),
                "failed": sum(
                    segment["status"] == "fail"
                    for segment in
                    verification.get("chunks", verification["segments"])
                ),
            }

            write_json_atomic(
                verification_path,
                verification,
            )

            print()
            print(
                "The best remaining short-utterance "
                "candidate was restored for review.",
                flush=True,
            )

            align_restored_candidate_manifest(chapter_manifest, {
                "bestCandidates": {str(index): candidate for index, candidate in best_short_candidates.items()}
            })
            if allow_review and not any(item["status"] == "fail" for item in verification.get("chunks", verification["segments"])):
                save_state(attempt, "complete", completed=True)
                return
            save_state(attempt, "verify", completed=False)
            indexes = [segment["segmentIndex"] for segment in retry_segments]
            raise RuntimeError(f"Segments {indexes} still require review after {total_attempts} attempts. "
                               "Best candidate audio is preserved; assembly was not started.")

        rewrite_due = attempt % max_attempts == 0
        # Reword only genuinely short segments. Regenerating a large segment
        # discards passing chunks, and the rewrite fallback can fragment its
        # sentences into progressively smaller synthesis/ASR inputs.
        unresolved_indexes = [
            int(segment["segmentIndex"])
            for segment in retry_segments
            if int(segment["segmentIndex"]) in short_indexes or "chunks" not in verification
        ]
        # A wording change invalidates all chunks in that segment. Regenerate
        # the whole segment after rewriting; retain selective chunk retries
        # while retrying unchanged wording.
        unresolved_chunks = [[int(c["segmentIndex"]), int(c["chunkIndex"])] for c in rejected_chunks if int(c["segmentIndex"]) not in short_indexes]

        pending_action = (
            "rewrite"
            if rewrite_due and (short_indexes or "chunks" not in verification)
            else "synthesize"
        )
        save_state(attempt, pending_action)

    raise RuntimeError("Segment verification budget exhausted without a verified completion.")

def segment_audio_paths(
    chapter_manifest: Path,
) -> list[Path]:
    manifest = load_json(chapter_manifest)

    paths: list[Path] = []
    for segment in manifest["segments"]:
        paths.extend(
            Path(chunk["audioPath"]).resolve()
            for chunk in segment.get("chunks", [])
        )
        paths.append(Path(segment["audioPath"]).resolve())
    return paths


def run_checkpointed_stage(
    name: str,
    checkpoint_name: str,
    report: dict,
    run_manifest_path: Path,
    input_paths: list[Path],
    settings: dict,
    output_paths: list[Path],
    action,
) -> bool:
    checkpoints = report.setdefault(
        "stageCheckpoints",
        {},
    )

    if stage_checkpoint_is_reusable(
        checkpoints.get(checkpoint_name),
        input_paths,
        settings,
        output_paths,
    ):
        print()
        print(
            f"===== {name} (reused) =====",
            flush=True,
        )
        return False

    action()

    checkpoints[checkpoint_name] = (
        create_stage_checkpoint(
            input_paths,
            settings,
            output_paths,
        )
    )

    write_run_manifest(
        run_manifest_path,
        report,
    )

    return True


def main() -> int:
    args = parse_args()
    if args.remix and args.resume_run_directory is None:
        raise ValueError("--remix requires --resume-run-directory.")

    if args.max_segment_attempts < 1:
        raise ValueError(
            "--max-segment-attempts must be at least 1."
        )

    book = args.book.resolve()
    voice_library = (
        args.voice_library.resolve()
    )

    tools_directory = Path(
        __file__
    ).resolve().parent

    is_resume = (
        args.resume_run_directory is not None
    )

    if is_resume:
        run_directory = (
            args.resume_run_directory.resolve()
        )

        if not run_directory.is_dir():
            raise FileNotFoundError(
                f"Resume run directory was not found: "
                f"{run_directory}"
            )
    elif args.run_directory is not None:
        run_directory = (
            args.run_directory.resolve()
        )

        if run_directory.exists():
            raise FileExistsError(
                f"New run directory already exists: "
                f"{run_directory}"
            )
    else:
        run_directory = create_run_path(
            book
        )

    chapter_directory = (
        run_directory / args.chapter
    )

    run_manifest_path = (
        run_directory / "run.json"
    )

    resumed_utc = datetime.now(
        timezone.utc
    ).isoformat()

    if is_resume:
        if not run_manifest_path.is_file():
            raise FileNotFoundError(
                f"Run manifest was not found: "
                f"{run_manifest_path}"
            )

        report = load_json(
            run_manifest_path
        )

        if (
            Path(report.get("bookPath", "")).resolve() !=
                book or
            Path(
                report.get(
                    "voiceLibraryPath",
                    "",
                )
            ).resolve() != voice_library or
            report.get("chapterId") != args.chapter or
            Path(
                report.get(
                    "chapterDirectory",
                    "",
                )
            ).resolve() != chapter_directory
        ):
            raise RuntimeError(
                "Resume run identity does not match the "
                "requested book, voice library, or chapter."
            )

        if report.get("status") == "completed" and not args.remix:
            raise RuntimeError(
                "A completed production run requires --remix to change ambience while retaining its verified narration."
            )

        expected_settings = {
            "whisperModel":
                args.whisper_model,
            "pauseSeconds": args.pause,
            "maxSegmentAttempts":
                args.max_segment_attempts,
        }

        if report.get("settings") != expected_settings:
            raise RuntimeError(
                "Resume settings do not match the original run."
            )

        report.setdefault(
            "resumeHistory",
            [],
        ).append(
            {
                "resumedUtc": resumed_utc,
                "previousStatus":
                    report.get("status"),
                "previousFailedStage":
                    report.get("failedStage"),
                "previousVerificationAttempts":
                    report.get(
                        "segmentVerificationAttempts",
                        [],
                    ),
            }
        )

        report["resumeCount"] = (
            report.get("resumeCount", 0) + 1
        )
        report["lastResumedUtc"] = resumed_utc
        report["completedUtc"] = None
        report["status"] = "running"
        report["failedStage"] = None
        report["error"] = None
        report["segmentVerificationAttempts"] = []
        report["masteredAudioPath"] = None
        report.setdefault(
            "stageCheckpoints",
            {},
        )
    else:
        report = {
            "schemaVersion": 1,
            "runId": run_directory.name,
            "bookPath": str(book),
            "voiceLibraryPath":
                str(voice_library),
            "chapterId": args.chapter,
            "startedUtc": resumed_utc,
            "completedUtc": None,
            "status": "running",
            "failedStage": None,
            "settings": {
                "whisperModel":
                    args.whisper_model,
                "pauseSeconds": args.pause,
                "maxSegmentAttempts":
                    args.max_segment_attempts,
            },
            "segmentVerificationAttempts": [],
            "stageCheckpoints": {},
            "resumeCount": 0,
            "resumeHistory": [],
            "lastResumedUtc": None,
            "chapterDirectory":
                str(chapter_directory),
            "masteredAudioPath": None,
        }

    write_run_manifest(
        run_manifest_path,
        report,
    )

    current_stage = "initialization"
    synthesis_worker = None

    try:
        current_stage = "audio text preparation"
        prepare_chapter(book, args.chapter, args.audio_preparation_model or args.rewrite_model, args.ollama_url)
        current_stage = "synthesis"

        synthesis_worker = PersistentSynthesisWorker(
            tools_directory /
            "synthesize_chapter_probe.py"
        )

        synthesis_arguments = [
            sys.executable,
            str(
                tools_directory /
                "synthesize_chapter_probe.py"
            ),
            str(book),
            str(voice_library),
            "--chapter",
            args.chapter,
            "--run-directory",
            str(run_directory),
        ]

        if is_resume:
            synthesis_arguments.append(
                "--resume"
            )

        synthesis_worker.run(
            (
                "Resume synthesis"
                if is_resume
                else "Synthesis"
            ),
            synthesis_arguments,
        )

        chapter_manifest = (
            chapter_directory /
            "chapter.json"
        )

        verification_path = (
            chapter_directory /
            "verification.json"
        )

        segment_inputs = [
            chapter_manifest,
            tools_directory /
            "verify_chapter_probe.py",
            tools_directory / "produce_chapter.py",
            tools_directory / "secondary_transcription.py",
            tools_directory / "chat_label_recovery.py",
            tools_directory / "verify_voice_library.py",
            tools_directory / "pronunciations.py",
            tools_directory / "context_pronunciations.py",
            tools_directory / "spoken_text.py",
            *segment_audio_paths(
                chapter_manifest
            ),
        ]

        current_stage = "segment verification"

        run_checkpointed_stage(
            "Segment verification",
            "segmentVerification",
            report,
            run_manifest_path,
            segment_inputs,
            {
                "whisperModel":
                    args.whisper_model,
                "maxSegmentAttempts":
                    args.max_segment_attempts,
                "verificationPolicyVersion": 4,
                "allowReview": args.allow_review,
            },
            [verification_path],
            lambda: verify_segments_with_retries(
                tools_directory,
                book,
                voice_library,
                args.chapter,
                run_directory,
                chapter_manifest,
                args.whisper_model,
                args.max_segment_attempts,
                report[
                    "segmentVerificationAttempts"
                ],
                args.rewrite_model,
                args.ollama_url,
                args.max_fragment_rewrites,
                synthesis_runner=synthesis_worker.run,
                allow_review=args.allow_review,
            ),
        )

        synthesis_worker.close()
        synthesis_worker = None

        raw_chapter_audio = (
            chapter_directory /
            f"{args.chapter}.wav"
        )

        assembly_manifest = (
            chapter_directory /
            "assembly.json"
        )

        assembly_inputs = [
            chapter_manifest,
            verification_path,
            tools_directory /
            "assemble_chapter_probe.py",
            *segment_audio_paths(
                chapter_manifest
            ),
        ]

        current_stage = "assembly"

        run_checkpointed_stage(
            "Assembly",
            "assembly",
            report,
            run_manifest_path,
            assembly_inputs,
            {
                "pauseSeconds": args.pause,
                "allowReview": args.allow_review,
            },
            [
                assembly_manifest,
                raw_chapter_audio,
            ],
            lambda: assemble_verified_audio(tools_directory, chapter_manifest, args.pause, args.allow_review),
        )

        assembly_verification_path = (
            chapter_directory /
            "assembly-verification.json"
        )

        current_stage = "assembly verification"

        assembly_integrity_inputs = [chapter_manifest, chapter_directory / "verification.json"]
        assembly_integrity_inputs.extend(
            Path(segment["audioPath"]) for segment in load_json(chapter_manifest)["segments"]
        )
        assembly_integrity_inputs.extend(
            Path(chunk["audioPath"]) for segment in load_json(chapter_manifest)["segments"]
            for chunk in segment.get("chunks", [])
        )

        run_checkpointed_stage(
            "Assembly verification",
            "assemblyVerification",
            report,
            run_manifest_path,
            [
                assembly_manifest,
                raw_chapter_audio,
                tools_directory /
                "verify_assembly_probe.py",
                *assembly_integrity_inputs,
            ],
            {
                "whisperModel":
                    args.whisper_model,
                "allowReview": args.allow_review,
            },
            [assembly_verification_path],
            lambda: run_stage(
                "Assembly verification",
                [
                    sys.executable,
                    str(
                        tools_directory /
                        "verify_assembly_probe.py"
                    ),
                    str(assembly_manifest),
                    "--model",
                    args.whisper_model,
                    *(["--allow-review"] if args.allow_review else []),
                ],
            ),
        )

        mastered_audio = (
            chapter_directory /
            f"{args.chapter}.mastered.wav"
        )

        mastering_report_path = (
            chapter_directory /
            "mastering.json"
        )

        current_stage = "mastering"

        run_checkpointed_stage(
            "Mastering",
            "mastering",
            report,
            run_manifest_path,
            [
                assembly_manifest,
                raw_chapter_audio,
                tools_directory /
                "master_chapter_probe.py",
            ],
            {},
            [
                mastering_report_path,
                mastered_audio,
            ],
            lambda: run_stage(
                "Mastering",
                [
                    sys.executable,
                    str(
                        tools_directory /
                        "master_chapter_probe.py"
                    ),
                    str(assembly_manifest),
                ],
            ),
        )

        mastered_verification_path = (
            chapter_directory /
            "mastered-verification.json"
        )

        current_stage = "mastered verification"

        mastered_comparison_fingerprint = pronunciation_fingerprint(
            " ".join(segment["sourceText"] for segment in load_json(chapter_manifest)["segments"]),
            load_pronunciations(book),
        )

        run_checkpointed_stage(
            "Mastered verification",
            "masteredVerification",
            report,
            run_manifest_path,
            [
                assembly_manifest,
                mastered_audio,
                tools_directory /
                "verify_assembly_probe.py",
                chapter_manifest,
                tools_directory / "pronunciations.py",
                tools_directory / "spoken_text.py",
            ],
            {
                "whisperModel":
                    args.whisper_model,
                "allowReview": args.allow_review,
                "pronunciationFingerprint": mastered_comparison_fingerprint,
            },
            [mastered_verification_path],
            lambda: run_stage(
                "Mastered verification",
                [
                    sys.executable,
                    str(
                        tools_directory /
                        "verify_assembly_probe.py"
                    ),
                    str(assembly_manifest),
                    "--audio",
                    str(mastered_audio),
                    "--report-name",
                    "mastered-verification.json",
                    "--model",
                    args.whisper_model,
                ],
                allowed_exit_codes=(0, 2) if args.allow_review else (0,),
            ),
        )
        mastered_result = load_json(mastered_verification_path)
        if mastered_result["status"] == "fail" and not args.allow_review:
            raise RuntimeError("Mastered audio verification failed; final audio is incomplete or incorrect.")
        report["reviewRequired"] = (mastered_result["status"] != "pass" or any(
            item["status"] != "pass" for item in load_json(verification_path).get("chunks", load_json(verification_path)["segments"])
        ))
        dialogue_review_path = book / "production/dialogue-review" / f"{args.chapter}.json"
        dialogue_review = load_json(dialogue_review_path) if dialogue_review_path.is_file() else {}
        if dialogue_review.get("sourceSha256") == load_json(chapter_manifest)["sourceSha256"]:
            report["dialogueReview"] = dialogue_review
            report["reviewRequired"] |= dialogue_review.get("reviewRequired", False)
        report["allowReview"] = args.allow_review

        final_audio = (
            book /
            "output" /
            "final" /
            f"{args.chapter}.mp3"
        )

        export_report_path = (
            chapter_directory /
            "chapter-export.json"
        )

        export_audio = mastered_audio
        report["ambienceEnabled"] = not args.no_ambience
        if args.no_ambience:
            report.pop("ambienceAudioPath", None)
            report.pop("ambienceReportPath", None)
        if not args.no_ambience:
            from ambience import add_ambience
            current_stage = "ambience"
            ambience_audio = chapter_directory / f"{args.chapter}.ambience.wav"
            ambience_report = chapter_directory / "ambience.json"
            existing_ambience = load_json(ambience_report) if ambience_report.is_file() else {}
            ambience_assets = [Path(cue["audioPath"]) for cue in existing_ambience.get("cues", [])]
            ambience_base_inputs = [chapter_manifest, assembly_manifest, mastered_audio, mastered_verification_path,
                                    tools_directory / "ambience.py"]
            ambience_inputs = [*ambience_base_inputs, *ambience_assets]

            def render_ambience():
                add_ambience(chapter_manifest, assembly_manifest, mastered_audio,
                             args.rewrite_model, args.ollama_url, args.soundforge_url, args.ambience_gain_db)
                # Include newly downloaded assets in the first checkpoint, so
                # a later resume can reuse it immediately and detect tampering.
                ambience_inputs[:] = [*ambience_base_inputs,
                                      *(Path(cue["audioPath"]) for cue in load_json(ambience_report)["cues"])]

            run_checkpointed_stage(
                "SoundForge ambience", "ambience", report, run_manifest_path,
                ambience_inputs,
                {"version": 1, "soundforgeUrl": args.soundforge_url, "model": args.rewrite_model,
                 "gainDb": args.ambience_gain_db},
                [ambience_report, ambience_audio],
                render_ambience)
            export_audio = ambience_audio
            report["ambienceAudioPath"] = str(ambience_audio)
            report["ambienceReportPath"] = str(ambience_report)

        current_stage = "MP3 export"

        run_checkpointed_stage(
            "MP3 export",
            "mp3Export",
            report,
            run_manifest_path,
            [
                resolve_book_manifest(book),
                export_audio,
                mastered_verification_path,
                tools_directory /
                "export_chapter.py",
            ],
            {
                "bitrate": "128k",
                "ambienceEnabled": not args.no_ambience,
            },
            [
                export_report_path,
                final_audio,
            ],
            lambda: run_stage(
                "MP3 export",
                [
                    sys.executable,
                    str(
                        tools_directory /
                        "export_chapter.py"
                    ),
                    str(book),
                    args.chapter,
                    str(export_audio),
                    "--bitrate",
                    "128k",
                ],
            ),
        )

        report["status"] = "completed"
        report["completedUtc"] = (
            datetime.now(
                timezone.utc
            ).isoformat()
        )
        report["masteredAudioPath"] = (
            str(mastered_audio)
        )
        report["finalAudioPath"] = (
            str(final_audio)
        )

        write_run_manifest(
            run_manifest_path,
            report,
        )

        print()
        print("===== Production complete =====")
        print(f"Run:     {run_directory}")
        print(f"Chapter: {args.chapter}")
        print(f"Master:  {mastered_audio}")
        if not args.no_ambience:
            print(f"Mix:     {export_audio}")
        print(f"MP3:     {final_audio}")

        return 0
    except Exception as error:
        if synthesis_worker is not None:
            synthesis_worker.close()

        report["status"] = "failed"
        report["failedStage"] = current_stage
        report["error"] = str(error)
        report["completedUtc"] = (
            datetime.now(
                timezone.utc
            ).isoformat()
        )

        write_run_manifest(
            run_manifest_path,
            report,
        )

        print()
        print(
            f"Production failed during "
            f"{current_stage}.",
            file=sys.stderr,
        )

        raise


if __name__ == "__main__":
    raise SystemExit(main())
