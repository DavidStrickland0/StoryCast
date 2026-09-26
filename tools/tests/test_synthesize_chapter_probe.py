from __future__ import annotations

import sys
import unittest
from pathlib import Path


TOOLS_DIRECTORY = Path(__file__).resolve().parents[1]

sys.path.insert(
    0,
    str(TOOLS_DIRECTORY),
)

from synthesize_chapter_probe import (
    parse_chunk_selector,
    split_synthesis_text,
)


class SynthesisTextChunkTests(unittest.TestCase):
    def test_preserves_short_text(self) -> None:
        text = "A short sentence."

        self.assertEqual(
            [text],
            split_synthesis_text(text),
        )

    def test_splits_long_text_at_sentence_boundaries(
        self,
    ) -> None:
        text = (
            "This sentence contains enough words for testing. "
            "The next sentence also needs to remain intact. "
        ) * 12

        chunks = split_synthesis_text(
            text.strip(),
            max_characters=140,
        )

        self.assertGreater(
            len(chunks),
            1,
        )

        self.assertLessEqual(
            max(map(len, chunks)),
            140,
        )

        self.assertEqual(
            text.replace(" ", "").strip(),
            "".join(chunks).replace(" ", ""),
        )

    def test_splits_unbroken_text_at_hard_limit(
        self,
    ) -> None:
        text = "x" * 701

        chunks = split_synthesis_text(
            text,
            max_characters=280,
        )

        self.assertEqual(
            [280, 280, 141],
            [len(chunk) for chunk in chunks],
        )

        self.assertEqual(
            text,
            "".join(chunks),
        )

    def test_rejects_nonpositive_limit(self) -> None:
        with self.assertRaises(ValueError):
            split_synthesis_text(
                "Text",
                max_characters=0,
            )

    def test_parses_chunk_selector(self) -> None:
        self.assertEqual((12, 3), parse_chunk_selector("12:3"))


if __name__ == "__main__":
    unittest.main()
