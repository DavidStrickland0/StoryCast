from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from export_chapter import (
    chapter_number,
    chapter_title,
)


class ChapterExportTests(unittest.TestCase):
    def test_extracts_chapter_number(self) -> None:
        self.assertEqual(
            12,
            chapter_number("chapter-012"),
        )

    def test_reads_first_markdown_heading(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            book = Path(directory)
            chapter = book / "chapter.md"

            chapter.write_text(
                "# Chapter 1: The Beginning\n\nText.\n",
                encoding="utf-8",
            )

            title = chapter_title(
                book,
                {
                    "chapters": [
                        "chapter.md",
                    ]
                },
                1,
            )

            self.assertEqual(
                "Chapter 1: The Beginning",
                title,
            )

    def test_uses_fallback_without_heading(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            book = Path(directory)
            chapter = book / "chapter.md"

            chapter.write_text(
                "Text without a heading.\n",
                encoding="utf-8",
            )

            title = chapter_title(
                book,
                {
                    "chapters": [
                        "chapter.md",
                    ]
                },
                1,
            )

            self.assertEqual(
                "Chapter 1",
                title,
            )


if __name__ == "__main__":
    unittest.main()