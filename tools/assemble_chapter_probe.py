from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path

import torch
import torchaudio


PAUSE_SECONDS = 0.18


def load_json(path: Path) -> dict:
    with path.open(
        "r",
        encoding="utf-8-sig",
    ) as stream:
        return json.load(stream)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Assemble a verified StoryCast chapter."
    )
    parser.add_argument(
        "chapter_manifest",
        type=Path,
    )
    parser.add_argument(
        "--pause",
        type=float,
        default=PAUSE_SECONDS,
    )

    args = parser.parse_args()

    manifest_path = args.chapter_manifest.resolve()
    chapter_directory = manifest_path.parent

    manifest = load_json(manifest_path)

    verification_path = (
        chapter_directory / "verification.json"
    )

    if not verification_path.is_file():
        raise FileNotFoundError(
            f"Chapter verification was not found: "
            f"{verification_path}"
        )

    verification = load_json(
        verification_path
    )

    verification_by_index = {
        item["segmentIndex"]: item
        for item in verification["segments"]
    }

    segments = sorted(
        manifest["segments"],
        key=lambda item: item["index"],
    )

    if not segments:
        raise RuntimeError(
            "Chapter contains no synthesized segments."
        )

    audio_parts: list[torch.Tensor] = []
    timing: list[dict] = []

    sample_rate: int | None = None
    channels: int | None = None
    cursor_samples = 0

    for position, segment in enumerate(segments):
        segment_index = segment["index"]

        verification_result = (
            verification_by_index.get(
                segment_index
            )
        )

        if verification_result is None:
            raise RuntimeError(
                f"Segment {segment_index} has no "
                f"verification result."
            )

        if verification_result["status"] == "fail":
            raise RuntimeError(
                f"Segment {segment_index} has verification "
                f"status '{verification_result['status']}'."
            )

        audio_path = Path(
            segment["audioPath"]
        ).resolve()

        audio, current_sample_rate = (
            torchaudio.load(
                str(audio_path)
            )
        )

        if sample_rate is None:
            sample_rate = current_sample_rate
            channels = audio.shape[0]
        elif current_sample_rate != sample_rate:
            raise RuntimeError(
                f"Segment {segment_index} uses sample rate "
                f"{current_sample_rate}; expected {sample_rate}."
            )
        elif audio.shape[0] != channels:
            raise RuntimeError(
                f"Segment {segment_index} uses "
                f"{audio.shape[0]} channels; expected {channels}."
            )

        start_seconds = (
            cursor_samples / sample_rate
        )

        audio_parts.append(audio)
        cursor_samples += audio.shape[-1]

        end_seconds = (
            cursor_samples / sample_rate
        )

        timing.append(
            {
                "segmentIndex": segment_index,
                "speakerId": segment["speakerId"],
                "voiceId": segment["voiceId"],
                "audioPath": str(audio_path),
                "startSeconds": start_seconds,
                "endSeconds": end_seconds,
                "durationSeconds":
                    end_seconds - start_seconds,
            }
        )

        if position < len(segments) - 1:
            pause_samples = round(
                args.pause * sample_rate
            )

            silence = torch.zeros(
                channels,
                pause_samples,
                dtype=audio.dtype,
            )

            audio_parts.append(silence)
            cursor_samples += pause_samples

    if sample_rate is None:
        raise RuntimeError(
            "Unable to determine chapter sample rate."
        )

    chapter_audio = torch.cat(
        audio_parts,
        dim=-1,
    )

    chapter_id = manifest["chapterId"]

    output_path = (
        chapter_directory /
        f"{chapter_id}.wav"
    )

    temporary_path = (
        chapter_directory /
        f".{chapter_id}.wav.tmp"
    )

    torchaudio.save(
        str(temporary_path),
        chapter_audio,
        sample_rate,
        format="wav",
    )

    temporary_path.replace(output_path)

    duration_seconds = (
        chapter_audio.shape[-1] /
        sample_rate
    )

    assembly = {
        "schemaVersion": 1,
        "generatedUtc": datetime.now(
            timezone.utc
        ).isoformat(),
        "chapterId": chapter_id,
        "sourceSha256":
            manifest["sourceSha256"],
        "sampleRate": sample_rate,
        "channels": channels,
        "pauseSeconds": args.pause,
        "durationSeconds": duration_seconds,
        "audioPath": str(output_path),
        "segments": timing,
    }

    assembly_path = (
        chapter_directory /
        "assembly.json"
    )

    temporary_assembly_path = (
        chapter_directory /
        ".assembly.json.tmp"
    )

    with temporary_assembly_path.open(
        "w",
        encoding="utf-8",
        newline="\n",
    ) as stream:
        json.dump(
            assembly,
            stream,
            indent=2,
            ensure_ascii=False,
        )
        stream.write("\n")

    temporary_assembly_path.replace(
        assembly_path
    )

    print("Chapter assembly complete.")
    print(f"Chapter:   {chapter_id}")
    print(f"Segments:  {len(segments)}")
    print(f"Pause:     {args.pause:.2f} seconds")
    print(f"Duration:  {duration_seconds:.2f} seconds")
    print(f"Audio:     {output_path}")
    print(f"Manifest:  {assembly_path}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
