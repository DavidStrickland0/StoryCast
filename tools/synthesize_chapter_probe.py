from __future__ import annotations
from chat_label_recovery import apply_chunk_speech_overrides

import argparse
import gc
import hashlib
import json
import re
import sys
import traceback
import uuid
from datetime import datetime, timezone
from pathlib import Path

import torch
import torchaudio

from audio_postprocessing import polish_audio
from book_manifest import resolve_book_manifest
from heading_pauses import load_markdown_headings, split_synthesis_units, CHUNK_PAUSE_SECONDS, HEADING_PAUSE_SECONDS
from prosody import resolve_segment_synthesis_settings
from delivery_planning import load_delivery, reference_settings
from pronunciations import (
    apply_pronunciations,
    load_pronunciations,
    pronunciation_fingerprint,
)
from spoken_text import normalize_spoken_text, has_spoken_content
from prepare_audio_text import load_prepared
from context_pronunciations import load_context_plan, render_units, context_fingerprint, speech_base
from synthesis_chunking import ends_at_sentence_boundary, split_synthesis_text, parse_chunk_selector
from synthesis_chunking import MAX_SYNTHESIS_CHARACTERS, split_chat_message


DEFAULT_NARRATOR_EXAGGERATION = 0.4
DEFAULT_CHARACTER_EXAGGERATION = 0.65
DEFAULT_CFG_WEIGHT = 0.5
DEFAULT_TEMPERATURE = 0.7
WORKER_RESULT_PREFIX = "__STORYCAST_SYNTHESIS_RESULT__"
_CACHED_MODEL = None


def prepare_synthesis_text(text: str) -> str:
    """Stabilize isolated one-word input without changing source text."""
    words = re.findall(
        r"[A-Za-z0-9]+(?:'[A-Za-z0-9]+)?",
        text,
    )

    if len(words) == 1:
        return f"{words[0]}."

    return text

def resolve_synthesis_settings(
    assignment: dict,
    speaker_id: str,
    source_text: str,
    delivery: str = "",
    performance: dict | None = None,
) -> dict[str, float | str]:
    """Resolve prose-aware settings from the casting baseline."""
    return resolve_segment_synthesis_settings(
        assignment,
        speaker_id,
        source_text,
        delivery,
        performance,
    )


def load_json(path: Path) -> dict:
    with path.open(
        "r",
        encoding="utf-8-sig",
    ) as stream:
        return json.load(stream)


def write_json_atomic(
    path: Path,
    value: dict,
) -> None:
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


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def resume_chunk_is_reusable(chunk, text, pause, audio_path):
    return bool(chunk and chunk.get("sourceText") == text and
                chunk.get("pauseAfterSeconds", 0) == pause and audio_path.is_file() and
                chunk.get("sha256") and file_sha256(audio_path) == chunk["sha256"])


def chunks_are_reusable(
    segment: dict,
    synthesis_units: list[tuple[str, float]],
    chapter_directory: Path,
) -> bool:
    chunks = segment.get("chunks", [])
    if len(chunks) != len(synthesis_units):
        return False
    for chunk_index, (text, pause) in enumerate(synthesis_units):
        chunk = chunks[chunk_index]
        path = (
            chapter_directory /
            f"segment-{segment['index']:04d}-chunk-{chunk_index:04d}.wav"
        ).resolve()
        if (
            chunk.get("index") != chunk_index or
            chunk.get("sourceText") != text or
            chunk.get("pauseAfterSeconds") != pause or
            Path(chunk.get("audioPath", "")).resolve() != path or
            not path.is_file() or
            chunk.get("sha256") != file_sha256(path)
        ):
            return False
    return True


