import unittest
from verify_voice_library import normalize_words, edit_distance


class TranscriptCompoundTests(unittest.TestCase):
    def test_adjectival_question_contraction_preserves_meaning(self):
        self.assertEqual(normalize_words("Why's she still so bad at mornings?"),
                         normalize_words("Why is she still so bad at mornings?"))
        for changed in ("Why is she still so good at mornings?", "Why is he still so bad at mornings?",
                        "Why is she not so bad at mornings?", "Why is she so bad at mornings?"):
            self.assertNotEqual(normalize_words("Why's she still so bad at mornings?"), normalize_words(changed))
        self.assertNotEqual(normalize_words("Why's she left?"), normalize_words("Why is she left?"))
    def test_nasal_hesitation_spellings_preserve_the_rest_of_the_sentence(self):
        self.assertEqual(normalize_words('Hm, both commands work.'), normalize_words('Mmm, both commands work.'))
        self.assertNotEqual(normalize_words('Hm'), normalize_words('mhm'))
        self.assertEqual(['3', 'mm'], normalize_words('3 mm'))
        self.assertGreater(edit_distance(normalize_words('Hm, both commands work.'), normalize_words('Mmm, commands work.')), 0)
    def test_text_box_and_dialog_spelling_are_equivalent(self):
        self.assertEqual(normalize_words('A textbox and a dialogue box'), normalize_words('A text box and a dialog box'))

    def test_time_spelling_and_jrpg_letters(self):
        from spoken_text import normalize_spoken_text
        self.assertEqual('seven oh five P M', normalize_spoken_text('7.05pm'))
        self.assertEqual('J R P G', normalize_spoken_text('JRPG'))
        self.assertEqual('7.05 dollars', normalize_spoken_text('7.05 dollars'))
    def test_primordial_creator_title_and_face_down_are_equivalent(self):
        expected = "Chapter one – Boyfailure. I'm lying facedown on the floor."
        actual = "Chapter 1 Boy Failure. I'm lying face down on the floor."
        self.assertEqual(normalize_words(expected), normalize_words(actual))

    def test_chapter_five_spelling_difference_has_zero_errors(self):
        expected = normalize_words("In this mode, most lifeforms will die from a single shot.")
        actual = normalize_words("In this mode, most life forms will die from a single shot.")
        self.assertEqual(expected, actual)
        self.assertEqual(0, edit_distance(expected, actual))

    def test_known_compounds_normalize_in_both_directions(self):
        for closed, opened in (("medlab", "med lab"), ("bloodstain", "blood stain"), ("setup", "set up"),
                               ("SciFi", "sci-fi"), ("microcams", "micro cams")):
            with self.subTest(closed=closed):
                self.assertEqual(normalize_words(closed), normalize_words(opened))

    def test_changed_meaning_names_numbers_and_missing_words_still_differ(self):
        for expected, actual in (("nowhere", "now here"), ("Jenny", "Denny"),
                                 ("lifeforms will die", "life forms will not die"),
                                 ("twelve dollars", "thirteen dollars"), ("a single shot", "a shot")):
            with self.subTest(expected=expected):
                self.assertGreater(edit_distance(normalize_words(expected), normalize_words(actual)), 0)

    def test_police_slang_joined_spelling_preserves_surrounding_words(self):
        self.assertEqual(normalize_words('The po po are almost here'), normalize_words('The Popo are almost here'))
        self.assertNotEqual(normalize_words('The po po are almost here'), normalize_words('The Popo are not here'))
