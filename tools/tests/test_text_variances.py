import unittest
from verify_voice_library import normalize_words
from spoken_text import normalize_spoken_text


class VarianceTests(unittest.TestCase):
    def test_at_symbol_matches_word(self):
        self.assertEqual(normalize_words('Meet @ home.'), normalize_words('Meet at home.'))

    def test_clock_notation_matches_dotted_spelling(self):
        for first, second in [('3am', '3 A.M.'), ('3 am', '3 A.M.'), ('3pm', '3 P.M.'), ('3:15am', '3:15 A.M.')]:
            self.assertEqual(normalize_words(first), normalize_words(second))
            self.assertEqual(normalize_words(normalize_spoken_text(first)), normalize_words(normalize_spoken_text(second)))
        self.assertNotEqual(normalize_words('3am'), normalize_words('3pm'))
        self.assertNotEqual(normalize_words('I am here'), normalize_words('I A.M. here'))
