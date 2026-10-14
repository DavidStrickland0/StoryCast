import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from verify_voice_library import normalize_words, edit_distance
from verify_chapter_probe import classify_verification


class VerificationSpellingTests(unittest.TestCase):
    def test_remaining_chapter_chunks_pass_equivalent_spoken_spellings(self):
        pairs = [
            ('"Likely taken by goblins. Maybe sold to a merchant nearby."',
             'likely taken by goblins, may be sold to a merchant nearby.'),
            ('He gestured toward the treeline beyond the farmhouses.',
             'He gestured toward the tree line beyond the farmhouses.'),
            ("It was tucked under straw near the doorframe. The goblins must've used it to drag something heavy out quickly.",
             'It was tucked under straw near the door frame. The goblins must have used it to drag something heavy out quickly.'),
        ]
        for expected, heard in pairs:
            with self.subTest(expected=expected):
                old, new = normalize_words(expected), normalize_words(heard)
                self.assertEqual(old, new)
                wer = edit_distance(old, new) / len(old)
                self.assertEqual(('pass', 'transcript'), classify_verification(old, new, wer, expected, heard))

    def test_missing_changed_and_repeated_speech_remains_distinct(self):
        for expected, heard in [
            ('The elder nodded approvingly.', 'approvingly.'),
            ('They must have left.', 'They must not have left.'),
            ('maybe sold', 'sold'),
            ('nowhere', 'now here'),
            ("he'd left", 'he would left'),
            ('tree line', 'tree line tree line'),
        ]:
            with self.subTest(expected=expected):
                self.assertNotEqual(normalize_words(expected), normalize_words(heard))

    def test_curly_contractions_and_plurals(self):
        self.assertEqual(normalize_words('They must’ve crossed the treelines near the doorframes.'),
                         normalize_words('They must have crossed the tree lines near the door frames.'))


if __name__ == '__main__':
    unittest.main()
