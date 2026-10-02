from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path

from pronunciations import (
    canonicalize_transcription,
    find_book_root,
    load_pronunciations,
)
from spoken_text import normalize_spoken_text

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
    """Classify transcript accuracy for every segment length."""
    if is_repeated_utterance(
        expected_words,
        actual_words,
    ):
        return "fail", "repeated-utterance"
    if word_error_rate <= 0.08:
        return "pass", "transcript"

    if word_error_rate <= 0.20:
        return "review", "transcript"

    return "fail", "transcript"


def verify_audio(
    model: WhisperModel,
    audio_path: Path,
    expected_text: str,
    pronunciations: list,
) -> dict:
    import torchaudio
    from audio_postprocessing import analyze_audio
    from verify_voice_library import edit_distance, normalize_words

    audio, sample_rate = torchaudio.load(str(audio_path))
    audio_quality = analyze_audio(audio, sample_rate)
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
        normalize_spoken_text(
            canonicalize_transcription(expected_text, pronunciations)
        )
    )
    actual_words = normalize_words(
        normalize_spoken_text(
            canonicalize_transcription(transcription, pronunciations)
        )
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
    if (
        audio_quality["maximumSilenceSeconds"] > 1.5 or
        audio_quality["leadingSilenceSeconds"] > 0.6 or
        audio_quality["trailingSilenceSeconds"] > 0.6
    ):
        status = "fail"
        verification_mode = "audio-quality-silence"
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
        "audioQuality": audio_quality,
    }


def main() -> int:
    from faster_whisper import WhisperModel
    from verify_voice_library import edit_distance, normalize_words

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
    parser.add_argument(
        "--segment-index",
        type=int,
        action="append",
        default=None,
    )

    args = parser.parse_args()

    manifest_path = args.chapter_manifest.resolve()
    manifest = load_json(manifest_path)

    selected_indexes = (
        set(args.segment_index)
        if args.segment_index
        else None
    )

    report_path = (
        manifest_path.parent /
        "verification.json"
    )

    existing_results: list[dict] = []

    if (
        selected_indexes is not None
        and report_path.is_file()
    ):
        existing_report = load_json(report_path)
        existing_results = [
            segment
            for segment in existing_report.get(
                "segments",
                [],
            )
            if int(segment["segmentIndex"])
            not in selected_indexes
        ]
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

    segments_to_verify = [
        segment
        for segment in manifest["segments"]
        if (
            selected_indexes is None
            or int(segment["index"]) in selected_indexes
        )
    ]

    results: list[dict] = list(
        existing_results
    )

    chunk_results = [chunk for result in existing_results for chunk in result.get("chunks", [])]

    for position, segment in enumerate(
        segments_to_verify,
        start=1,
    ):
        audio_path = Path(
            segment["audioPath"]
        ).resolve()

        source_text = segment["sourceText"]
        expected_text = segment.get(
            "spokenText",
            source_text,
        )
        was_rewritten = (
            expected_text != source_text
        )

        print(
            f"[{position}/{len(segments_to_verify)}] "
            f"segment {segment['index']} | "
            f"{segment['speakerId']}",
            flush=True,
        )

        chunks = segment.get("chunks") or [
            {
                "index": 0,
                "sourceText": expected_text,
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

        if was_rewritten and status == "pass":
            status = "review"
            for result in current_results:
                result["status"] = "review"
                result["verificationMode"] = "ai-rewritten-short-utterance"
        expected_count = sum(c["expectedWordCount"] for c in current_results)
        distance = sum(c["editDistance"] for c in current_results)
        verification_mode = "ai-rewritten-short-utterance" if was_rewritten and status == "review" else "transcript"
        transcription = " ".join(c["transcription"] for c in current_results)
        word_error_rate = distance / expected_count if expected_count else 1.0
        results.append(
            {
                "chunks": current_results,
                "expectedWordCount": expected_count,
                "transcribedWordCount": sum(c["transcribedWordCount"] for c in current_results),
                "editDistance": distance,
                "wordErrorRate": word_error_rate,
                "segmentIndex": segment["index"],
                "speakerId": segment["speakerId"],
                "voiceId": segment["voiceId"],
                "status": status,
                "verificationMode": verification_mode,
                "sourceText": source_text,
                "expectedText": expected_text,
                "spokenText": expected_text,
                "wasRewritten": was_rewritten,
                "transcription": transcription,
                "audioPath": str(audio_path),
            }
        )

        print(
            f"    WER={word_error_rate:.1%} | "
            f"{status} | {verification_mode}",
            flush=True,
        )

    results.sort(
        key=lambda item: int(
            item["segmentIndex"]
        )
    )

    chunk_results.sort(key=lambda c: (int(c["segmentIndex"]), int(c["chunkIndex"])))
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