def load_voice_samples(
    library: Path,
) -> dict[str, Path]:
    samples: dict[str, Path] = {}

    for manifest_path in library.glob(
        "*/voice.json"
    ):
        manifest = load_json(manifest_path)

        voice_id = manifest["id"].lower()

        if voice_id in samples:
            raise RuntimeError(
                f"Duplicate voice ID: {manifest['id']}"
            )

        sample_path = (
            manifest_path.parent /
            manifest["sample"]
        ).resolve()

        if not sample_path.is_file():
            raise FileNotFoundError(
                f"Voice sample was not found: {sample_path}"
            )

        samples[voice_id] = sample_path

    return samples


def create_run_directory(book: Path) -> Path:
    timestamp = datetime.now(
        timezone.utc
    ).strftime("%Y%m%d-%H%M%S-%f")[:-3]

    run_id = (
        f"{timestamp}-chapter-{uuid.uuid4().hex[:8]}"
    )

    run_directory = (
        book / "output" / run_id
    ).resolve()

    run_directory.mkdir(
        parents=True,
        exist_ok=False,
    )

    return run_directory


def move_model(model, device: str) -> None:
    model.t3.to(device)
    model.s3gen.to(device)
    model.ve.to(device)

    if model.conds is not None:
        model.conds = model.conds.to(device)

    model.device = device


def park_cached_model() -> None:
    if _CACHED_MODEL is None:
        return

    move_model(_CACHED_MODEL, "cpu")
    gc.collect()

    if torch.cuda.is_available():
        torch.cuda.empty_cache()


