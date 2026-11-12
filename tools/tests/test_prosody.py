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
        self.assertEqual(0.7, settings["exaggeration"])
        self.assertEqual(0.5, settings["cfgWeight"])
        self.assertEqual(0.7, settings["temperature"])

    def test_reduces_whispering_intensity(self) -> None:
        settings = resolve_segment_synthesis_settings(
            BASELINE,
            "kaelen",
            "They are already here.",
            "whispered",
        )

        self.assertEqual("whispering", settings["profile"])
        self.assertEqual(0.6, settings["exaggeration"])
        self.assertEqual(0.5, settings["cfgWeight"])
        self.assertEqual(0.7, settings["temperature"])

    def test_free_form_delivery_keeps_voice_controls_near_cast_baseline(self):
        for intensity in (-0.3, 0.2):
            settings = resolve_segment_synthesis_settings(
                BASELINE, "kaelen", "Stay here.", "emotional",
                {"direction": "emotional", "adjustments": {
                    "exaggeration": intensity, "cfgWeight": -0.1,
                    "temperature": 0.1}})
            self.assertLessEqual(abs(settings["exaggeration"] - 0.65), 0.050001)
            self.assertEqual(0.5, settings["cfgWeight"])
            self.assertEqual(0.7, settings["temperature"])
            self.assertEqual("stable-base-v1", settings["voicePolicy"])

    def test_detects_urgent_punctuation(self) -> None:
        self.assertEqual(
            "urgent-afraid",
            classify_delivery("Run!"),
        )


if __name__ == "__main__":
    unittest.main()
