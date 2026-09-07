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

    if args.run_directory is None:
        run_directory = create_run_directory(
            book
        )
    else:
        run_directory = (
            args.run_directory.resolve()
        )
        run_directory.mkdir(
            parents=True,
            exist_ok=False,
        )

    chapter_directory = (
        run_directory / args.chapter
    )

    chapter_directory.mkdir()

    device = (
        "cuda"
        if torch.cuda.is_available()
        else "cpu"
    )

    print(f"Run:      {run_directory}", flush=True)
    print(f"Chapter:  {args.chapter}", flush=True)
    print(f"Segments: {len(segments)}", flush=True)
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

        seed = 10000 + segment_index

        torch.manual_seed(seed)

        if torch.cuda.is_available():
            torch.cuda.manual_seed_all(seed)

        output_path = (
            chapter_directory /
            f"segment-{segment_index:04d}.wav"
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
            str(output_path),
            audio,
            model.sr,
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

    with manifest_path.open(
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

    total_duration = sum(
        segment["durationSeconds"]
        for segment in generated_segments
    )

    print()
    print("Chapter synthesis complete.")
    print(f"Segments: {len(generated_segments)}")
    print(f"Duration: {total_duration:.2f} seconds")
    print(f"Manifest: {manifest_path}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
