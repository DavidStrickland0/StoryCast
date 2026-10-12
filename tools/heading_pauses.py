"""Detect source headings and preserve their pause boundaries for synthesis."""
from __future__ import annotations

import json
import re
from pathlib import Path
from book_manifest import resolve_book_manifest
from spoken_text import normalize_spoken_text
from synthesis_chunking import MAX_SYNTHESIS_CHARACTERS, split_synthesis_text, ends_at_sentence_boundary, split_chat_message

CHUNK_PAUSE_SECONDS = 0.12
HEADING_PAUSE_SECONDS = 1.25


def load_markdown_headings(
    book: Path,
    chapter_id: str,
) -> set[str]:
    """Load spoken headings from the chapter Markdown."""
    manifest = json.loads(resolve_book_manifest(book).read_text(encoding="utf-8-sig"))
    chapter_path: Path | None = None

    for index, chapter_value in enumerate(manifest["chapters"], 1):
        candidate = (book / str(chapter_value)).resolve()

        if candidate.stem.lower() == chapter_id.lower() or chapter_id.lower() == f"chapter-{index:03d}":
            chapter_path = candidate
            break

    if chapter_path is None or not chapter_path.is_file():
        return set()

    headings: set[str] = set()

    with chapter_path.open(
        "r",
        encoding="utf-8-sig",
    ) as stream:
        lines = list(stream)
        # Plain-text imports may put the number and title on consecutive
        # lines in the opening heading block. Do not consume later paragraphs.
        opening = "".join(lines).lstrip("\ufeff \t\r\n").split("\n\n", 1)[0]
        opening_lines = [line.strip() for line in opening.splitlines() if line.strip()]
        if (len(opening_lines) > 1 and
                re.fullmatch(r"Chapter\s+(?:\d+|[IVXLCDM]+)", opening_lines[0], re.IGNORECASE)):
            headings.update(opening_lines)
        first_line = True
        for line in lines:
            stripped = line.strip().lstrip('\ufeff')
            if first_line and stripped:
                first_line = False
                if re.match(r'^Chapter\s+(?:\d+|[IVXLCDM]+)\b', stripped, re.IGNORECASE):
                    headings.add(stripped)
            match = re.match(
                r"^[ \t]{0,3}#{1,6}[ \t]+"
                r"(.+?)[ \t]*#*[ \t]*$",
                line.rstrip("\r\n"),
            )

            if match:
                headings.add(match.group(1).strip())

    source_metadata = book / "source.json"
    if source_metadata.is_file():
        metadata = json.loads(source_metadata.read_text(encoding="utf-8-sig"))
        for entry in metadata.get("chapters", []):
            if (book / str(entry.get("path", ""))).resolve() == chapter_path:
                title = str(entry.get("title", "")).strip()
                if title:
                    headings.add(title)
                    headings.update(line.strip() for line in title.splitlines() if line.strip())
                break

    return headings


def split_synthesis_units(
    text: str,
    headings: set[str],
    max_characters: int = MAX_SYNTHESIS_CHARACTERS,
) -> list[tuple[str, float]]:
    """Split text and assign longer pauses after headings."""
    units: list[tuple[str, float]] = []
    ordinary_lines: list[str] = []
    spoken_headings = {normalize_spoken_text(heading).strip() for heading in headings}

    def flush_ordinary() -> None:
        ordinary_text = "\n".join(ordinary_lines).strip()
        ordinary_lines.clear()

        if not ordinary_text:
            return

        for chunk in split_synthesis_text(ordinary_text, max_characters=max_characters):
            pause = (
                CHUNK_PAUSE_SECONDS
                if ends_at_sentence_boundary(chunk)
                else 0.0
            )
            units.append((chunk, pause))

    first_content_line = True
    for line in text.splitlines():
        stripped = line.strip()
        if first_content_line and stripped:
            first_content_line = False
            for heading in sorted(headings | spoken_headings, key=len, reverse=True):
                if stripped.startswith(heading) and stripped[len(heading):len(heading) + 1].isspace():
                    flush_ordinary()
                    units.append((heading, HEADING_PAUSE_SECONDS))
                    stripped = stripped[len(heading):].strip()
                    line = stripped
                    break
        chat_chunks = split_chat_message(stripped, max_characters)

        if chat_chunks is not None:
            flush_ordinary()
            units.extend((chunk, CHUNK_PAUSE_SECONDS) for chunk in chat_chunks)
        elif stripped and (stripped in headings or stripped in spoken_headings):
            flush_ordinary()
            units.append((stripped, HEADING_PAUSE_SECONDS))
        else:
            ordinary_lines.append(line)

    flush_ordinary()
    return units


