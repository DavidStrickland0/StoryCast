from __future__ import annotations

import argparse
import json
import uuid
from datetime import datetime, timezone
from pathlib import Path

import torch
import torchaudio

from chatterbox.tts import ChatterboxTTS


EXAGGERATION = 0.5
CFG_WEIGHT = 0.5
TEMPERATURE = 0.7


def load_json(path: Path) -> dict:
    with path.open(
        "r",
        encoding="utf-8-sig",
    ) as stream:
        return json.load(stream)


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


def main() -> int:
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
        "--segment-index",
        type=int,
        action="append",
        default=None,
    )
    parser.add_argument(
        "--attempt",
        type=int,
        default=0,
    )

    args = parser.parse_args()

    book = args.book.resolve()
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
            assignment["voiceId"]
        for assignment in casting["assignments"]
    }

    voice_samples = load_voice_samples(
        library
    )

    segments = artifact["script"]["segments"]

    if not segments:
        raise RuntimeError(
            f"Chapter contains no segments: {args.chapter}"
        )

    selected_indexes = set(
        args.segment_index or []
    )
    is_retry = bool(selected_indexes)

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

        if is_retry:
            if not run_directory.is_dir():
                raise FileNotFoundError(
                    f"Retry run directory was not found: "
                    f"{run_directory}"
                )
        else:
            run_directory.mkdir(
                parents=True,
                exist_ok=False,
            )

    chapter_directory = (
        run_directory / args.chapter
    )

    chapter_directory.mkdir(
        exist_ok=is_retry,
    )

    device = (
        "cuda"
        if torch.cuda.is_available()
        else "cpu"
    )

    print(f"Run:      {run_directory}", flush=True)
    print(f"Chapter:  {args.chapter}", flush=True)
    print(f"Segments: {len(segments)}", flush=True)
    print(f"Attempt:  {args.attempt}", flush=True)
    print(f"Device:   {device}", flush=True)
    print()
    print("Loading Chatterbox...", flush=True)

    model = ChatterboxTTS.from_pretrained(
        device=device
    )

    generated_segments: list[dict] = []

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

        voice_id = assignments.get(
            speaker_id.lower()
        )

        if voice_id is None:
            raise RuntimeError(
                f"No casting assignment exists for "
                f"speaker '{speaker_id}'."
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
            segment_index +
            args.attempt * 100000
        )

        torch.manual_seed(seed)

        if torch.cuda.is_available():
            torch.cuda.manual_seed_all(seed)

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
            f"{speaker_id} | {voice_id}",
            flush=True,
        )

        with torch.inference_mode():
            audio = model.generate(
                source_text,
                audio_prompt_path=str(voice_sample),
                exaggeration=EXAGGERATION,
                cfg_weight=CFG_WEIGHT,
                temperature=TEMPERATURE,
            )

        audio = audio.detach().to(
            device="cpu",
            dtype=torch.float32,
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

        generated_segments.append(
            {
                "index": segment_index,
                "kind": segment["kind"],
                "speakerId": speaker_id,
                "voiceId": voice_id,
                "sourceText": source_text,
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
            }
        )

        del audio

        if torch.cuda.is_available():
            torch.cuda.empty_cache()

    manifest = {
        "schemaVersion": 1,
        "chapterId": args.chapter,
        "sourceSha256": artifact["sourceSha256"],
        "preparationVersion":
            artifact["preparationVersion"],
        "settings": {
            "exaggeration": EXAGGERATION,
            "cfgWeight": CFG_WEIGHT,
            "temperature": TEMPERATURE,
        },
        "segments": generated_segments,
    }

    manifest_path = (
        chapter_directory / "chapter.json"
    )

    if is_retry:
        if not manifest_path.is_file():
            raise FileNotFoundError(
                f"Existing chapter manifest was not found: "
                f"{manifest_path}"
            )

        existing_manifest = load_json(
            manifest_path
        )

        replacements = {
            segment["index"]: segment
            for segment in generated_segments
        }

        existing_manifest["segments"] = [
            replacements.get(
                segment["index"],
                segment,
            )
            for segment in existing_manifest["segments"]
        ]

        manifest = existing_manifest

    temporary_manifest_path = (
        chapter_directory /
        ".chapter.json.tmp"
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
        manifest_path
    )

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


if __name__ == "__main__":
    raise SystemExit(main())
