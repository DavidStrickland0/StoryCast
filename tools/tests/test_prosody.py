from __future__ import annotations

import unittest

from prosody import (
    classify_delivery,
    resolve_segment_synthesis_settings,
)


BASELINE = {
    "synthesis": {
        "exaggeration": 0.65,
        "cfgWeight": 0.5,
        "temperature": 0.7,
    }
}


class ProsodyTests(unittest.TestCase):
    def test_preserves_neutral_baseline(self) -> None:
        settings = resolve_segment_synthesis_settings(
            BASELINE,
            "kaelen",
            "I understand.",
        )

        self.assertEqual("neutral", settings["profile"])
        self.assertEqual(0.65, settings["exaggeration"])
        self.assertEqual(0.5, settings["cfgWeight"])
        self.assertEqual(0.7, settings["temperature"])

    def test_increases_shouting_intensity(self) -> None:
        settings = resolve_segment_synthesis_settings(
            BASELINE,
            "elias",
            "Get away from her!",
            "shouted angrily",
        )

        self.assertEqual(
            "angry-shouting",
            settings["profile"],
        )
        self.assertEqual(0.85, settings["exaggeration"])
        self.assertEqual(0.45, settings["cfgWeight"])
        self.assertEqual(0.75, settings["temperature"])

    def test_reduces_whispering_intensity(self) -> None:
        settings = resolve_segment_synthesis_settings(
            BASELINE,
            "kaelen",
            "They are already here.",
            "whispered",
        )

        self.assertEqual("whispering", settings["profile"])
        self.assertEqual(0.5, settings["exaggeration"])
        self.assertEqual(0.55, settings["cfgWeight"])
        self.assertEqual(0.6, settings["temperature"])

    def test_detects_urgent_punctuation(self) -> None:
        self.assertEqual(
            "urgent-afraid",
            classify_delivery("Run!"),
        )


if __name__ == "__main__":
    unittest.main()
