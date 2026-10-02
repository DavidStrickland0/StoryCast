from __future__ import annotations

import unittest

from verify_chapter_probe import classify_verification


class VerificationClassificationTests(unittest.TestCase):
    def test_rejects_inaccurate_one_word_utterance(self) -> None:
        status, mode = classify_verification(
            ["sak"],
            ["sock"],
            1.0,
        )

        self.assertEqual("fail", status)
        self.assertEqual(
            "transcript",
            mode,
        )

    def test_accepts_exact_one_word_utterance(self) -> None:
        status, mode = classify_verification(
            ["miller"],
            ["miller"],
            0.0,
        )

        self.assertEqual("pass", status)
        self.assertEqual(
            "transcript",
            mode,
        )
    def test_rejects_silent_short_utterance(self) -> None:
        status, mode = classify_verification(
            ["sak"],
            [],
            1.0,
        )

        self.assertEqual("fail", status)
        self.assertEqual("transcript", mode)

    def test_keeps_long_dialogue_strict(self) -> None:
        status, mode = classify_verification(
            [
                "this",
                "is",
                "ordinary",
                "dialogue",
                "spoken",
                "clearly",
            ],
            ["completely", "different"],
            1.0,
        )

        self.assertEqual("fail", status)
        self.assertEqual("transcript", mode)

    def test_short_fragment_above_review_threshold_fails(self) -> None:
        status, mode = classify_verification(
            ["elias", "said", "his", "tone", "sharp"],
            ["elias", "said", "tone"],
            0.40,
        )

        self.assertEqual("fail", status)
        self.assertEqual(
            "transcript",
            mode,
        )

    def test_short_and_long_fragments_share_review_threshold(self) -> None:
        for expected, actual in [
            (["one", "two", "three", "four", "five"],
             ["one", "two", "three", "four", "six"]),
            (["one"] * 10, ["one"] * 9 + ["two"]),
        ]:
            with self.subTest(word_count=len(expected)):
                self.assertEqual(
                    ("review", "transcript"),
                    classify_verification(expected, actual, 0.20),
                )

    def test_rejects_repeated_short_utterance(self) -> None:
        status, mode = classify_verification(
            ["do", "something"],
            ["do", "something", "do", "something"],
            1.0,
        )

        self.assertEqual("fail", status)
        self.assertEqual(
            "repeated-utterance",
            mode,
        )

if __name__ == "__main__":
    unittest.main()
