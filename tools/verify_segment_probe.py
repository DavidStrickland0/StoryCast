from __future__ import annotations

import argparse
import json
from pathlib import Path

from faster_whisper import WhisperModel

from verify_voice_library import (
    edit_distance,
    normalize_words,
)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Verify one synthesized StoryCast segment."
    )
    parser.add_argument(
        "metadata",
        type=Path,
        help="Synthesized segment metadata JSON.",
    )
    parser.add_argument(
        "--model",
        default="small.en",
        help="Faster-Whisper model.",
    )

    args = parser.parse_args()

    metadata_path = args.metadata.resolve()

    with metadata_path.open(
        "r",
        encoding="utf-8-sig",
    ) as stream:
        metadata = json.load(stream)

    audio_path = Path(
        metadata["audioPath"]
    ).resolve()

    expected_text = metadata["sourceText"]

    print(
        f"Loading {args.model} on CUDA...",
        flush=True,
    )

    model = WhisperModel(
        args.model,
        device="cuda",
        compute_type="float16",
    )

    print(
        f"Verifying: {audio_path}",
        flush=True,
    )

    segments, _ = model.transcribe(
        str(audio_path),
        language="en",
        beam_size=5,
        vad_filter=True,
        condition_on_previous_text=False,
        temperature=0.0,
        word_timestamps=True,
        hallucination_silence_threshold=1.0,
    )

    transcription = " ".join(
        segment.text.strip()
        for segment in segments
    ).strip()

    expected_words = normalize_words(
        expected_text
    )

    actual_words = normalize_words(
        transcription
    )

    distance = edit_distance(
        expected_words,
        actual_words,
    )

    word_error_rate = (
        distance / len(expected_words)
        if expected_words
        else 1.0
    )

    print()
    print("EXPECTED:")
    print(expected_text)
    print()
    print("TRANSCRIPTION:")
    print(transcription)
    print()
    print(f"Expected words: {len(expected_words)}")
    print(f"Actual words:   {len(actual_words)}")
    print(f"Edit distance:  {distance}")
    print(f"Word error rate: {word_error_rate:.1%}")

    return 0 if word_error_rate <= 0.08 else 2


if __name__ == "__main__":
    raise SystemExit(main())
