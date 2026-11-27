from __future__ import annotations

import tempfile
import json
import unittest
from pathlib import Path

from export_chapter import (
    chapter_number,
    chapter_title,
    final_mp3_path,
)


class ChapterExportTests(unittest.TestCase):
    def test_partial_book_uses_title_numbers_without_collisions(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            book = Path(directory)
            titles = ["Chapter 19: Arrival", "Chapter 21 The Gate", "Chapter 21: Part Two", "Interlude"]
            paths = [f"{index}.txt" for index in range(len(titles))]
            (book / "book.json").write_text(json.dumps({"chapters": paths}), encoding="utf-8")
            (book / "source.json").write_text(json.dumps({
                "sourceUrl": "https://novgo.net/example.html",
                "chapters": [{"path": path, "title": title} for path, title in zip(paths, titles)],
            }), encoding="utf-8")
            self.assertEqual(
                ["chapter-019.mp3", "chapter-021.mp3", "chapter-021-track-003.mp3", "chapter-track-004.mp3"],
                [final_mp3_path(book, f"chapter-{index:03d}").name for index in range(1, 5)],
            )

    def test_preserves_captured_page_titles_in_partial_book(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            book = Path(directory)
            for title in ("Chapter 47: Arrival", "Prologue", "Epilogue: Home", "Interlude 2"):
                with self.subTest(title=title):
                    (book / "00001.txt").write_text(title + "\n\nStory text.\n", encoding="utf-8")
                    (book / "source.json").write_text(json.dumps({"chapters": [
                        {"path": "other.txt", "title": "Wrong chapter"},
                        {"path": "00001.txt", "title": title},
                    ]}), encoding="utf-8")
                    self.assertEqual(title, chapter_title(book, {"chapters": ["00001.txt"]}, 1))

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
