from __future__ import annotations

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
from prosody import resolve_segment_synthesis_settings
from pronunciations import (
    apply_pronunciations,
    load_pronunciations,
    pronunciation_fingerprint,
)
from spoken_text import normalize_spoken_text


DEFAULT_NARRATOR_EXAGGERATION = 0.4
DEFAULT_CHARACTER_EXAGGERATION = 0.65
DEFAULT_CFG_WEIGHT = 0.5
DEFAULT_TEMPERATURE = 0.7
MAX_SYNTHESIS_CHARACTERS = 280
CHUNK_PAUSE_SECONDS = 0.12
HEADING_PAUSE_SECONDS = 1.25
WORKER_RESULT_PREFIX = "__STORYCAST_SYNTHESIS_RESULT__"
_CACHED_MODEL = None


def split_synthesis_text(
    text: str,
    max_characters: int = MAX_SYNTHESIS_CHARACTERS,
) -> list[str]:
    if max_characters < 1:
        raise ValueError(
            "Maximum synthesis characters must be positive."
        )

    if len(text) <= max_characters:
        return [text.strip()]

    chunks: list[str] = []
    cursor = 0

    while cursor < len(text):
        remaining = text[cursor:]

        if len(remaining) <= max_characters:
            chunk = remaining
            cursor = len(text)
        else:
            window = text[
                cursor:cursor + max_characters
            ]

            sentence_matches = list(
                re.finditer(
                    r'[.!?]["”’]?\s+',
                    window,
                )
            )

            minimum_boundary = max_characters // 2

            sentence_end = next(
                (
                    match.end()
                    for match in reversed(
                        sentence_matches
                    )
                    if match.end() >= minimum_boundary
                ),
                None,
            )

            if sentence_end is not None:
                end = cursor + sentence_end
            else:
                whitespace = window.rfind(
                    " ",
                    minimum_boundary,
                )

                end = (
                    cursor + whitespace + 1
                    if whitespace >= minimum_boundary
                    else cursor + max_characters
                )

            chunk = text[cursor:end]
            cursor = end

        normalized = chunk.strip()

        if normalized:
            chunks.append(normalized)

    return chunks



def load_markdown_headings(
    book: Path,
    chapter_id: str,
) -> set[str]:
    """Load spoken headings from the chapter Markdown."""
    manifest = load_json(book / "book.json")
    chapter_path: Path | None = None

    for chapter_value in manifest["chapters"]:
        candidate = (book / str(chapter_value)).resolve()

        if candidate.stem.lower() == chapter_id.lower():
            chapter_path = candidate
            break

    if chapter_path is None or not chapter_path.is_file():
        return set()

    headings: set[str] = set()

    with chapter_path.open(
        "r",
        encoding="utf-8-sig",
    ) as stream:
        for line in stream:
            match = re.match(
                r"^[ \t]{0,3}#{1,6}[ \t]+"
                r"(.+?)[ \t]*#*[ \t]*$",
                line.rstrip("\r\n"),
            )

            if match:
                headings.add(match.group(1).strip())

    return headings


def split_synthesis_units(
    text: str,
    headings: set[str],
) -> list[tuple[str, float]]:
    """Split text and assign longer pauses after headings."""
    units: list[tuple[str, float]] = []
    ordinary_lines: list[str] = []

    def flush_ordinary() -> None:
        ordinary_text = "\n".join(ordinary_lines).strip()
        ordinary_lines.clear()

        if not ordinary_text:
            return

        for chunk in split_synthesis_text(ordinary_text):
            units.append((chunk, CHUNK_PAUSE_SECONDS))

    for line in text.splitlines():
        stripped = line.strip()

        if stripped and stripped in headings:
            flush_ordinary()
            units.append((stripped, HEADING_PAUSE_SECONDS))
        else:
            ordinary_lines.append(line)

    flush_ordinary()
    return units


def resolve_synthesis_settings(
    assignment: dict,
    speaker_id: str,
    source_text: str,
    delivery: str = "",
) -> dict[str, float | str]:
    """Resolve prose-aware settings from the casting baseline."""
    return resolve_segment_synthesis_settings(
        assignment,
        speaker_id,
        source_text,
        delivery,
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


def parse_chunk_selector(value: str) -> tuple[int, int]:
    try:
        segment_text, chunk_text = value.split(":", 1)
        segment_index = int(segment_text)
        chunk_index = int(chunk_text)
    except (ValueError, TypeError) as exception:
        raise argparse.ArgumentTypeError(
            "Chunk selectors must use <segment-index>:<chunk-index>."
        ) from exception
    if segment_index < 0 or chunk_index < 0:
        raise argparse.ArgumentTypeError(
            "Chunk selector indexes must be nonnegative."
        )
    return segment_index, chunk_index


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

    if not segments:
        raise RuntimeError(
            f"Chapter contains no segments: {args.chapter}"
        )

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
                )
                if casting_assignment is not None
                else None
            )
            existing = existing_by_index.get(
                segment_index
            )
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
                    split_synthesis_units(
                        segment["sourceText"],
                        markdown_headings,
                    ),
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
                )

        voice_sample = voice_samples.get(
            voice_id.lower()
        )

        if voice_sample is None:
            raise RuntimeError(
                f"Voice sample was not found for "
                f"voice '{voice_id}'."
            )

        seed = (
            10000 +
            segment_index * 1000 +
            args.attempt * 100000
        )

        synthesis_units = split_synthesis_units(
            source_text,
            markdown_headings,
        )

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

        if any(
            not selected_chunks or
            (segment_index, chunk_index) in selected_chunks
            for chunk_index in range(len(synthesis_units))
        ):
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
            regenerate_chunk = (
                not selected_chunks or
                (segment_index, chunk_index) in selected_chunks
            )
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
                        chunk_text,
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
