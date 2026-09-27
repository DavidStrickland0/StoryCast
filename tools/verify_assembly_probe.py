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

    # The assembly manifest supplies exact offsets for the audio that was
    # concatenated. Transcribing the entire chapter in one pass can make
    # Whisper drift or repeat text after many minutes of speech.
    import torchaudio

    chapter_segments = {
        item["index"]: item
        for item in chapter_manifest["segments"]
    }
    timed_segments = sorted(
        assembly["segments"],
        key=lambda item: item["segmentIndex"],
    )
    if not timed_segments:
        raise ValueError("Assembly has no timed segments.")

    windows = []
    current = []
    for item in timed_segments:
        if item["segmentIndex"] not in chapter_segments:
            raise ValueError(
                f"Missing source text for segment {item['segmentIndex']}."
            )
        if current and (
            item["endSeconds"] - current[0]["startSeconds"] > 30.0
        ):
            windows.append(current)
            current = []
        current.append(item)
    if current:
        windows.append(current)

    audio_info = torchaudio.info(str(audio_path))
    expected_rate = assembly["sampleRate"]
    if audio_info.sample_rate != expected_rate:
        raise ValueError(
            f"Audio sample rate {audio_info.sample_rate} does not match "
            f"assembly rate {expected_rate}."
        )

    results = []
    all_transcriptions = []
    distance = 0
    expected_count = 0
    actual_count = 0
    for number, items in enumerate(windows, start=1):
        start_frame = round(items[0]["startSeconds"] * expected_rate)
        end_frame = round(items[-1]["endSeconds"] * expected_rate)
        if start_frame < 0 or end_frame > audio_info.num_frames:
            raise ValueError(
                f"Window {number} is outside the assembled audio."
            )
        waveform, sample_rate = torchaudio.load(
            str(audio_path),
            frame_offset=start_frame,
            num_frames=end_frame - start_frame,
        )
        waveform = waveform.mean(dim=0, keepdim=True)
        if sample_rate != 16000:
            waveform = torchaudio.functional.resample(
                waveform, sample_rate, 16000
            )
        transcription_segments, _ = model.transcribe(
            waveform.squeeze(0).numpy(),
            language="en",
            beam_size=5,
            vad_filter=True,
            condition_on_previous_text=False,
            temperature=0.0,
            word_timestamps=True,
            hallucination_silence_threshold=1.0,
        )
        heard = " ".join(
            segment.text.strip()
            for segment in transcription_segments
        ).strip()
        expected = "".join(
            chapter_segments[item["segmentIndex"]]["sourceText"]
            for item in items
        )
        expected_window_words = normalize_words(expected)
        heard_words = normalize_words(heard)
        window_distance = edit_distance(
            expected_window_words, heard_words
        )
        window_rate = (
            window_distance / len(expected_window_words)
            if expected_window_words else 1.0
        )
        results.append({
            "windowIndex": number,
            "firstSegmentIndex": items[0]["segmentIndex"],
            "lastSegmentIndex": items[-1]["segmentIndex"],
            "startSeconds": items[0]["startSeconds"],
            "endSeconds": items[-1]["endSeconds"],
            "expectedWordCount": len(expected_window_words),
            "transcribedWordCount": len(heard_words),
            "editDistance": window_distance,
            "wordErrorRate": round(window_rate, 6),
            "transcription": heard,
        })
        distance += window_distance
        expected_count += len(expected_window_words)
        actual_count += len(heard_words)
        all_transcriptions.append(heard)
        print(
            f"Window {number}/{len(windows)}: "
            f"segments {items[0]['segmentIndex']}-"
            f"{items[-1]['segmentIndex']}, "
            f"WER {window_rate:.1%}",
            flush=True,
        )

    transcription = " ".join(all_transcriptions).strip()
    expected_words = normalize_words(expected_text)
    if expected_count != len(expected_words):
        raise ValueError(
            "Window text does not match the complete chapter text."
        )
    word_error_rate = distance / expected_count if expected_count else 1.0

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
        "transcribedWordCount": actual_count,
        "editDistance": distance,
        "wordErrorRate": round(
            word_error_rate,
            6,
        ),
        "expectedText": expected_text,
        "transcription": transcription,
        "audioPath": str(audio_path),
        "windows": results,
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
    print(f"Actual words:   {actual_count}")
    print(f"Edit distance:  {distance}")
    print(f"Word error rate: {word_error_rate:.1%}")
    print(f"Status:          {status}")
    print(f"Report:          {report_path}")

    return 0 if status == "pass" else 2


if __name__ == "__main__":
    raise SystemExit(main())