def main(
    arguments: list[str] | None = None,
) -> int:
    parser = argparse.ArgumentParser(
        description="Synthesize one complete StoryCast chapter."
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
        default="chapter-001",
    )
    parser.add_argument(
        "--run-directory",
        type=Path,
        default=None,
    )

    parser.add_argument(
        "--resume",
        action="store_true",
    )
    parser.add_argument(
        "--segment-index",
        type=int,
        action="append",
        default=None,
    )
    parser.add_argument(
        "--chunk",
        type=parse_chunk_selector,
        action="append",
        default=None,
        help="Regenerate one chunk as <segment-index>:<chunk-index>.",
    )
    parser.add_argument(
        "--attempt",
        type=int,
        default=0,
    )

    args = parser.parse_args(arguments)

    book = args.book.resolve()

    pronunciations = load_pronunciations(book)
    library = args.voice_library.resolve()

    casting = load_json(
        book / "production" / "casting.json"
    )

    artifact = load_json(
        book /
        "production" /
        "scripts" /
        f"{args.chapter}.json"
    )

    assignments = {
        assignment["characterId"].lower():
            assignment
        for assignment in casting["assignments"]
    }

    voice_samples = load_voice_samples(
        library
    )

    markdown_headings = load_markdown_headings(
        book,
        args.chapter,
    )

    segments = artifact["script"]["segments"]
    performances = load_delivery(book, args.chapter, segments)
    segments = [dict(segment, performance=performances.get(segment["index"])) for segment in segments]
    invalid_dialogue = [segment["index"] for segment in segments
                        if segment.get("kind") == "dialogue" and not has_spoken_content(segment["sourceText"])]
    if invalid_dialogue:
        raise ValueError(f"Invalid empty dialogue segments {invalid_dialogue}; regenerate dialogue attribution before synthesis.")
    prepared = load_prepared(book, args.chapter, artifact)
    segments = [dict(segment, spokenText=prepared.get(segment["index"], segment.get("spokenText", segment["sourceText"]))) for segment in segments]
    context_plans = load_context_plan(book, args.chapter)
    segments = [dict(segment, contextPronunciations=context_plans.get(str(segment["index"]), {})) for segment in segments]

    if not segments:
        raise RuntimeError(
            f"Chapter contains no segments: {args.chapter}"
        )

    whole_segment_indexes = set(args.segment_index or [])
    selected_indexes = set(
        args.segment_index or []
    )
    selected_chunks = set(args.chunk or [])
    selected_indexes.update(
        segment_index
        for segment_index, _ in selected_chunks
    )
    is_retry = bool(selected_indexes or selected_chunks)
    is_resume = args.resume

    if is_retry and is_resume:
        raise RuntimeError(
            "--resume cannot be combined with --segment-index."
        )

    if is_resume and args.run_directory is None:
        raise RuntimeError(
            "Resume synthesis requires --run-directory."
        )

    if is_resume and args.attempt != 0:
        raise RuntimeError(
            "Resume synthesis requires --attempt 0."
        )

    if is_retry and args.run_directory is None:
        raise RuntimeError(
            "Selective synthesis requires --run-directory."
        )

    if is_retry and args.attempt < 1:
        raise RuntimeError(
            "Selective synthesis requires --attempt of at least 1."
        )

    available_indexes = {
        segment["index"]
        for segment in segments
    }
    unknown_indexes = (
        selected_indexes - available_indexes
    )

    if unknown_indexes:
        raise RuntimeError(
            f"Unknown segment indexes: {sorted(unknown_indexes)}"
        )

    if is_retry:
        segments = [
            segment
            for segment in segments
            if segment["index"] in selected_indexes
        ]

    if args.run_directory is None:
        run_directory = create_run_directory(
            book
        )
    else:
        run_directory = (
            args.run_directory.resolve()
        )

        if is_retry or is_resume:
            if not run_directory.is_dir():
                raise FileNotFoundError(
                    f"Retry run directory was not found: "
                    f"{run_directory}"
                )
        else:
            run_directory.mkdir(
                parents=True,
                exist_ok=True,
            )

    chapter_directory = (
        run_directory / args.chapter
    )

    chapter_directory.mkdir(
        exist_ok=is_retry or is_resume,
    )

    manifest_path = (
        chapter_directory /
        "chapter.json"
    )

    manifest = {
        "schemaVersion": 1,
        "chapterId": args.chapter,
        "sourceSha256": artifact["sourceSha256"],
        "preparationVersion":
            artifact["preparationVersion"],
        "settings": {
            "source": "casting",
        },
        "segments": [],
    }

    if is_retry:
        if not manifest_path.is_file():
            raise FileNotFoundError(
                f"Existing chapter checkpoint was not found: "
                f"{manifest_path}"
            )

        manifest = load_json(
            manifest_path
        )

        chunks_by_segment = {
            segment["index"]: {
                chunk["index"]
                for chunk in segment.get("chunks", [])
            }
            for segment in manifest.get("segments", [])
        }
        unknown_chunks = [
            selector
            for selector in selected_chunks
            if selector[1] not in chunks_by_segment.get(selector[0], set())
        ]
        if unknown_chunks:
            raise RuntimeError(
                f"Unknown chunk selectors: {sorted(unknown_chunks)}"
            )
    elif is_resume and manifest_path.is_file():
        manifest = load_json(
            manifest_path
        )
    else:
        write_json_atomic(
            manifest_path,
            manifest,
        )

    if is_retry or is_resume:
        if (
            manifest.get("chapterId") !=
                args.chapter or
            manifest.get("sourceSha256") !=
                artifact["sourceSha256"] or
            manifest.get("preparationVersion") !=
                artifact["preparationVersion"]
        ):
            raise RuntimeError(
                "Existing chapter checkpoint does not match "
                "the current production script."
            )

    if is_retry or is_resume:
        checkpoint_segments = {item["index"]: item for item in manifest["segments"]}
        segments = [dict(segment) for segment in segments]
        for segment in segments:
            checkpoint = checkpoint_segments.get(segment["index"], {})
            for key in ("spokenText", "fragmentRewrite", "fragmentRewrites", "fragmentRewriteFailures", "synthesisMaxCharacters"):
                if key in checkpoint:
                    segment[key] = checkpoint[key]

    reused_segments = 0

    if is_resume:
        existing_by_index = {
            segment["index"]: segment
            for segment in manifest["segments"]
        }

        remaining_segments: list[dict] = []

        for segment in segments:
            segment_index = segment["index"]
            speaker_id = segment["speakerId"]
            casting_assignment = assignments.get(
                speaker_id.lower()
            )
            voice_id = (
                casting_assignment["voiceId"]
                if casting_assignment is not None
                else None
            )
            synthesis_settings = (
                resolve_synthesis_settings(
                    casting_assignment,
                    speaker_id,
                    segment["sourceText"],
                    segment.get("delivery", ""),
                    segment.get("performance"),
                )
                if casting_assignment is not None
                else None
            )
            existing = existing_by_index.get(
                segment_index
            )
            if synthesis_settings is not None and voice_id.lower() in voice_samples:
                _, synthesis_settings = reference_settings(synthesis_settings, voice_samples[voice_id.lower()], voice_id, segment.get("delivery", ""))
            expected_audio_path = (
                chapter_directory /
                f"segment-{segment_index:04d}.wav"
            ).resolve()

            segment_pronunciation_fingerprint = (

                pronunciation_fingerprint(

                    segment["sourceText"],

                    pronunciations,

                )

            )


            reusable = (
                existing is not None and
                existing.get("contextPronunciationFingerprint", "") == context_fingerprint(segment.get("contextPronunciations")) and
                voice_id is not None and
                existing.get("kind") ==
                    segment["kind"] and
                existing.get("speakerId") ==
                    speaker_id and
                existing.get("voiceId") ==
                    voice_id and
                existing.get("synthesis") ==
                    synthesis_settings and
                existing.get(
                    "pronunciationFingerprint",
                    pronunciation_fingerprint(
                        segment["sourceText"],
                        [],
                    ),
                ) ==
                    segment_pronunciation_fingerprint and
                existing.get("sourceText") ==
                    segment["sourceText"] and
                chunks_are_reusable(
                    existing,
                    apply_chunk_speech_overrides(split_synthesis_units(
                        prepare_synthesis_text(normalize_spoken_text(apply_pronunciations(
                            segment.get("spokenText", segment["sourceText"]), pronunciations))),
                        markdown_headings,
                        max_characters=segment.get("synthesisMaxCharacters", MAX_SYNTHESIS_CHARACTERS),
                    ), existing),
                    chapter_directory,
                ) and
                Path(
                    existing.get(
                        "audioPath",
                        "")
                ).resolve() == expected_audio_path and
                expected_audio_path.is_file()
            )

            if reusable:
                try:
                    audio_info = torchaudio.info(
                        str(expected_audio_path)
                    )

                    segment_pronunciation_fingerprint = (

                        pronunciation_fingerprint(

                            segment["sourceText"],

                            pronunciations,

                        )

                    )


                    reusable = (
                        audio_info.num_frames > 0 and
                        audio_info.sample_rate ==
                            existing.get("sampleRate")
                    )
                except Exception:
                    reusable = False

            if reusable:
                reused_segments += 1
            else:
                remaining_segments.append(
                    segment
                )

        segments = remaining_segments

    device = (
        "cuda"
        if torch.cuda.is_available()
        else "cpu"
    )

    print(f"Run:      {run_directory}", flush=True)
    print(f"Chapter:  {args.chapter}", flush=True)
    print(f"Segments: {len(segments)}", flush=True)
    print(f"Attempt:  {args.attempt}", flush=True)
    print(f"Reused:   {reused_segments}", flush=True)
    print(f"Device:   {device}", flush=True)
    global _CACHED_MODEL
    model = None

    if segments:
        if _CACHED_MODEL is None:
            from cache_tokenizer import configure_tokenizer_cache
            configure_tokenizer_cache()
            from chatterbox.mtl_tts import ChatterboxMultilingualTTS

            print()
            print(
                "Loading Chatterbox...",
                flush=True,
            )

            _CACHED_MODEL = ChatterboxMultilingualTTS.from_pretrained(
                device=device,
                t3_model="v3",
            )
        else:
            print()
            print(
                "Reusing Chatterbox model...",
                flush=True,
            )
            move_model(_CACHED_MODEL, device)

        model = _CACHED_MODEL

    generated_segments: list[dict] = []

    if segments and model is None:
        raise RuntimeError(
            "Chatterbox failed to initialize."
        )

    for position, segment in enumerate(
        segments,
        start=1,
    ):
        segment_index = segment["index"]
        speaker_id = segment["speakerId"]
        source_text = segment["sourceText"]
        spoken_text = segment.get(
            "spokenText",
            source_text,
        )

        if not source_text.strip():
            raise RuntimeError(
                f"Segment {segment_index} contains only whitespace."
            )

        casting_assignment = assignments.get(
            speaker_id.lower()
        )

        if casting_assignment is None:
            raise RuntimeError(
                f"No casting assignment exists for "
                f"speaker '{speaker_id}'."
            )

        voice_id = casting_assignment["voiceId"]
        synthesis_settings = resolve_synthesis_settings(
                    casting_assignment,
                    speaker_id,
                    segment["sourceText"],
                    segment.get("delivery", ""),
                    segment.get("performance"),
                )

        voice_sample = voice_samples.get(
            voice_id.lower()
        )

        if voice_sample is None:
            raise RuntimeError(
                f"Voice sample was not found for "
                f"voice '{voice_id}'."
            )

        voice_sample, synthesis_settings = reference_settings(
            synthesis_settings, voice_sample, voice_id, segment.get("delivery", ""))
        if segment.get("delivery"):
            print(f"Delivery: {segment['delivery']} | {synthesis_settings['referenceMode']} | parameter approximation", flush=True)

        seed = (
            10000 +
            segment_index * 1000 +
            args.attempt * 100000
        )

        synthesis_text = prepare_synthesis_text(
            normalize_spoken_text(apply_pronunciations(spoken_text, pronunciations))
        )

        synthesis_units = split_synthesis_units(
            synthesis_text,
            markdown_headings,
            max_characters=segment.get("synthesisMaxCharacters", MAX_SYNTHESIS_CHARACTERS),
        )
        previous_segment = next((item for item in manifest["segments"] if item["index"] == segment_index), None)
        synthesis_units = apply_chunk_speech_overrides(synthesis_units, previous_segment)
        contextual_plan = segment.get("contextPronunciations", {})
        contextual_units = render_units(synthesis_units, contextual_plan, speech_base(spoken_text, pronunciations), include_decisions=True)

        output_path = (
            chapter_directory /
            f"segment-{segment_index:04d}.wav"
        )

        temporary_output_path = (
            chapter_directory /
            f".segment-{segment_index:04d}.wav.tmp"
        )

        print(
            f"[{position}/{len(segments)}] "
            f"segment {segment_index} | "
            f"{speaker_id} | {voice_id} | "
            f"{len(synthesis_units)} chunks",
            flush=True,
        )

        generated_chunks: list[torch.Tensor] = []
        chunk_records: list[dict] = []
        existing_segment = next(
            (
                item
                for item in manifest["segments"]
                if item["index"] == segment_index
            ),
            None,
        )
        existing_chunks = {
            item["index"]: item
            for item in (
                existing_segment.get("chunks", [])
                if existing_segment is not None
                else []
            )
        }

        resume_context_matches = bool(is_resume and existing_segment and
            existing_segment.get("contextPronunciationFingerprint", "") == context_fingerprint(contextual_plan) and
            existing_segment.get("kind") == segment["kind"] and
            existing_segment.get("speakerId") == speaker_id and
            existing_segment.get("voiceId") == voice_id and
            existing_segment.get("sourceText") == source_text and
            existing_segment.get("spokenText", source_text) == spoken_text and
            existing_segment.get("synthesis") == synthesis_settings and
            existing_segment.get("pronunciationFingerprint") == pronunciation_fingerprint(source_text, pronunciations))
        regenerate_chunks = {}
        for chunk_index, (text, pause) in enumerate(synthesis_units):
            regenerate = not selected_chunks or segment_index in whole_segment_indexes or (segment_index, chunk_index) in selected_chunks
            if existing_segment and existing_segment.get("contextPronunciationFingerprint", "") != context_fingerprint(contextual_plan):
                regenerate = True
            if existing_segment and existing_segment.get("synthesis") != synthesis_settings:
                # Delivery/reference changes invalidate every chunk, including selective retries.
                regenerate = True
            if resume_context_matches:
                regenerate = not resume_chunk_is_reusable(existing_chunks.get(chunk_index), text, pause,
                    chapter_directory / f"segment-{segment_index:04d}-chunk-{chunk_index:04d}.wav")
            regenerate_chunks[chunk_index] = regenerate
        if any(regenerate_chunks.values()):
            model.prepare_conditionals(
                str(voice_sample),
                exaggeration=synthesis_settings["exaggeration"],
            )

        for chunk_index, synthesis_unit in enumerate(
            synthesis_units
        ):
            chunk_text, pause_after_seconds = synthesis_unit
            chunk_seed = seed + chunk_index
            chunk_output_path = (
                chapter_directory /
                f"segment-{segment_index:04d}-chunk-{chunk_index:04d}.wav"
            )
            regenerate_chunk = regenerate_chunks[chunk_index]
            existing_chunk = existing_chunks.get(chunk_index)

            if not regenerate_chunk:
                if (
                    existing_chunk is None or
                    existing_chunk.get("sourceText") != chunk_text or
                    not chunk_output_path.is_file()
                ):
                    raise RuntimeError(
                        f"Chunk {segment_index}:{chunk_index} cannot be reused."
                    )
                chunk_audio, chunk_sample_rate = torchaudio.load(
                    str(chunk_output_path)
                )
                if chunk_sample_rate != model.sr:
                    raise RuntimeError(
                        f"Chunk {segment_index}:{chunk_index} uses an "
                        f"unexpected sample rate."
                    )
                chunk_record = dict(existing_chunk)
                generated_chunks.append(chunk_audio)
                chunk_records.append(chunk_record)
                print(
                    f"  chunk {chunk_index + 1}/{len(synthesis_units)} | "
                    f"reused",
                    flush=True,
                )
            else:
                synthesis_text = normalize_spoken_text(
                    apply_pronunciations(
                        contextual_units[chunk_index][0],
                        pronunciations,
                    )
                )
                print(
                    f"  chunk {chunk_index + 1}/"
                    f"{len(synthesis_units)} | "
                    f"{len(chunk_text)} characters",
                    flush=True,
                )
                torch.manual_seed(chunk_seed)

                if torch.cuda.is_available():
                    torch.cuda.manual_seed_all(chunk_seed)

                with torch.inference_mode():
                    generated_audio = model.generate(
                        synthesis_text,
                        language_id="en",
                        audio_prompt_path=None,
                        exaggeration=synthesis_settings["exaggeration"],
                        cfg_weight=synthesis_settings["cfgWeight"],
                        temperature=synthesis_settings["temperature"],
                    )

                chunk_audio = generated_audio.detach().to(
                    device="cpu",
                    dtype=torch.float32,
                )
                del generated_audio
                chunk_audio, audio_processing = polish_audio(
                    chunk_audio,
                    model.sr,
                )

                chunk_temporary_path = chunk_output_path.with_name(
                    f".{chunk_output_path.name}.tmp"
                )
                torchaudio.save(
                    str(chunk_temporary_path),
                    chunk_audio,
                    model.sr,
                    format="wav",
                )
                chunk_temporary_path.replace(chunk_output_path)
                chunk_record = {
                    "index": chunk_index,
                    "sourceText": chunk_text,
                    "synthesisText": synthesis_text,
                    "contextPronunciations": contextual_units[chunk_index][1],
                    "pauseAfterSeconds": pause_after_seconds,
                    "seed": chunk_seed,
                    "attempt": args.attempt,
                    "sampleRate": model.sr,
                    "durationSeconds": chunk_audio.shape[-1] / model.sr,
                    "audioPath": str(chunk_output_path),
                    "sha256": file_sha256(chunk_output_path),
                    "audioProcessing": audio_processing,
                }
                generated_chunks.append(chunk_audio)
                chunk_records.append(chunk_record)

                if torch.cuda.is_available():
                    torch.cuda.synchronize()
                    torch.cuda.empty_cache()

            if chunk_index + 1 < len(
                synthesis_units
            ):
                pause_frames = int(
                    model.sr *
                    pause_after_seconds
                )

                generated_chunks.append(
                    torch.zeros(
                        (
                            *chunk_audio.shape[:-1],
                            pause_frames,
                        ),
                        dtype=chunk_audio.dtype,
                    )
                )

        audio = torch.cat(
            generated_chunks,
            dim=-1,
        )

        torchaudio.save(
            str(temporary_output_path),
            audio,
            model.sr,
            format="wav",
        )

        temporary_output_path.replace(
            output_path
        )

        duration_seconds = (
            audio.shape[-1] / model.sr
        )

        generated_segment = (
            {
                "index": segment_index,
                "kind": segment["kind"],
                "speakerId": speaker_id,
                "voiceId": voice_id,
                "synthesis": synthesis_settings,
                "sourceText": source_text,
                "contextPronunciations": contextual_plan,
                "contextPronunciationFingerprint": context_fingerprint(contextual_plan),
            "spokenText": spoken_text,
            "fragmentRewrites": segment.get("fragmentRewrites", []),
            "fragmentRewriteFailures": segment.get("fragmentRewriteFailures", []),
            "synthesisMaxCharacters": segment.get("synthesisMaxCharacters", MAX_SYNTHESIS_CHARACTERS),
                "pronunciationFingerprint":
                    pronunciation_fingerprint(
                        source_text,
                        pronunciations,
                    ),
                "delivery": segment.get(
                    "delivery",
                    "",
                ),
                "seed": seed,
                "attempt": args.attempt,
                "sampleRate": model.sr,
                "durationSeconds":
                    duration_seconds,
                "audioPath": str(output_path),
                "sha256": file_sha256(output_path),
                "chunks": chunk_records,
            }
        )

        if previous_segment and previous_segment.get("chunkSpeechOverrides"):
            generated_segment["chunkSpeechOverrides"] = previous_segment["chunkSpeechOverrides"]
        generated_segments.append(
            generated_segment
        )

        manifest["segments"] = [
            existing
            for existing in manifest["segments"]
            if existing["index"] != segment_index
        ]

        manifest["segments"].append(
            generated_segment
        )

        manifest["segments"].sort(
            key=lambda item: item["index"]
        )

        write_json_atomic(
            manifest_path,
            manifest,
        )

        del audio

        if torch.cuda.is_available():
            torch.cuda.empty_cache()

    total_duration = sum(
        segment["durationSeconds"]
        for segment in manifest["segments"]
    )

    print()
    print("Chapter synthesis complete.")
    print(f"Segments: {len(manifest['segments'])}")
    print(f"Duration: {total_duration:.2f} seconds")
    print(f"Manifest: {manifest_path}")

    return 0


def run_persistent_worker() -> int:
    for line in sys.stdin:
        try:
            request = json.loads(line)

            if request.get("command") == "stop":
                park_cached_model()
                return 0

            arguments = request.get("arguments")
            if not isinstance(arguments, list) or not all(
                isinstance(argument, str)
                for argument in arguments
            ):
                raise ValueError(
                    "Persistent synthesis requests require string arguments."
                )

            exit_code = main(arguments)
            park_cached_model()
            response = {
                "ok": True,
                "exitCode": exit_code,
            }
        except Exception as exception:
            traceback.print_exc()
            park_cached_model()
            response = {
                "ok": False,
                "error": str(exception),
            }

        print(
            WORKER_RESULT_PREFIX + json.dumps(response),
            flush=True,
        )

    park_cached_model()
    return 0


if __name__ == "__main__":
    if "--persistent-worker" in sys.argv[1:]:
        raise SystemExit(run_persistent_worker())

    raise SystemExit(main())
