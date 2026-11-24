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
        if not torch.is_inference_mode_enabled():
            raise AssertionError('Voice conditioning must not retain a training graph.')
        self.conditionals += 1

    def generate(self, *args, **kwargs):
        self.generations += 1
        return .1 * torch.sin(torch.arange(4800).float().unsqueeze(0) * .1)


class SharedCachePipelineTests(unittest.TestCase):
    def test_cpu_speech_model_is_unloaded_before_verification(self):
        with patch.object(synthesis, '_CACHED_MODEL', object()), \
             patch.object(synthesis, '_CACHED_MODEL_FINGERPRINT', 'model'), \
             patch.object(synthesis.torch.cuda, 'is_available', return_value=False), \
             patch.object(synthesis, 'release_cpu_memory') as release:
            synthesis.park_cached_model()
            self.assertIsNone(synthesis._CACHED_MODEL)
            self.assertIsNone(synthesis._CACHED_MODEL_FINGERPRINT)
            release.assert_called_once()

    def test_cpu_decode_preserves_all_tokens_with_bounded_overlapping_windows(self):
        from chatterbox.mtl_tts import S3_TOKEN_RATE
        calls = []
        def decode(tokens, **kwargs):
            calls.append(tokens.shape[-1])
            return tokens.float().repeat_interleave(2, dim=-1), None
        decoder = type('Decoder', (), {})()
        decoder.inference = decode
        model = type('Model', (), {'s3gen': decoder, 'sr': S3_TOKEN_RATE * 2})()
        tokens = torch.arange(686).unsqueeze(0)
        with patch.object(synthesis.torch.cuda, 'is_available', return_value=False):
            with synthesis.bounded_cpu_decode(model):
                audio, _ = decoder.inference(speech_tokens=tokens, ref_dict={})
                short, _ = decoder.inference(speech_tokens=tokens[..., :8], ref_dict={})
                self.assertTrue(torch.equal(tokens[..., :8].float().repeat_interleave(2, dim=-1), short))
        self.assertTrue(torch.equal(tokens.float().repeat_interleave(2, dim=-1), audio))
        self.assertLessEqual(max(calls), 256)
        self.assertIs(decoder.inference, decode)

    def test_cpu_quantization_changes_transformer_only_and_can_run_inference(self):
        transformer = torch.nn.Sequential(torch.nn.Linear(8, 8), torch.nn.ReLU())
        decoder = torch.nn.Linear(8, 8)
        model = type('Model', (), {'t3': type('T3', (), {'tfmr': transformer})(), 's3gen': decoder})()
        synthesis.reduce_cpu_model_memory(model)
        self.assertIsInstance(model.t3.tfmr[0], torch.ao.nn.quantized.dynamic.Linear)
        self.assertIs(model.s3gen, decoder)
        self.assertEqual((1, 8), model.t3.tfmr(torch.ones(1, 8)).shape)

    def test_cpu_allocator_cleanup_runs_during_generation_and_hook_is_removed(self):
        transformer = torch.nn.Identity()
        model = type('Model', (), {'t3': type('T3', (), {'tfmr': transformer})()})()
        with patch.object(synthesis.torch.cuda, 'is_available', return_value=False), \
             patch.object(synthesis, 'release_cpu_memory') as cleanup:
            with self.assertRaisesRegex(RuntimeError, 'generation failed'):
                with synthesis.cpu_generation_memory(model):
                    for _ in range(32):
                        transformer(torch.ones(1))
                    self.assertEqual(2, cleanup.call_count)
                    raise RuntimeError('generation failed')
            self.assertFalse(transformer._forward_hooks)

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
