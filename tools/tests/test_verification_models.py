import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from verification_models import VerificationModels
from produce_chapter import PersistentVerificationRunner


class VerificationModelsTests(unittest.TestCase):
    def test_primary_loads_once_across_retry_passes(self):
        constructor = Mock()
        with patch.dict(sys.modules, {'faster_whisper': SimpleNamespace(WhisperModel=constructor)}):
            models = VerificationModels()
            first = models.primary('small.en', {'device': 'cuda', 'compute_type': 'float16'})
            second = models.primary('small.en', {'compute_type': 'float16', 'device': 'cuda'})
            self.assertIs(first, second)
            constructor.assert_called_once()
            models.close()
            self.assertFalse(models.primaries)

    def test_secondary_loads_once_when_rejected_clips_need_it(self):
        constructor = Mock()
        constructor.return_value.transcribe.return_value = ([SimpleNamespace(text='Hello.')], {})
        with patch.dict(sys.modules, {'faster_whisper': SimpleNamespace(WhisperModel=constructor)}):
            models = VerificationModels()
            first = models.secondary('medium.en')
            constructor.assert_not_called()
            self.assertEqual(first.transcribe('clip.wav'), 'Hello.')
            second = models.secondary('medium.en')
            self.assertIs(first, second)
            self.assertEqual(second.transcribe('retry.wav'), 'Hello.')
            constructor.assert_called_once_with('medium.en', device='cpu', compute_type='int8', cpu_threads=4)
            self.assertIsNone(models.secondary('none'))
            models.close()
            self.assertFalse(models.secondaries)

    def test_different_verification_settings_do_not_share_primary(self):
        constructor = Mock(side_effect=lambda *args, **kwargs: object())
        with patch.dict(sys.modules, {'faster_whisper': SimpleNamespace(WhisperModel=constructor)}):
            models = VerificationModels()
            first = models.primary('small.en', {'device': 'cuda'})
            second = models.primary('small.en', {'device': 'cpu'})
            self.assertIsNot(first, second)
            self.assertEqual(2, constructor.call_count)

    def test_runner_keeps_review_exit_code_and_reuses_cache(self):
        verify = Mock(side_effect=[2, 0])
        with patch.dict(sys.modules, {'verify_chapter_probe': SimpleNamespace(main=verify)}):
            runner = PersistentVerificationRunner()
            args = ['python', 'verify_chapter_probe.py', 'chapter.json', '--model', 'small.en']
            self.assertEqual(2, runner.run('attempt 1', args, allowed_exit_codes=(0, 2)))
            self.assertEqual(0, runner.run('attempt 2', args, allowed_exit_codes=(0, 2)))
            self.assertIs(verify.call_args_list[0].kwargs['model_cache'], verify.call_args_list[1].kwargs['model_cache'])
            self.assertEqual(args[2:], verify.call_args.args[0])
            runner.close()

    def test_runner_does_not_hide_verification_errors(self):
        with patch.dict(sys.modules, {'verify_chapter_probe': SimpleNamespace(main=Mock(return_value=1))}):
            runner = PersistentVerificationRunner()
            with self.assertRaisesRegex(RuntimeError, 'exit code 1'):
                runner.run('attempt', ['python', 'verify.py', 'chapter.json'], allowed_exit_codes=(0, 2))
            runner.close()
