import sys
import unittest
from pathlib import Path

import torch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from chatterbox_safety import filter_speech_tokens, configure_speech_token_guard


class SpeechTokenSafetyTests(unittest.TestCase):
    def test_decoder_receives_only_valid_indices_in_original_order(self):
        tokens = torch.tensor([0, 6560, 6561, -1, 6562, 42, 7000])
        self.assertEqual([0, 6560, 42], filter_speech_tokens(tokens, 6561).tolist())

    def test_empty_speech_fails_before_decoder(self):
        with self.assertRaisesRegex(ValueError, 'no valid speech tokens'):
            filter_speech_tokens(torch.tensor([6561, -1]), 6561)

    def test_multilingual_guard_is_idempotent(self):
        import chatterbox.mtl_tts as multilingual
        configure_speech_token_guard()
        installed = multilingual.drop_invalid_tokens
        configure_speech_token_guard()
        self.assertIs(installed, multilingual.drop_invalid_tokens)
        self.assertEqual([12, 13], installed(torch.tensor([12, 7000, 13])).tolist())


if __name__ == '__main__':
    unittest.main()
