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


def write_json_atomic(
    path: Path,
    value: dict,
) -> None:
    temporary_path = path.with_name(
        f".{path.name}.tmp"
    )

    with temporary_path.open(
        "w",
        encoding="utf-8",
        newline="\n",
    ) as stream:
        json.dump(
            value,
            stream,
            indent=2,
            ensure_ascii=False,
        )
        stream.write("\n")

    temporary_path.replace(path)


def load_expected_chapters(
    assembly: dict,
) -> tuple[list[dict], str]:
    chapters = sorted(
        assembly["chapters"],
        key=lambda chapter: chapter["index"],
    )

    if not chapters:
        raise RuntimeError(
            "Book assembly contains no chapters."
        )

    expected_chapters: list[dict] = []
    chapter_texts: list[str] = []

    for expected_index, chapter in enumerate(chapters):
        if chapter["index"] != expected_index:
            raise RuntimeError(
                "Book chapters must use sequential indexes."
            )

        audio_path = Path(
            chapter["audioPath"]
        ).resolve()

        chapter_manifest_path = (
            audio_path.parent / "chapter.json"
        )

        if not chapter_manifest_path.is_file():
            raise FileNotFoundError(
                f"Chapter manifest was not found: "
                f"{chapter_manifest_path}"
            )

        chapter_manifest = load_json(
            chapter_manifest_path
        )

        if (
            chapter_manifest.get("chapterId") !=
                chapter["chapterId"]
        ):
            raise RuntimeError(
                f"Chapter manifest identity does not match "
                f"{chapter['chapterId']}: "
                f"{chapter_manifest_path}"
            )

        segments = sorted(
            chapter_manifest["segments"],
            key=lambda segment: segment["index"],
        )

        for segment_index, segment in enumerate(segments):
            if segment["index"] != segment_index:
                raise RuntimeError(
                    f"Chapter {chapter['chapterId']} segments "
                    "must use sequential indexes."
                )

        expected_text = "".join(
            segment["sourceText"]
            for segment in segments
        )

        expected_words = normalize_words(
            normalize_spoken_text(expected_text)
        )

        expected_chapters.append(
            {
                "index": chapter["index"],
                "chapterId": chapter["chapterId"],
                "chapterManifestPath":
                    str(chapter_manifest_path),
                "expectedWordCount":
                    len(expected_words),
            }
        )

        chapter_texts.append(expected_text)

    return (
        expected_chapters,
        "\n\n".join(chapter_texts),
    )


def main() -> int:
    parser = argparse.ArgumentParser(
        description=(
            "Verify a complete assembled StoryCast audiobook."
        )
    )
    parser.add_argument(
        "assembly_manifest",
        type=Path,
    )
    parser.add_argument(
        "--model",
        default="small.en",
    )
    args = parser.parse_args()

    assembly_path = (
        args.assembly_manifest.resolve()
    )
    assembly = load_json(assembly_path)

    if assembly.get("schemaVersion") != 1:
        raise RuntimeError(
            "Unsupported book assembly manifest schema."
        )

    expected_chapters, expected_text = (
        load_expected_chapters(assembly)
    )

    audio_path = Path(
        assembly["audioPath"]
    ).resolve()

    if not audio_path.is_file():
        raise FileNotFoundError(
            f"Assembled audiobook was not found: "
            f"{audio_path}"
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
        f"Verifying complete audiobook: "
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
        "bookId": assembly["bookId"],
        "title": assembly["title"],
        "author": assembly["author"],
        "model": args.model,
        "status": status,
        "expectedWordCount":
            len(expected_words),
        "transcribedWordCount":
            len(actual_words),
        "editDistance": distance,
        "wordErrorRate": round(
            word_error_rate,
            6,
        ),
        "expectedText": expected_text,
        "transcription": transcription,
        "audioPath": str(audio_path),
        "assemblyManifestPath":
            str(assembly_path),
        "chapters": expected_chapters,
    }

    report_path = (
        assembly_path.parent /
        "book-verification.json"
    )

    write_json_atomic(
        report_path,
        report,
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
    print(f"Status:          {status}")
    print(f"Report:          {report_path}")

    return 0 if status == "pass" else 2


if __name__ == "__main__":
    raise SystemExit(main())
