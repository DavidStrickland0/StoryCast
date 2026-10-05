from __future__ import annotations

import argparse
import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path
from worker_device import whisper_options

from faster_whisper import WhisperModel


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Verify StoryCast voice samples with Whisper."
    )
    parser.add_argument(
        "library",
        type=Path,
        help="Voice-library directory.",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=None,
        help="Output JSON report path.",
    )
    parser.add_argument(
        "--model",
        default="small.en",
        help="Faster-Whisper model name.",
    )
    return parser.parse_args()


def normalize_words(text: str) -> list[str]:
    words = re.findall(
        r"[a-z0-9]+(?:'[a-z0-9]+)?",
        text.lower(),
    )

    number_words = {
        "zero": "0",
        "one": "1",
        "two": "2",
        "three": "3",
        "four": "4",
        "five": "5",
        "six": "6",
        "seven": "7",
        "eight": "8",
        "nine": "9",
        "ten": "10",
    }

    return [
        number_words.get(word, word)
        for word in words
    ]


def soundex(word: str) -> str:
    if not word:
        return ""

    groups = {
        **dict.fromkeys("bfpv", "1"),
        **dict.fromkeys("cgjkqsxz", "2"),
        **dict.fromkeys("dt", "3"),
        "l": "4",
        **dict.fromkeys("mn", "5"),
        "r": "6",
    }

    first = word[0]
    encoded: list[str] = []
    previous = groups.get(first, "")

    for character in word[1:]:
        current = groups.get(character, "")

        if current and current != previous:
            encoded.append(current)

        previous = current

    return (
        first.upper() +
        "".join(encoded) +
        "000"
    )[:4]


def words_equivalent(
    expected: str,
    actual: str,
) -> bool:
    if expected == actual:
        return True

    if len(expected) < 4 or len(actual) < 4:
        return False

    return soundex(expected) == soundex(actual)

def edit_distance(
    expected: list[str],
    actual: list[str],
) -> int:
    previous = list(range(len(actual) + 1))

    for expected_index, expected_word in enumerate(
        expected,
        start=1,
    ):
        current = [expected_index]

        for actual_index, actual_word in enumerate(
            actual,
            start=1,
        ):
            substitution_cost = (
                0
                if words_equivalent(
                    expected_word,
                    actual_word,
                )
                else 1
            )

            current.append(
                min(
                    previous[actual_index] + 1,
                    current[actual_index - 1] + 1,
                    previous[actual_index - 1]
                    + substitution_cost,
                )
            )

        previous = current

    return previous[-1]


def classify(word_error_rate: float) -> str:
    if word_error_rate <= 0.08:
        return "pass"

    if word_error_rate <= 0.20:
        return "review"

    return "fail"


def transcribe(
    model: WhisperModel,
    sample_path: Path,
) -> tuple[str, str, float]:
    segments, info = model.transcribe(
        str(sample_path),
        language="en",
        beam_size=5,
        vad_filter=True,
        condition_on_previous_text=False,
        temperature=0.0,
        word_timestamps=True,
        hallucination_silence_threshold=1.0,
    )

    text = " ".join(
        segment.text.strip()
        for segment in segments
    ).strip()

    return (
        text,
        info.language,
        info.language_probability,
    )


def main() -> int:
    args = parse_args()

    library = args.library.resolve()

    if not library.is_dir():
        raise FileNotFoundError(
            f"Voice library was not found: {library}"
        )

    output_path = (
        args.output.resolve()
        if args.output is not None
        else library / "voice-verification.json"
    )

    manifests = sorted(
        library.glob("*/voice.json"),
        key=lambda path: path.parent.name.lower(),
    )

    if not manifests:
        raise RuntimeError(
            f"No voice manifests were found: {library}"
        )

    print(
        f"Loading {args.model} on {whisper_options()['device']}...",
        flush=True,
    )

    model = WhisperModel(
        args.model,
        **whisper_options(),
    )

    results: list[dict[str, object]] = []

    for index, manifest_path in enumerate(
        manifests,
        start=1,
    ):
        with manifest_path.open(
            "r",
            encoding="utf-8-sig",
        ) as stream:
            manifest = json.load(stream)

        voice_id = manifest["id"]
        expected_text = manifest["sampleText"]
        sample_path = (
            manifest_path.parent / manifest["sample"]
        ).resolve()

        if not sample_path.is_file():
            raise FileNotFoundError(
                f"Voice sample was not found: {sample_path}"
            )

        transcription, language, probability = transcribe(
            model,
            sample_path,
        )

        expected_words = normalize_words(expected_text)
        actual_words = normalize_words(transcription)

        distance = edit_distance(
            expected_words,
            actual_words,
        )

        word_error_rate = (
            distance / len(expected_words)
            if expected_words
            else 1.0
        )

        status = classify(word_error_rate)

        result = {
            "voiceId": voice_id,
            "directory": manifest_path.parent.name,
            "expectedWordCount": len(expected_words),
            "transcribedWordCount": len(actual_words),
            "editDistance": distance,
            "wordErrorRate": round(
                word_error_rate,
                6,
            ),
            "status": status,
            "language": language,
            "languageProbability": round(
                language_probability
                if (
                    language_probability :=
                    probability
                ) is not None
                else 0.0,
                6,
            ),
            "transcription": transcription,
        }

        results.append(result)

        print(
            f"[{index:2}/{len(manifests)}] "
            f"{voice_id}  "
            f"WER={word_error_rate:.1%}  "
            f"{status}",
            flush=True,
        )

    summary = {
        "total": len(results),
        "passed": sum(
            result["status"] == "pass"
            for result in results
        ),
        "review": sum(
            result["status"] == "review"
            for result in results
        ),
        "failed": sum(
            result["status"] == "fail"
            for result in results
        ),
    }

    report = {
        "schemaVersion": 1,
        "generatedUtc": datetime.now(
            timezone.utc
        ).isoformat(),
        "model": args.model,
        "thresholds": {
            "passMaximumWordErrorRate": 0.08,
            "reviewMaximumWordErrorRate": 0.20,
        },
        "summary": summary,
        "voices": results,
    }

    output_path.parent.mkdir(
        parents=True,
        exist_ok=True,
    )

    temporary_path = output_path.with_name(
        f".{output_path.name}.tmp"
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

    temporary_path.replace(output_path)

    print()
    print("Voice verification complete.")
    print(f"Passed: {summary['passed']}")
    print(f"Review: {summary['review']}")
    print(f"Failed: {summary['failed']}")
    print(f"Report: {output_path}")

    return 2 if summary["failed"] > 0 else 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exception:
        print(
            f"Voice verification failed: {exception}",
            file=sys.stderr,
        )
        raise
