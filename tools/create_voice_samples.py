from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
import subprocess
import tempfile
from pathlib import Path

import torch
import torchaudio

from chatterbox.mtl_tts import ChatterboxMultilingualTTS


SAMPLE_TEXT_PARTS = [
    (
        "Beyond the northern ridge, morning light spread across the quiet "
        "valley. A cold wind moved through the trees while distant water "
        "echoed against the stones."
    ),
    (
        "Mara paused beside the weathered gate and listened. "
        "\"We should keep moving,\" she said calmly. "
        "No one answered, but somewhere in the darkness, a wooden door "
        "closed with a sharp and final sound."
    ),
]

EXAGGERATION = 0.5
CFG_WEIGHT = 0.5
TEMPERATURE = 0.7
INTER_CHUNK_PAUSE_SECONDS = 0.18
REFERENCE_SECONDS = 20.0
REFERENCE_SAMPLE_RATE = 24000


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Generate standardized Chatterbox voice samples from MP3 "
            "reference files."
        )
    )

    parser.add_argument(
        "input_directory",
        type=Path,
        help="Directory containing the source MP3 files.",
    )

    parser.add_argument(
        "output_directory",
        type=Path,
        help="Directory in which StoryCast voice folders will be created.",
    )

    parser.add_argument(
        "--reference-start",
        type=float,
        default=0.0,
        help="Seconds to skip at the beginning of every source MP3.",
    )

    parser.add_argument(
        "--reference-seconds",
        type=float,
        default=REFERENCE_SECONDS,
        help="Maximum reference audio duration. Default: 20 seconds.",
    )

    parser.add_argument(
        "--overwrite",
        action="store_true",
        help="Regenerate samples that already exist.",
    )

    parser.add_argument(
        "--device",
        choices=["auto", "cuda", "cpu"],
        default="auto",
        help="Generation device. Default: auto.",
    )

    return parser.parse_args()


def resolve_device(requested_device: str) -> str:
    if requested_device == "auto":
        return "cuda" if torch.cuda.is_available() else "cpu"

    if requested_device == "cuda" and not torch.cuda.is_available():
        raise RuntimeError(
            "CUDA was requested, but PyTorch cannot access a CUDA device."
        )

    return requested_device


def require_ffmpeg() -> str:
    ffmpeg = shutil.which("ffmpeg")

    if ffmpeg is None:
        raise RuntimeError(
            "FFmpeg was not found on PATH. Install FFmpeg before running "
            "this script."
        )

    return ffmpeg


def create_voice_id(source: Path) -> str:
    """Create a neutral, stable identifier from the source audio bytes."""
    digest = hashlib.sha256()

    with source.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)

    return f"voice-{digest.hexdigest()[:16]}"

def prepare_reference(
    ffmpeg: str,
    source: Path,
    destination: Path,
    start_seconds: float,
    duration_seconds: float,
) -> None:
    command = [
        ffmpeg,
        "-hide_banner",
        "-loglevel",
        "error",
        "-y",
        "-ss",
        str(start_seconds),
        "-i",
        str(source),
        "-t",
        str(duration_seconds),
        "-ac",
        "1",
        "-ar",
        str(REFERENCE_SAMPLE_RATE),
        "-af",
        "highpass=f=70,lowpass=f=12000,loudnorm=I=-20:TP=-2:LRA=7",
        str(destination),
    ]

    result = subprocess.run(
        command,
        capture_output=True,
        text=True,
        check=False,
    )

    if result.returncode != 0:
        message = result.stderr.strip() or "Unknown FFmpeg error."
        raise RuntimeError(
            f"Could not prepare reference audio from {source}: {message}"
        )


def set_seed(seed: int) -> None:
    torch.manual_seed(seed)

    if torch.cuda.is_available():
        torch.cuda.manual_seed_all(seed)


def generate_sample(
    model: ChatterboxMultilingualTTS,
    reference_path: Path,
) -> torch.Tensor:
    generated_parts: list[torch.Tensor] = []

    pause_samples = round(
        model.sr * INTER_CHUNK_PAUSE_SECONDS
    )

    pause = torch.zeros(
        1,
        pause_samples,
        dtype=torch.float32,
    )

    for index, text in enumerate(SAMPLE_TEXT_PARTS):
        set_seed(10_000 + index)

        with torch.inference_mode():
            audio = model.generate(
                text,
                language_id="en",
                audio_prompt_path=str(reference_path),
                exaggeration=EXAGGERATION,
                cfg_weight=CFG_WEIGHT,
                temperature=TEMPERATURE,
            )

        audio = audio.detach().to(
            device="cpu",
            dtype=torch.float32,
        )

        if audio.ndim == 1:
            audio = audio.unsqueeze(0)

        if index > 0:
            generated_parts.append(pause)

        generated_parts.append(audio)

    combined = torch.cat(generated_parts, dim=-1)

    peak = combined.abs().max()

    if peak > 0:
        combined = combined * (0.95 / peak)

    return combined


