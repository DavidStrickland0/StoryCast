from __future__ import annotations

import sys
import unittest
from pathlib import Path


TOOLS_DIRECTORY = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS_DIRECTORY))

from spoken_text import normalize_spoken_text


class SpokenTextTests(unittest.TestCase):
    def test_expands_date_and_year(self) -> None:
        self.assertEqual(
            "On March third, eighteen sixty-six, we left.",
            normalize_spoken_text(
                "On March 3, 1866, we left."
            ),
        )

    def test_expands_clock_times(self) -> None:
        self.assertEqual(
            "Meet at nine oh five A M and leave at three o'clock.",
            normalize_spoken_text(
                "Meet at 9:05 a.m. and leave at 3:00."
            ),
        )

    def test_spells_hp_as_letters(self) -> None:
        self.assertEqual(
            "The H P laptop has sixteen gigabytes.",
            normalize_spoken_text(
                "The HP laptop has 16 gigabytes."
            ),
        )

    def test_expands_ordinals_and_common_integers(self) -> None:
        self.assertEqual(
            "The twenty-first room held one hundred five boxes.",
            normalize_spoken_text(
                "The 21st room held 105 boxes."
            ),
        )


if __name__ == "__main__":
    unittest.main()
