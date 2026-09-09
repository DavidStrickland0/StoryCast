from __future__ import annotations

import unittest

from verify_chapter_probe import classify_verification


class VerificationClassificationTests(unittest.TestCase):
    def test_passes_audible_one_word_utterance(self) -> None:
        status, mode = classify_verification(
            ["sak"],
            ["sock"],
            1.0,
        )

        self.assertEqual("pass", status)
        self.assertEqual(
            "audibility-short-utterance",
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

    def test_passes_audible_five_word_fragment(self) -> None:
        status, mode = classify_verification(
            ["elias", "said", "his", "tone", "sharp"],
            ["elias", "said", "tone"],
            0.40,
        )

        self.assertEqual("pass", status)
        self.assertEqual(
            "audibility-short-utterance",
            mode,
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