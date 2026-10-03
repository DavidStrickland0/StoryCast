from __future__ import annotations

import sys
import unittest
from pathlib import Path


TOOLS_DIRECTORY = Path(__file__).resolve().parents[1]

sys.path.insert(
    0,
    str(TOOLS_DIRECTORY),
)

from synthesis_chunking import (
    ends_at_sentence_boundary,
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

    def test_prefers_early_sentence_end_over_mid_sentence_cut(self) -> None:
        text = (
            "You had to be the guy who got the job done, the one who held "
            "the line while the rest of them scrambled for cover. "
            "I was a Marine. That was the identity I had built for myself, "
            "brick by brick, since I was eighteen and too young to know "
            "better and old enough to realize that the world didn't care "
            "about my feelings."
        )

        chunks = split_synthesis_text(text)

        self.assertEqual(
            text.replace(" ", ""),
            "".join(chunks).replace(" ", ""),
        )
        self.assertTrue(
            all(ends_at_sentence_boundary(chunk) for chunk in chunks)
        )
        self.assertLessEqual(max(map(len, chunks)), 280)

    def test_reports_non_sentence_boundary_without_pause(self) -> None:
        self.assertFalse(ends_at_sentence_boundary("the world didn't"))
        self.assertTrue(ends_at_sentence_boundary("The world didn't care."))

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
