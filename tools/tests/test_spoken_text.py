from __future__ import annotations

import sys
import unittest
from pathlib import Path


TOOLS_DIRECTORY = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS_DIRECTORY))

from spoken_text import currency_amounts, normalize_spoken_text


class SpokenTextTests(unittest.TestCase):
    def test_scene_dividers_are_silent_but_equations_are_preserved(self):
        self.assertEqual("Before.\n\nAfter.", normalize_spoken_text("Before.\n=====\nAfter."))
        self.assertEqual("A = B", normalize_spoken_text("A = B"))

    def test_ai_is_read_as_letter_names_without_changing_names_or_eye(self):
        self.assertEqual("The A I spoke. The A I's voice was calm.",
                         normalize_spoken_text("The AI spoke. The AI's voice was calm."))
        self.assertEqual("Ai raised an eye. Aiden AIPlayer ai", normalize_spoken_text("Ai raised an eye. Aiden AIPlayer ai"))
        from verify_voice_library import normalize_words
        self.assertEqual(normalize_words(normalize_spoken_text("The AI spoke")), normalize_words("The A I spoke"))
        self.assertNotEqual(normalize_words(normalize_spoken_text("The AI spoke")), normalize_words("The eye spoke"))

    def test_vip_is_read_as_letters_without_changing_handles(self):
        self.assertEqual("The V I P entrance. VIPPlayer V I P", normalize_spoken_text("The VIP entrance. VIPPlayer vip"))

    def test_spells_id_without_changing_contractions_or_names(self):
        self.assertEqual("Show your I D. I'd check it, but I'd rather wait.",
                         normalize_spoken_text("Show your ID. I'd check it, but I’d rather wait."))
        self.assertEqual("id Id identity ID123 GRID UserID",
                         normalize_spoken_text("id Id identity ID123 GRID UserID"))
        self.assertEqual("[User I D: twelve]", normalize_spoken_text("[User ID: 12]"))

    def test_curly_apostrophes_preserve_contractions_and_possessives(self):
        self.assertEqual("Maren's sure she won't leave 'em behind.",
                         normalize_spoken_text("Maren’s sure she won’t leave ‘em behind."))

    def test_spells_os_without_changing_words_or_pronouns(self) -> None:
        self.assertEqual("The O S works. I use it.", normalize_spoken_text("The OS works. I use it."))
        self.assertEqual("OSes Cosmos os", normalize_spoken_text("OSes Cosmos os"))

    def test_reads_world_war_roman_numerals_as_numbers(self) -> None:
        self.assertEqual(
            "World War one ended before World War two.",
            normalize_spoken_text("World War I ended before World War II."),
        )
        self.assertEqual("world war two", normalize_spoken_text("world war II"))
        text = "I saw an IV drip. II is a label. World War III."
        self.assertEqual(text, normalize_spoken_text(text))
        text = "The OS recorded World War II. I read it."
        normalized = normalize_spoken_text(text)
        self.assertEqual(normalized, normalize_spoken_text(normalized))

    def test_expands_complete_currency_before_generic_numbers(self) -> None:
        self.assertEqual(
            "He owed two thousand four hundred eighty-seven dollars and sixty-three cents.",
            normalize_spoken_text("He owed $2,487.63."),
        )
        self.assertEqual("one dollar and one cent", normalize_spoken_text("$1.01"))
        self.assertEqual("two dollars and fifty cents", normalize_spoken_text("$2.5"))
        self.assertEqual("one thousand six hundred forty-seven dollars", normalize_spoken_text("$1647"))

    def test_currency_values_are_exact_across_numeric_and_spoken_forms(self) -> None:
        self.assertEqual([248763], currency_amounts("He owes $2,487.63."))
        self.assertEqual([248763], currency_amounts("He owes two thousand four hundred and eighty-seven dollars and sixty-three cents."))
        self.assertEqual([248, 763], currency_amounts("He owes $2.48, $7.63."))

    def test_expands_room_number_before_sentence_period(self) -> None:
        self.assertEqual(
            "Room three one four. Dr. Thorne will see you now.",
            normalize_spoken_text("Room 314. Dr. Thorne will see you now."),
        )
        self.assertEqual(
            "Go to room three zero seven.",
            normalize_spoken_text("Go to room 307."),
        )

    def test_room_numbers_preserve_each_digit_and_leading_zero(self) -> None:
        self.assertEqual(
            "Room number zero three zero seven.",
            normalize_spoken_text("Room number 0307."),
        )
        self.assertEqual(
            "Room one eight six six.",
            normalize_spoken_text("Room 1866."),
        )

    def test_room_digit_transcription_matches_but_missing_zero_does_not(self) -> None:
        expected = normalize_spoken_text("room 307")
        self.assertEqual(expected, normalize_spoken_text("room three zero seven"))
        self.assertNotEqual(expected, normalize_spoken_text("room 37"))
        self.assertEqual(expected, normalize_spoken_text(expected))

    def test_non_room_cardinals_keep_hundreds(self) -> None:
        self.assertEqual(
            "He counted three hundred seven coins.",
            normalize_spoken_text("He counted 307 coins."),
        )

    def test_does_not_expand_parts_of_decimal_numbers(self) -> None:
        self.assertEqual("Value 12.5.", normalize_spoken_text("Value 12.5."))

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