def write_manifest(
    destination: Path,
    voice_id: str,
    source: Path,
    duration_seconds: float,
) -> None:
    manifest = {
        "id": voice_id,
        "sample": "sample.wav",
        "language": "en",
        "accent": "",
        "apparentAge": "",
        "presentation": "",
        "qualities": [],
        "suitableRoles": [],
        "narratorSuitable": False,
        "sourceFile": str(source.resolve()),
        "sampleText": " ".join(SAMPLE_TEXT_PARTS),
        "generatedDurationSeconds": round(duration_seconds, 2),
    }

    destination.write_text(
        json.dumps(manifest, indent=2) + "\n",
        encoding="utf-8",
    )


def process_voice(
    model: ChatterboxMultilingualTTS,
    ffmpeg: str,
    source: Path,
    output_root: Path,
    reference_start: float,
    reference_seconds: float,
    overwrite: bool,
) -> tuple[str, float, bool]:
    voice_id = create_voice_id(source)
    voice_directory = output_root / voice_id
    sample_path = voice_directory / "sample.wav"
    manifest_path = voice_directory / "voice.json"

    if sample_path.exists() and manifest_path.exists() and not overwrite:
        return voice_id, 0.0, True

    voice_directory.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(
        prefix=f"storycast-{voice_id}-"
    ) as temporary_directory:
        reference_path = (
            Path(temporary_directory) / "reference.wav"
        )

        prepare_reference(
            ffmpeg=ffmpeg,
            source=source,
            destination=reference_path,
            start_seconds=reference_start,
            duration_seconds=reference_seconds,
        )

        audio = generate_sample(
            model=model,
            reference_path=reference_path,
        )

        torchaudio.save(
            str(sample_path),
            audio,
            model.sr,
        )

    duration_seconds = audio.shape[-1] / model.sr

    write_manifest(
        destination=manifest_path,
        voice_id=voice_id,
        source=source,
        duration_seconds=duration_seconds,
    )

    return voice_id, duration_seconds, False


def main() -> int:
    arguments = parse_arguments()

    input_directory = arguments.input_directory.resolve()
    output_directory = arguments.output_directory.resolve()

    if not input_directory.is_dir():
        raise DirectoryNotFoundError(
            f"Input directory was not found: {input_directory}"
        )

    if arguments.reference_start < 0:
        raise ValueError("--reference-start cannot be negative.")

    if arguments.reference_seconds <= 0:
        raise ValueError("--reference-seconds must be greater than zero.")

    mp3_files = sorted(
        input_directory.rglob("*.mp3"),
        key=lambda path: str(path).lower(),
    )

    if not mp3_files:
        print(f"No MP3 files found beneath {input_directory}")
        return 0

    output_directory.mkdir(parents=True, exist_ok=True)

    voice_ids: dict[str, Path] = {}

    for source in mp3_files:
        voice_id = create_voice_id(source)

        if voice_id in voice_ids:
            raise ValueError(
                f"Duplicate voice ID '{voice_id}' would be produced by "
                f"both '{voice_ids[voice_id]}' and '{source}'."
            )

        voice_ids[voice_id] = source

    ffmpeg = require_ffmpeg()
    device = resolve_device(arguments.device)

    print(f"Input:   {input_directory}")
    print(f"Output:  {output_directory}")
    print(f"Voices:  {len(mp3_files)}")
    print(f"Device:  {device}")
    print()
    print("Loading Chatterbox...")

    model = ChatterboxMultilingualTTS.from_pretrained(
        device=device,
        t3_model="v3",
    )

    failures: list[tuple[Path, str]] = []

    for index, source in enumerate(mp3_files, start=1):
        print(
            f"[{index}/{len(mp3_files)}] {source.name}",
            flush=True,
        )

        try:
            voice_id, duration, skipped = process_voice(
                model=model,
                ffmpeg=ffmpeg,
                source=source,
                output_root=output_directory,
                reference_start=arguments.reference_start,
                reference_seconds=arguments.reference_seconds,
                overwrite=arguments.overwrite,
            )

            if skipped:
                print(f"  Skipped existing voice: {voice_id}")
            else:
                print(
                    f"  Created {voice_id}/sample.wav "
                    f"({duration:.1f} seconds)"
                )
        except Exception as exception:
            failures.append((source, str(exception)))
            print(f"  FAILED: {exception}")

        if torch.cuda.is_available():
            torch.cuda.empty_cache()

    print()
    print(
        f"Completed: {len(mp3_files) - len(failures)}"
    )
    print(f"Failed:    {len(failures)}")

    if failures:
        print()

        for source, error in failures:
            print(f"{source}: {error}")

        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
