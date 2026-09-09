from __future__ import annotations

import argparse
import json
import uuid
from datetime import datetime, timezone
from pathlib import Path

import torch
import torchaudio

from prosody import resolve_segment_synthesis_settings
from chatterbox.tts import ChatterboxTTS


DEFAULT_NARRATOR_EXAGGERATION = 0.4
DEFAULT_CHARACTER_EXAGGERATION = 0.65
DEFAULT_CFG_WEIGHT = 0.5
DEFAULT_TEMPERATURE = 0.7


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


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Synthesize one StoryCast production segment."
    )
    parser.add_argument(
        "book",
        type=Path,
        help="StoryCast book directory.",
    )
    parser.add_argument(
        "voice_library",
        type=Path,
        help="StoryCast voice-library directory.",
    )
    parser.add_argument(
        "--chapter",
        default="chapter-001",
        help="Chapter identifier.",
    )
    parser.add_argument(
        "--segment",
        type=int,
        default=0,
        help="Production-segment index.",
    )
    return parser.parse_args()


def load_json(path: Path) -> dict:
    with path.open(
        "r",
        encoding="utf-8-sig",
    ) as stream:
        return json.load(stream)


def find_voice_sample(
    library: Path,
    voice_id: str,
) -> Path:
    for manifest_path in sorted(
        library.glob("*/voice.json")
    ):
        manifest = load_json(manifest_path)

        if manifest["id"].lower() != voice_id.lower():
            continue

        sample_path = (
            manifest_path.parent / manifest["sample"]
        ).resolve()

        if not sample_path.is_file():
            raise FileNotFoundError(
                f"Voice sample was not found: {sample_path}"
            )

        return sample_path

    raise RuntimeError(
        f"Voice was not found in the library: {voice_id}"
    )


def create_run_directory(book: Path) -> Path:
    timestamp = datetime.now(
        timezone.utc
    ).strftime("%Y%m%d-%H%M%S-%f")[:-3]

    run_id = f"{timestamp}-smoke-{uuid.uuid4().hex[:8]}"

    run_directory = (
        book / "output" / run_id
    ).resolve()

    run_directory.mkdir(
        parents=True,
        exist_ok=False,
    )

    return run_directory


def main() -> int:
    args = parse_args()

    book = args.book.resolve()
    library = args.voice_library.resolve()

    casting_path = (
        book / "production" / "casting.json"
    )

    script_path = (
        book /
        "production" /
        "scripts" /
        f"{args.chapter}.json"
    )

    casting = load_json(casting_path)
    artifact = load_json(script_path)

    matching_segments = [
        segment
        for segment in artifact["script"]["segments"]
        if segment["index"] == args.segment
    ]

    if len(matching_segments) != 1:
        raise RuntimeError(
            f"Expected one segment with index {args.segment}; "
            f"found {len(matching_segments)}."
        )

    segment = matching_segments[0]
    speaker_id = segment["speakerId"]

    matching_assignments = [
        assignment
        for assignment in casting["assignments"]
        if assignment["characterId"].lower()
        == speaker_id.lower()
    ]

    if len(matching_assignments) != 1:
        raise RuntimeError(
            f"Expected one casting assignment for "
            f"'{speaker_id}'; found "
            f"{len(matching_assignments)}."
        )

    assignment = matching_assignments[0]
    voice_id = assignment["voiceId"]
    synthesis_settings = resolve_synthesis_settings(
        assignment,
        speaker_id,
        segment["sourceText"],
        segment.get("delivery", ""),
    )

    voice_sample = find_voice_sample(
        library,
        voice_id,
    )

    run_directory = create_run_directory(book)

    chapter_directory = (
        run_directory / args.chapter
    )

    chapter_directory.mkdir()

    output_path = (
        chapter_directory /
        f"segment-{args.segment:04d}.wav"
    )

    torch.manual_seed(
        10000 + args.segment
    )

    if torch.cuda.is_available():
        torch.cuda.manual_seed_all(
            10000 + args.segment
        )

    device = (
        "cuda"
        if torch.cuda.is_available()
        else "cpu"
    )

    print(f"Run:       {run_directory}", flush=True)
    print(f"Chapter:   {args.chapter}", flush=True)
    print(f"Segment:   {args.segment}", flush=True)
    print(f"Speaker:   {speaker_id}", flush=True)
    print(f"Voice:     {voice_id}", flush=True)
    print(f"Reference: {voice_sample}", flush=True)
    print(f"Device:    {device}", flush=True)
    print()
    print("Loading Chatterbox...", flush=True)

    model = ChatterboxTTS.from_pretrained(
        device=device
    )

    print("Synthesizing segment...", flush=True)

    with torch.inference_mode():
        audio = model.generate(
            segment["sourceText"],
            audio_prompt_path=str(voice_sample),
            exaggeration=synthesis_settings["exaggeration"],
            cfg_weight=synthesis_settings["cfgWeight"],
            temperature=synthesis_settings["temperature"],
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

    metadata = {
        "schemaVersion": 1,
        "chapterId": args.chapter,
        "segmentIndex": args.segment,
        "speakerId": speaker_id,
        "voiceId": voice_id,
        "synthesis": synthesis_settings,
        "sourceText": segment["sourceText"],
        "sampleRate": model.sr,
        "durationSeconds": duration_seconds,
        "settings": {
            "exaggeration":
                synthesis_settings["exaggeration"],
            "cfgWeight":
                synthesis_settings["cfgWeight"],
            "temperature":
                synthesis_settings["temperature"],
            "profile":
                synthesis_settings["profile"],
            "seed": 10000 + args.segment,
        },
        "audioPath": str(output_path),
    }

    metadata_path = (
        chapter_directory /
        f"segment-{args.segment:04d}.json"
    )

    with metadata_path.open(
        "w",
        encoding="utf-8",
        newline="\n",
    ) as stream:
        json.dump(
            metadata,
            stream,
            indent=2,
            ensure_ascii=False,
        )
        stream.write("\n")

    print()
    print("Segment synthesis complete.")
    print(f"Duration: {duration_seconds:.2f} seconds")
    print(f"Audio:    {output_path}")
    print(f"Metadata: {metadata_path}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
