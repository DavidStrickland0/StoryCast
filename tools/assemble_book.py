from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path


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


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()

    with path.open("rb") as stream:
        for block in iter(
            lambda: stream.read(1024 * 1024),
            b"",
        ):
            digest.update(block)

    return digest.hexdigest()


def run(
    arguments: list[str],
) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(
        arguments,
        capture_output=True,
        text=True,
        check=False,
    )

    if result.returncode != 0:
        raise RuntimeError(
            f"{arguments[0]} failed with exit code "
            f"{result.returncode}:\n{result.stderr}"
        )

    return result


def probe_audio(path: Path) -> dict:
    result = run(
        [
            "ffprobe",
            "-v",
            "error",
            "-select_streams",
            "a:0",
            "-show_entries",
            (
                "stream=sample_rate,channels,"
                "channel_layout,codec_name:"
                "format=duration"
            ),
            "-of",
            "json",
            str(path),
        ]
    )

    payload = json.loads(result.stdout)
    streams = payload.get("streams", [])

    if len(streams) != 1:
        raise RuntimeError(
            f"Expected one audio stream: {path}"
        )

    stream = streams[0]
    format_data = payload.get("format", {})

    return {
        "sampleRate": int(stream["sample_rate"]),
        "channels": int(stream["channels"]),
        "channelLayout":
            stream.get("channel_layout") or
            (
                "mono"
                if int(stream["channels"]) == 1
                else "stereo"
            ),
        "codecName": stream["codec_name"],
        "durationSeconds": float(
            format_data["duration"]
        ),
    }


def concat_escape(path: Path) -> str:
    return str(path).replace(
        "'",
        "'\\''",
    )


def main() -> int:
    parser = argparse.ArgumentParser(
        description=(
            "Assemble mastered chapters into one audiobook WAV."
        )
    )
    parser.add_argument(
        "request",
        type=Path,
    )
    args = parser.parse_args()

    request_path = args.request.resolve()
    request = load_json(request_path)

    if request.get("schemaVersion") != 1:
        raise RuntimeError(
            "Unsupported book assembly request schema."
        )

    chapters = sorted(
        request["chapters"],
        key=lambda chapter: chapter["index"],
    )

    if not chapters:
        raise RuntimeError(
            "Book assembly request contains no chapters."
        )

    for expected_index, chapter in enumerate(chapters):
        if chapter["index"] != expected_index:
            raise RuntimeError(
                "Book chapters must use sequential indexes."
            )

    run_directory = Path(
        request["runDirectory"]
    ).resolve()

    if not run_directory.is_dir():
        raise FileNotFoundError(
            f"Book run directory was not found: "
            f"{run_directory}"
        )

    chapter_pause = float(
        request.get(
            "chapterPauseSeconds",
            1.0,
        )
    )

    if chapter_pause < 0:
        raise ValueError(
            "Chapter pause cannot be negative."
        )

    chapter_records: list[dict] = []
    expected_format: dict | None = None
    cursor_seconds = 0.0

    for chapter in chapters:
        audio_path = Path(
            chapter["audioPath"]
        ).resolve()

        if not audio_path.is_file():
            raise FileNotFoundError(
                f"Mastered chapter audio was not found: "
                f"{audio_path}"
            )

        audio_format = probe_audio(audio_path)

        compatibility = {
            "sampleRate": audio_format["sampleRate"],
            "channels": audio_format["channels"],
            "channelLayout":
                audio_format["channelLayout"],
            "codecName": audio_format["codecName"],
        }

        if expected_format is None:
            expected_format = compatibility
        elif compatibility != expected_format:
            raise RuntimeError(
                f"Chapter {chapter['chapterId']} uses "
                f"incompatible audio format: {compatibility}; "
                f"expected {expected_format}."
            )

        start_seconds = cursor_seconds
        end_seconds = (
            start_seconds +
            audio_format["durationSeconds"]
        )

        chapter_records.append(
            {
                "index": chapter["index"],
                "chapterId": chapter["chapterId"],
                "audioPath": str(audio_path),
                "audioSha256":
                    sha256_file(audio_path),
                "startSeconds": start_seconds,
                "endSeconds": end_seconds,
                "durationSeconds":
                    audio_format["durationSeconds"],
            }
        )

        cursor_seconds = end_seconds

        if chapter["index"] < len(chapters) - 1:
            cursor_seconds += chapter_pause

    if expected_format is None:
        raise RuntimeError(
            "Unable to determine audiobook format."
        )

    silence_path = (
        run_directory /
        ".chapter-pause.wav"
    )

    concat_path = (
        run_directory /
        ".book-concat.txt"
    )

    output_path = (
        run_directory /
        f"{request['bookId']}.mastered.wav"
    )

    temporary_output_path = (
        run_directory /
        f".{request['bookId']}.mastered.wav.tmp"
    )

    temporary_output_path.unlink(
        missing_ok=True
    )

    concat_entries: list[Path] = []

    if chapter_pause > 0 and len(chapters) > 1:
        run(
            [
                "ffmpeg",
                "-hide_banner",
                "-loglevel",
                "error",
                "-y",
                "-f",
                "lavfi",
                "-i",
                (
                    f"anullsrc="
                    f"r={expected_format['sampleRate']}:"
                    f"cl={expected_format['channelLayout']}"
                ),
                "-t",
                str(chapter_pause),
                "-c:a",
                expected_format["codecName"],
                str(silence_path),
            ]
        )

    for position, chapter in enumerate(
        chapter_records
    ):
        concat_entries.append(
            Path(chapter["audioPath"])
        )

        if (
            position < len(chapter_records) - 1 and
            chapter_pause > 0
        ):
            concat_entries.append(silence_path)

    with concat_path.open(
        "w",
        encoding="utf-8",
        newline="\n",
    ) as stream:
        for entry in concat_entries:
            stream.write(
                f"file '{concat_escape(entry)}'\n"
            )

    try:
        run(
            [
                "ffmpeg",
                "-hide_banner",
                "-loglevel",
                "error",
                "-y",
                "-f",
                "concat",
                "-safe",
                "0",
                "-i",
                str(concat_path),
                "-c",
                "copy",
                "-f",
                "wav",
                str(temporary_output_path),
            ]
        )

        temporary_output_path.replace(
            output_path
        )
    finally:
        concat_path.unlink(missing_ok=True)
        silence_path.unlink(missing_ok=True)
        temporary_output_path.unlink(
            missing_ok=True
        )

    output_format = probe_audio(output_path)

    manifest = {
        "schemaVersion": 1,
        "generatedUtc": datetime.now(
            timezone.utc
        ).isoformat(),
        "bookId": request["bookId"],
        "title": request["title"],
        "author": request["author"],
        "chapterPauseSeconds": chapter_pause,
        "sampleRate":
            output_format["sampleRate"],
        "channels":
            output_format["channels"],
        "durationSeconds":
            output_format["durationSeconds"],
        "audioPath": str(output_path),
        "audioSha256":
            sha256_file(output_path),
        "chapters": chapter_records,
    }

    manifest_path = (
        run_directory /
        "book-assembly.json"
    )

    write_json_atomic(
        manifest_path,
        manifest,
    )

    print("Audiobook assembly complete.")
    print(f"Book:      {request['title']}")
    print(f"Chapters:  {len(chapters)}")
    print(f"Pause:     {chapter_pause:.2f} seconds")
    print(
        f"Duration:  "
        f"{output_format['durationSeconds']:.2f} seconds"
    )
    print(f"Audio:     {output_path}")
    print(f"Manifest:  {manifest_path}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())