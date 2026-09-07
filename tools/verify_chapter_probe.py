from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path

from faster_whisper import WhisperModel

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
        description="Verify a synthesized StoryCast chapter."
    )
    parser.add_argument(
        "chapter_manifest",
        type=Path,
    )
    parser.add_argument(
        "--model",
        default="small.en",
    )

    args = parser.parse_args()

    manifest_path = args.chapter_manifest.resolve()
    manifest = load_json(manifest_path)

    print(
        f"Loading {args.model} on CUDA...",
        flush=True,
    )

    model = WhisperModel(
        args.model,
        device="cuda",
        compute_type="float16",
    )

    results: list[dict] = []

    for position, segment in enumerate(
        manifest["segments"],
        start=1,
    ):
        audio_path = Path(
            segment["audioPath"]
        ).resolve()

        expected_text = segment["sourceText"]

        print(
            f"[{position}/{len(manifest['segments'])}] "
            f"segment {segment['index']} | "
            f"{segment['speakerId']}",
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
            item.text.strip()
            for item in transcription_segments
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

        if word_error_rate <= 0.08:
            status = "pass"
        elif word_error_rate <= 0.20:
            status = "review"
        else:
            status = "fail"

        results.append(
            {
                "segmentIndex": segment["index"],
                "speakerId": segment["speakerId"],
                "voiceId": segment["voiceId"],
                "expectedWordCount":
                    len(expected_words),
                "transcribedWordCount":
                    len(actual_words),
                "editDistance": distance,
                "wordErrorRate": round(
                    word_error_rate,
                    6,
                ),
                "status": status,
                "expectedText": expected_text,
                "transcription": transcription,
                "audioPath": str(audio_path),
            }
        )

        print(
            f"    WER={word_error_rate:.1%} | {status}",
            flush=True,
        )

    summary = {
        "total": len(results),
        "passed": sum(
            item["status"] == "pass"
            for item in results
        ),
        "review": sum(
            item["status"] == "review"
            for item in results
        ),
        "failed": sum(
            item["status"] == "fail"
            for item in results
        ),
    }

    report = {
        "schemaVersion": 1,
        "generatedUtc": datetime.now(
            timezone.utc
        ).isoformat(),
        "model": args.model,
        "chapterId": manifest["chapterId"],
        "summary": summary,
        "segments": results,
    }

    report_path = (
        manifest_path.parent /
        "verification.json"
    )

    temporary_path = (
        manifest_path.parent /
        ".verification.json.tmp"
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
    print("Chapter verification complete.")
    print(f"Passed: {summary['passed']}")
    print(f"Review: {summary['review']}")
    print(f"Failed: {summary['failed']}")
    print(f"Report: {report_path}")

    return (
        2
        if summary["failed"] > 0
        or summary["review"] > 0
        else 0
    )


if __name__ == "__main__":
    raise SystemExit(main())
