from __future__ import annotations

import argparse
import json
import re
import subprocess
from datetime import datetime, timezone
from pathlib import Path

from book_manifest import resolve_book_manifest

DEFAULT_BITRATE = "128k"


def load_json(path: Path) -> dict:
    """Load one UTF-8 JSON document."""
    with path.open(
        "r",
        encoding="utf-8-sig",
    ) as stream:
        return json.load(stream)


def write_json_atomic(
    path: Path,
    value: dict,
) -> None:
    """Write JSON through a temporary file."""
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


def chapter_number(
    chapter_id: str,
) -> int:
    """Extract a positive track number from a chapter identifier."""
    match = re.fullmatch(
        r"chapter-(\d+)",
        chapter_id,
        flags=re.IGNORECASE,
    )

    if match is None:
        raise ValueError(
            f"Unsupported chapter identifier: {chapter_id}"
        )

    number = int(match.group(1))

    if number < 1:
        raise ValueError(
            f"Chapter number must be positive: {chapter_id}"
        )

    return number


def chapter_title(
    book: Path,
    manifest: dict,
    track_number: int,
) -> str:
    """Read the chapter title from its first Markdown heading."""
    chapters = manifest["chapters"]

    if track_number > len(chapters):
        raise ValueError(
            f"Chapter track {track_number} exceeds the "
            f"{len(chapters)} configured chapters."
        )

    source_path = (
        book /
        chapters[track_number - 1]
    ).resolve()

    if not source_path.is_file():
        raise FileNotFoundError(
            f"Chapter source was not found: {source_path}"
        )

    with source_path.open(
        "r",
        encoding="utf-8-sig",
    ) as stream:
        for line in stream:
            match = re.match(
                r"^\s*#{1,6}\s+(.+?)\s*$",
                line,
            )

            if match is not None:
                return match.group(1).strip()

    return f"Chapter {track_number}"


def production_chapter_title(
    chapter_manifest: dict,
    track_number: int,
) -> str:
    """Read a title from persisted production source text."""
    source_text = "".join(
        segment.get("sourceText", "")
        for segment in chapter_manifest.get(
            "segments",
            [],
        )
    )

    for line in source_text.splitlines():
        candidate = line.strip()

        if not candidate:
            continue

        candidate = re.sub(
            r"^\s*#{1,6}\s*",
            "",
            candidate,
        ).strip()

        if candidate:
            return candidate

    return f"Chapter {track_number}"


def run_ffmpeg(    arguments: list[str],
) -> None:
    """Run FFmpeg and raise a descriptive error on failure."""
    result = subprocess.run(
        ["ffmpeg", *arguments],
        capture_output=True,
        text=True,
        check=False,
    )

    if result.returncode != 0:
        raise RuntimeError(
            f"FFmpeg failed with exit code "
            f"{result.returncode}:\n{result.stderr}"
        )


