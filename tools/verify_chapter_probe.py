from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path

from faster_whisper import WhisperModel

from pronunciations import (
    canonicalize_transcription,
    find_book_root,
    load_pronunciations,
)

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


def is_repeated_utterance(
    expected_words: list[str],
    actual_words: list[str],
) -> bool:
    """Detect an exact utterance repeated two or more times."""
    if (
        not expected_words
        or len(actual_words) <= len(expected_words)
        or len(actual_words) % len(expected_words) != 0
    ):
        return False

    repetitions = (
        len(actual_words) //
        len(expected_words)
    )

    return (
        repetitions >= 2
        and actual_words ==
        expected_words * repetitions
    )

def classify_verification(
    expected_words: list[str],
    actual_words: list[str],
    word_error_rate: float,
) -> tuple[str, str]:
    """Classify transcript accuracy or short-utterance audibility."""
    if is_repeated_utterance(
        expected_words,
        actual_words,
    ):
        return "fail", "repeated-utterance"
    if (
        0 < len(expected_words) <= 5
        and actual_words
    ):
        return "pass", "audibility-short-utterance"

    if word_error_rate <= 0.08:
        return "pass", "transcript"

    if word_error_rate <= 0.20:
        return "review", "transcript"

    if (
        len(expected_words) <= 10
        and actual_words
        and abs(len(expected_words) - len(actual_words)) <= 2
        and edit_distance(expected_words, actual_words) <= 2
    ):
        return "review", "transcript-short-fragment"

    return "fail", "transcript"


def verify_audio(
    model: WhisperModel,
    audio_path: Path,
    expected_text: str,
    pronunciations: list,
) -> dict:
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
    expected_text = expected_text.replace("\u2019", "'").replace(
        "\u00e2\u20ac\u2122", "'"
    )
    expected_words = normalize_words(
        canonicalize_transcription(expected_text, pronunciations)
    )
    actual_words = normalize_words(
        canonicalize_transcription(transcription, pronunciations)
    )
    distance = edit_distance(expected_words, actual_words)
    word_error_rate = (
        distance / len(expected_words)
        if expected_words
        else 1.0
    )
    status, verification_mode = classify_verification(
        expected_words,
        actual_words,
        word_error_rate,
    )
    return {
        "expectedWordCount": len(expected_words),
        "transcribedWordCount": len(actual_words),
        "editDistance": distance,
        "wordErrorRate": round(word_error_rate, 6),
        "status": status,
        "verificationMode": verification_mode,
        "expectedText": expected_text,
        "transcription": transcription,
        "audioPath": str(audio_path),
    }


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
    book = find_book_root(manifest_path)
    pronunciations = load_pronunciations(book)
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
    chunk_results: list[dict] = []

    for position, segment in enumerate(
        manifest["segments"],
        start=1,
    ):
        print(
            f"[{position}/{len(manifest['segments'])}] "
            f"segment {segment['index']} | "
            f"{segment['speakerId']}",
            flush=True,
        )

        chunks = segment.get("chunks") or [
            {
                "index": 0,
                "sourceText": segment["sourceText"],
                "audioPath": segment["audioPath"],
            }
        ]
        current_results: list[dict] = []
        for chunk in chunks:
            result = verify_audio(
                model,
                Path(chunk["audioPath"]).resolve(),
                chunk["sourceText"],
                pronunciations,
            )
            result.update(
                {
                    "segmentIndex": segment["index"],
                    "chunkIndex": chunk["index"],
                    "speakerId": segment["speakerId"],
                    "voiceId": segment["voiceId"],
                }
            )
            current_results.append(result)
            chunk_results.append(result)
            print(
                f"    chunk {chunk['index']} | "
                f"WER={result['wordErrorRate']:.1%} | "
                f"{result['status']} | {result['verificationMode']}",
                flush=True,
            )

        status = (
            "fail"
            if any(item["status"] == "fail" for item in current_results)
            else "review"
            if any(item["status"] == "review" for item in current_results)
            else "pass"
        )
        results.append(
            {
                "segmentIndex": segment["index"],
                "speakerId": segment["speakerId"],
                "voiceId": segment["voiceId"],
                "status": status,
                "chunks": current_results,
            }
        )

    summary = {
        "total": len(chunk_results),
        "passed": sum(
            item["status"] == "pass"
            for item in chunk_results
        ),
        "review": sum(
            item["status"] == "review"
            for item in chunk_results
        ),
        "failed": sum(
            item["status"] == "fail"
            for item in chunk_results
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
        "chunks": chunk_results,
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
        if summary["review"] > 0 or summary["failed"] > 0
        else 0
    )


if __name__ == "__main__":
    raise SystemExit(main())
