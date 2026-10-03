from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path

from faster_whisper import WhisperModel
from spoken_text import normalize_spoken_text

from verify_voice_library import (
    edit_distance,
    normalize_words,
)


def load_json(path: Path) -> dict:
    with path.open(
        "r",
        encoding="utf-8-sig",
    ) as stream:
        return json.load(stream)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Verify an assembled StoryCast chapter."
    )
    parser.add_argument(
        "assembly_manifest",
        type=Path,
    )
    parser.add_argument(
        "--model",
        default="small.en",
    )
    parser.add_argument(
        "--audio",
        type=Path,
        default=None,
    )
    parser.add_argument(
        "--report-name",
        default="assembly-verification.json",
    )

    args = parser.parse_args()

    assembly_path = args.assembly_manifest.resolve()
    chapter_directory = assembly_path.parent

    assembly = load_json(assembly_path)

    chapter_manifest = load_json(
        chapter_directory / "chapter.json"
    )

    expected_text = "".join(
        segment["sourceText"]
        for segment in sorted(
            chapter_manifest["segments"],
            key=lambda item: item["index"],
        )
    )

    audio_path = (
        args.audio.resolve()
        if args.audio is not None
        else Path(
            assembly["audioPath"]
        ).resolve()
    )

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
        f"Verifying assembled chapter: "
        f"{audio_path}",
        flush=True,
    )

    transcription_segments, _ = model.transcribe(
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
        for segment in transcription_segments
    ).strip()

    expected_words = normalize_words(
        normalize_spoken_text(expected_text)
    )

    actual_words = normalize_words(
        normalize_spoken_text(transcription)
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

    if word_error_rate <= 0.08:
        status = "pass"
    elif word_error_rate <= 0.20:
        status = "review"
    else:
        status = "fail"

    report = {
        "schemaVersion": 1,
        "generatedUtc": datetime.now(
            timezone.utc
        ).isoformat(),
        "model": args.model,
        "chapterId": assembly["chapterId"],
        "status": status,
        "expectedWordCount": len(expected_words),
        "transcribedWordCount": len(actual_words),
        "editDistance": distance,
        "wordErrorRate": round(
            word_error_rate,
            6,
        ),
        "expectedText": expected_text,
        "transcription": transcription,
        "audioPath": str(audio_path),
    }

    report_path = (
        chapter_directory /
        args.report_name
    )

    temporary_path = (
        chapter_directory /
        ".assembly-verification.json.tmp"
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

    temporary_path.replace(report_path)

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
    print(f"Status:          {status}")
    print(f"Report:          {report_path}")

    return 0 if status == "pass" else 2


if __name__ == "__main__":
    raise SystemExit(main())
