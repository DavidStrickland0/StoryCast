"""Exercise the synthesis entry point with a deterministic CPU test model."""
import contextlib
import io
import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import torch
import torchaudio
import synthesize_chapter_probe as synthesis
import verify_chapter_probe as verification
from shared_audio_cache import digest_file


class TestModel:
    sr = 24000

    def __init__(self):
        self.generations = 0
        self.conditionals = 0

    def prepare_conditionals(self, *args, **kwargs):
        self.conditionals += 1

    def generate(self, *args, **kwargs):
        self.generations += 1
        return .1 * torch.sin(torch.arange(4800).float().unsqueeze(0) * .1)


class SharedCachePipelineTests(unittest.TestCase):
    def test_second_book_reuses_verified_chunk_without_running_tts(self):
        with tempfile.TemporaryDirectory() as temporary, contextlib.redirect_stdout(io.StringIO()):
            root = Path(temporary)
            sample = root / 'voice.wav'
            torchaudio.save(str(sample), torch.ones(1, 2400) * .1, 24000)
            model = TestModel()
            settings = {'exaggeration': .4, 'cfgWeight': .5, 'temperature': .7, 'referenceSha256': digest_file(sample)}
            with patch.dict(os.environ, {'STORYCAST_CHUNK_CACHE_DIR': str(root / 'shared')}), \
                 patch.object(synthesis, '_CACHED_MODEL', model), \
                 patch.object(synthesis, '_CACHED_MODEL_FINGERPRINT', 'test-model-file-hash'), \
                 patch.object(synthesis, 'move_model'), \
                 patch.object(synthesis, 'runtime_identity', return_value={'testRuntime': 1}), \
                 patch.object(synthesis, 'load_voice_samples', return_value={'voice-one': sample}), \
                 patch.object(synthesis, 'resolve_synthesis_settings', side_effect=lambda *args: dict(settings)), \
                 patch.object(synthesis, 'reference_settings', side_effect=lambda *args: (sample, dict(settings))), \
                 patch.object(synthesis.torch.cuda, 'is_available', return_value=False):
                def run(name, index):
                    book = root / name
                    scripts = book / 'production/scripts'
                    scripts.mkdir(parents=True)
                    (book / 'book.json').write_text(json.dumps({'id': name, 'chapters': ['chapter.txt']}))
                    (book / 'chapter.txt').write_text('The lamp is on.')
                    (book / 'production/casting.json').write_text(json.dumps({'assignments': [{'characterId': 'narrator', 'voiceId': 'voice-one'}]}))
                    (scripts / 'chapter-001.json').write_text(json.dumps({'sourceSha256': 'source-hash', 'preparationVersion': '3',
                        'script': {'segments': [{'index': index, 'kind': 'narration', 'speakerId': 'narrator', 'sourceText': 'The lamp is on.'}]}}))
                    self.assertEqual(0, synthesis.main([str(book), str(root), '--chapter', 'chapter-001', '--run-directory', str(book / 'run')]))
                    return json.loads((book / 'run/chapter-001/chapter.json').read_text())['segments'][0]['chunks'][0]
                first = run('book-one', 0)
                self.assertEqual(1, model.generations)
                self.assertFalse(first['sharedCacheHit'])
                # An unverified generation in another book cannot be reused.
                unverified = run('book-unverified', 5)
                self.assertEqual(2, model.generations)
                class TestVerifiers:
                    def primary(self, *args): return object()
                    def secondary(self, *args): return None
                result = {'status': 'pass', 'wordErrorRate': 0, 'expectedWordCount': 4,
                          'transcribedWordCount': 4, 'editDistance': 0, 'verificationMode': 'transcript',
                          'transcription': 'The lamp is on.'}
                first_manifest = root / 'book-one/run/chapter-001/chapter.json'
                with patch.object(verification, 'verify_audio', return_value=result):
                    self.assertEqual(0, verification.main([str(first_manifest), '--secondary-model', 'none'], model_cache=TestVerifiers()))
                report = json.loads(first_manifest.with_name('verification.json').read_text())
                self.assertTrue(report['chunks'][0]['sharedCachePublished'])
                second = run('book-two', 9)
                self.assertEqual(2, model.generations)
                self.assertEqual(2, model.conditionals)
                self.assertTrue(second['sharedCacheHit'])
                self.assertEqual(first['seed'], second['seed'])
                self.assertEqual(first['sha256'], second['sha256'])
                settings['temperature'] = .8
                third = run('book-new-settings', 15)
                self.assertEqual(3, model.generations)
                self.assertFalse(third['sharedCacheHit'])
