from __future__ import annotations

import argparse
import json
import hashlib
import math
import wave
from datetime import datetime, timezone
from pathlib import Path
from worker_device import whisper_options

from faster_whisper import WhisperModel
from spoken_text import normalize_spoken_text
from pronunciations import canonicalize_transcription, find_book_root, load_pronunciations

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


def assembly_expected_text(manifest: dict) -> str:
    """Compare against the text actually synthesized, never ASR output."""
    return " ".join(
        chunk["sourceText"]
        for segment in sorted(manifest["segments"], key=lambda item: item["index"])
        for chunk in (sorted(segment.get("chunks") or [], key=lambda item: item["index"])
                      or [{"sourceText": segment.get("spokenText", segment["sourceText"])}])
    )


def comparison_words(text: str, pronunciations: list) -> list[str]:
    return normalize_words(normalize_spoken_text(canonicalize_transcription(text, pronunciations)))


def verification_text(text: str) -> str:
    """Use only the apostrophe substitutions made by chunk transcription."""
    return text.replace("\u2019", "'").replace("\u00e2\u20ac\u2122", "'")


def verify_segment_chunks(segment: dict, result: dict, allow_review: bool = False) -> None:
    chunks = sorted(segment.get("chunks") or [], key=lambda item: item["index"])
    if not chunks:
        return
    results = {item["chunkIndex"]: item for item in result.get("chunks", [])}
    indexes = [item["index"] for item in chunks]
    if len(set(indexes)) != len(indexes) or set(results) != set(indexes):
        raise ValueError("Chunk verification coverage is stale or duplicated.")
    with wave.open(segment["audioPath"], "rb") as output:
        fmt = (output.getnchannels(), output.getsampwidth(), output.getframerate(), output.getcomptype())
        frame_bytes = fmt[0] * fmt[1]
        for position, chunk in enumerate(chunks):
            verified = results[chunk["index"]]
            if verified["status"] not in (("pass", "review", "fail") if allow_review else ("pass",)) or verification_text(verified["expectedText"]) != verification_text(chunk["sourceText"]):
                raise ValueError("Chunk is unverified or its text has changed.")
            with wave.open(chunk["audioPath"], "rb") as source:
                if (source.getnchannels(), source.getsampwidth(), source.getframerate(), source.getcomptype()) != fmt:
                    raise ValueError("Chunk PCM format differs from segment.")
                while data := source.readframes(65536):
                    if output.readframes(len(data) // frame_bytes) != data:
                        raise ValueError("Segment PCM differs from verified chunks.")
            if position < len(chunks) - 1:
                pause = chunk.get("pauseAfterSeconds", 0)
                if not math.isfinite(pause) or pause < 0:
                    raise ValueError("Invalid chunk pause.")
                remaining = int(pause * fmt[2])
                while remaining:
                    frames = min(remaining, 65536)
                    if output.readframes(frames) != bytes(frames * frame_bytes):
                        raise ValueError("Segment chunk pause differs from manifest.")
                    remaining -= frames
        if output.readframes(1):
            raise ValueError("Segment contains extra PCM after its verified chunks.")


def verify_assembly_integrity(assembly: dict, chapter: dict, verification: dict, audio_path: Path, allow_review: bool = False) -> dict:
    """Prove raw assembly is the ordered verified PCM with only declared silence."""
    segments = sorted(chapter["segments"], key=lambda item: item["index"])
    indexes = [item["index"] for item in segments]
    if not indexes or len(set(indexes)) != len(indexes):
        raise ValueError("Empty or duplicate chapter segments.")
    if assembly["chapterId"] != chapter["chapterId"] or assembly["sourceSha256"] != chapter["sourceSha256"]:
        raise ValueError("Assembly belongs to different chapter source.")
    if [item["segmentIndex"] for item in assembly["segments"]] != indexes:
        raise ValueError("Assembly segment order or coverage differs from chapter.")
    results = {item["segmentIndex"]: item for item in verification["segments"]}
    if len(results) != len(verification["segments"]) or set(results) != set(indexes):
        raise ValueError("Verification segment coverage differs from chapter.")
    pause = assembly["pauseSeconds"]
    if not math.isfinite(pause) or pause < 0:
        raise ValueError("Invalid assembly pause.")
    digests = []
    with wave.open(str(audio_path), "rb") as output:
        fmt = (output.getnchannels(), output.getsampwidth(), output.getframerate(), output.getcomptype())
        if fmt[3] != "NONE" or (fmt[0], fmt[2]) != (assembly["channels"], assembly["sampleRate"]):
            raise ValueError("Assembly audio format differs from its manifest.")
        frame_bytes = fmt[0] * fmt[1]
        for position, segment in enumerate(segments):
            result = results[segment["index"]]
            if result["status"] not in (("pass", "review", "fail") if allow_review else ("pass",)):
                raise ValueError(f"Segment {segment['index']} is not verified.")
            expected = segment.get("spokenText", segment["sourceText"])
            if result.get("expectedText") != expected:
                raise ValueError(f"Segment {segment['index']} verification text is stale.")
            verify_segment_chunks(segment, result, allow_review)
            with wave.open(str(Path(segment["audioPath"])), "rb") as source:
                if (source.getnchannels(), source.getsampwidth(), source.getframerate(), source.getcomptype()) != fmt:
                    raise ValueError("Incompatible segment PCM format.")
                digest = hashlib.sha256()
                while data := source.readframes(65536):
                    digest.update(data)
                    if output.readframes(len(data) // frame_bytes) != data:
                        raise ValueError(f"Assembly PCM mismatch at segment {segment['index']}.")
                digests.append({"segmentIndex": segment["index"], "pcmSha256": digest.hexdigest()})
            if position < len(segments) - 1:
                remaining = round(pause * fmt[2])
                while remaining:
                    frames = min(remaining, 65536)
                    if output.readframes(frames) != bytes(frames * frame_bytes):
                        raise ValueError("Assembly pause contains speech, is missing, or has the wrong length.")
                    remaining -= frames
        if output.readframes(1):
            raise ValueError("Assembly contains extra audio after the final segment.")
    return {"verifiedSegmentCount": len(segments), "segmentPcmHashes": digests}


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

    parser.add_argument("--allow-review", action="store_true")
    args = parser.parse_args()

    assembly_path = args.assembly_manifest.resolve()
    chapter_directory = assembly_path.parent

    assembly = load_json(assembly_path)

    chapter_manifest = load_json(
        chapter_directory / "chapter.json"
    )

    expected_text = assembly_expected_text(chapter_manifest)
    pronunciations = load_pronunciations(find_book_root(assembly_path))

    audio_path = (
        args.audio.resolve()
        if args.audio is not None
        else Path(
            assembly["audioPath"]
        ).resolve()
    )

    if args.audio is None:
        try:
            details = verify_assembly_integrity(assembly, chapter_manifest,
                                              load_json(chapter_directory / "verification.json"), audio_path, args.allow_review)
            status = "pass"
        except (OSError, ValueError, KeyError, wave.Error) as error:
            details = {"error": str(error)}
            status = "fail"
        report = {"schemaVersion": 2, "generatedUtc": datetime.now(timezone.utc).isoformat(),
                  "chapterId": assembly["chapterId"], "status": status,
                  "verificationMode": "verified-segment-pcm-integrity", "audioPath": str(audio_path), **details}
        report_path = chapter_directory / args.report_name
        temporary_path = report_path.with_suffix(".json.tmp")
        temporary_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        temporary_path.replace(report_path)
        print(f"Assembly integrity: {status}. Report: {report_path}", flush=True)
        return 0 if status == "pass" else 2

    print(
        f"Loading {args.model} on {whisper_options()['device']}...",
        flush=True,
    )

    model = WhisperModel(
        args.model,
        **whisper_options(),
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

    expected_words = comparison_words(expected_text, pronunciations)
    actual_words = comparison_words(transcription, pronunciations)

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
