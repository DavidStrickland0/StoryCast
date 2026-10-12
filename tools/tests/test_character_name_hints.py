import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch

from verify_chapter_probe import verify_audio


class CharacterNameHintTests(unittest.TestCase):
    def verify(self, expected, first, hinted):
        model = SimpleNamespace(transcribe=Mock(side_effect=[
            ([SimpleNamespace(text=first)], None), ([SimpleNamespace(text=hinted)], None)]))
        quality = {'maximumSilenceSeconds': .1, 'leadingSilenceSeconds': .02, 'trailingSilenceSeconds': .02}
        with patch('torchaudio.load', return_value=(None, 24000)), patch('audio_postprocessing.analyze_audio', return_value=quality):
            report = verify_audio(model, Path('sample.wav'), expected, [], ['Selene'])
        return model, report

    def test_hint_corrects_name_and_records_both_transcripts(self):
        model, report = self.verify('Selene, cut it out!', 'Sling, cut it out.', 'Selene, cut it out.')
        self.assertEqual('pass', report['status'])
        self.assertEqual('transcript-character-name-hint', report['verificationMode'])
        self.assertEqual('Sling, cut it out.', report['unhintedTranscription'])
        self.assertEqual('Character names: Selene.', model.transcribe.call_args.kwargs['initial_prompt'])

    def test_hint_cannot_supply_omitted_name_or_change_other_dialogue(self):
        for first, hinted in [('Cut it out.', 'Selene, cut it out.'),
                              ('Sling, get it out.', 'Selene, cut it out.')]:
            with self.subTest(first=first):
                _, report = self.verify('Selene, cut it out!', first, hinted)
                self.assertEqual('fail', report['status'])
                self.assertFalse(report['nameHintAccepted'])

    def test_hint_cannot_correct_changed_money_amount(self):
        _, report = self.verify('Selene paid twelve dollars.', 'Celine paid thirteen dollars.', 'Selene paid twelve dollars.')
        self.assertEqual('fail', report['status'])
        self.assertFalse(report['nameHintAccepted'])

    def test_passed_clip_is_not_decoded_again(self):
        model, report = self.verify('Selene, cut it out!', 'Selene, cut it out.', 'unused')
        self.assertEqual('pass', report['status'])
        self.assertEqual(1, model.transcribe.call_count)
