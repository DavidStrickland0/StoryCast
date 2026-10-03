from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from book_manifest import find_book_root, resolve_book_manifest


class BookManifestTests(unittest.TestCase):
    def test_shared_production_manifest_is_found_from_chapter_output(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            book = Path(directory)
            production = book / "production"
            production.mkdir()
            manifest = production / "book.json"
            manifest.write_text("{}", encoding="utf-8")
            output = book / "output" / "chapter-001"
            output.mkdir(parents=True)

            self.assertEqual(manifest, resolve_book_manifest(book))
            self.assertEqual(book, find_book_root(output))

    def test_root_manifest_remains_supported(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            book = Path(directory)
            manifest = book / "book.json"
            manifest.write_text("{}", encoding="utf-8")

            self.assertEqual(manifest, resolve_book_manifest(book))