def main() -> int:
    """Export one mastered chapter as a tagged MP3."""
    parser = argparse.ArgumentParser(
        description=(
            "Export a mastered StoryCast chapter as MP3."
        )
    )
    parser.add_argument(
        "book",
        type=Path,
    )
    parser.add_argument(
        "chapter_id",
    )
    parser.add_argument(
        "mastered_audio",
        type=Path,
    )
    parser.add_argument(
        "--bitrate",
        default=DEFAULT_BITRATE,
    )

    args = parser.parse_args()

    book = args.book.resolve()
    book_manifest_path = resolve_book_manifest(book)
    mastered_audio = args.mastered_audio.resolve()

    if not book_manifest_path.is_file():
        raise FileNotFoundError(
            f"Book manifest was not found: "
            f"{book_manifest_path}"
        )

    if not mastered_audio.is_file():
        raise FileNotFoundError(
            f"Mastered chapter audio was not found: "
            f"{mastered_audio}"
        )

    manifest = load_json(book_manifest_path)
    track_number = chapter_number(args.chapter_id)
    track_total = len(manifest["chapters"])

    try:
        title = chapter_title(
            book,
            manifest,
            track_number,
        )
    except FileNotFoundError:
        chapter_manifest_path = (
            mastered_audio.parent /
            "chapter.json"
        )

        if not chapter_manifest_path.is_file():
            raise

        title = production_chapter_title(
            load_json(chapter_manifest_path),
            track_number,
        )

    final_directory = book / "output" / "final"
    final_directory.mkdir(
        parents=True,
        exist_ok=True,
    )

    output_path = (
        final_directory /
        f"{args.chapter_id}.mp3"
    )

    temporary_path = (
        final_directory /
        f".{args.chapter_id}.mp3.tmp"
    )

    temporary_path.unlink(missing_ok=True)

    ffmpeg_arguments = [
        "-hide_banner",
        "-loglevel",
        "error",
        "-y",
        "-i",
        str(mastered_audio),
    ]

    cover_value = manifest.get("cover")
    cover_path: Path | None = None

    if cover_value:
        cover_path = (
            book /
            str(cover_value)
        ).resolve()

        if not cover_path.is_file():
            raise FileNotFoundError(
                f"Configured cover image was not found: "
                f"{cover_path}"
            )

        ffmpeg_arguments.extend(
            [
                "-i",
                str(cover_path),
                "-map",
                "0:a:0",
                "-map",
                "1:v:0",
                "-c:v",
                "mjpeg",
                "-disposition:v:0",
                "attached_pic",
                "-metadata:s:v",
                "title=Cover",
                "-metadata:s:v",
                "comment=Cover (front)",
            ]
        )
    else:
        ffmpeg_arguments.extend(
            [
                "-map",
                "0:a:0",
            ]
        )

    artist = manifest.get(
        "artist",
        manifest["author"],
    )

    metadata = {
        "title": title,
        "track": f"{track_number}/{track_total}",
        "album": manifest["title"],
        "artist": artist,
        "album_artist": artist,
        "genre": manifest.get(
            "genre",
            "Audiobook",
        ),
        "language": manifest.get(
            "language",
            "en",
        ),
        "comment": (
            f"StoryCast chapter ID: {args.chapter_id}"
        ),
        "TCMP": "1",
    }

    publication_year = manifest.get(
        "publicationYear"
    )

    if publication_year is not None:
        metadata["date"] = str(publication_year)

    ffmpeg_arguments.extend(
        [
            "-c:a",
            "libmp3lame",
            "-b:a",
            args.bitrate,
            "-id3v2_version",
            "3",
            "-write_id3v1",
            "1",
        ]
    )

    for key, value in metadata.items():
        ffmpeg_arguments.extend(
            [
                "-metadata",
                f"{key}={value}",
            ]
        )

    ffmpeg_arguments.extend(
        [
            "-f",
            "mp3",
            str(temporary_path),
        ]
    )

    try:
        run_ffmpeg(ffmpeg_arguments)
        temporary_path.replace(output_path)
    finally:
        temporary_path.unlink(missing_ok=True)

    report = {
        "schemaVersion": 1,
        "generatedUtc": datetime.now(
            timezone.utc
        ).isoformat(),
        "bookId": manifest["id"],
        "chapterId": args.chapter_id,
        "title": title,
        "trackNumber": track_number,
        "trackTotal": track_total,
        "album": manifest["title"],
        "artist": artist,
        "genre": metadata["genre"],
        "language": metadata["language"],
        "publicationYear": publication_year,
        "bitrate": args.bitrate,
        "coverPath": (
            str(cover_path)
            if cover_path is not None
            else None
        ),
        "sourceAudioPath": str(mastered_audio),
        "outputPath": str(output_path),
    }

    report_path = (
        mastered_audio.parent /
        "chapter-export.json"
    )

    write_json_atomic(
        report_path,
        report,
    )

    print()
    print("Chapter MP3 export complete.")
    print(f"Chapter: {args.chapter_id}")
    print(f"Title:   {title}")
    print(
        f"Track:   {track_number}/{track_total}"
    )
    print(f"Audio:   {output_path}")
    print(f"Report:  {report_path}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
