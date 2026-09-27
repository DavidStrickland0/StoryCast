from __future__ import annotations

import sys
import unittest
from pathlib import Path

import torch


TOOLS_DIRECTORY = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS_DIRECTORY))

from audio_postprocessing import analyze_audio, polish_audio


class AudioPostprocessingTests(unittest.TestCase):
    def test_trims_edges_and_compresses_long_internal_silence(self) -> None:
        sample_rate = 1000
        speech = torch.full((1, 500), 0.2)
        audio = torch.cat(
            [
                torch.zeros((1, 800)),
                speech,
                torch.zeros((1, 3000)),
                speech,
                torch.zeros((1, 900)),
            ],
            dim=-1,
        )

        polished, report = polish_audio(audio, sample_rate)

        self.assertLess(polished.shape[-1], audio.shape[-1] - 2500)
        self.assertGreater(polished.shape[-1], 1400)
        self.assertLessEqual(
            report["after"]["maximumSilenceSeconds"],
            0.5,
        )
        self.assertLessEqual(
            report["after"]["leadingSilenceSeconds"],
            0.04,
        )
        self.assertLessEqual(
            report["after"]["trailingSilenceSeconds"],
            0.04,
        )

    def test_analysis_reports_pathological_pause(self) -> None:
        sample_rate = 1000
        audio = torch.cat(
            [
                torch.ones((1, 100)),
                torch.zeros((1, 2700)),
                torch.ones((1, 100)),
            ],
            dim=-1,
        )

        report = analyze_audio(audio, sample_rate)

        self.assertEqual(2.7, report["maximumSilenceSeconds"])


if __name__ == "__main__":
    unittest.main()
