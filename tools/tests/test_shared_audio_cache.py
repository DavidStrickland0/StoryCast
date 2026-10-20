import concurrent.futures
import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from shared_audio_cache import SharedAudioCache, chunk_identity, digest_file, identity


class SharedCacheTests(unittest.TestCase):
    def inputs(self, **changes):
        options = dict(text='The lamp is on.', synthesis_text='The lamp is on.', voice_id='voice-one',
                       reference_hash='reference-hash', settings={'temperature': .7, 'cfgWeight': .5, 'exaggeration': .4},
                       model_hash='model-file-hash', sample_rate=24000, runtime={'version': 'one'},
                       delivery='calm', performance={'pace': 'normal'}, context=[], pause=.12, attempt=0)
        options.update(changes)
        return chunk_identity(**options)

    def candidate(self, root, content=b'test audio'):
        audio = root / 'candidate.wav'
        audio.write_bytes(content)
        inputs = self.inputs()
        chunk = dict(sharedCacheInputs=inputs, sourceText=inputs['text'], synthesisText=inputs['synthesisText'],
                     seed=inputs['seed'], sampleRate=inputs['sampleRate'], audioPath=str(audio), sha256=digest_file(audio))
        result = dict(status='pass', audioSha256=chunk['sha256'], verificationPolicy='verified-policy')
        return inputs, chunk, result

    def test_every_input_invalidates_key_and_retry_changes_seed(self):
        original = self.inputs()
        changes = dict(text='The lamp is off.', synthesis_text='The lamp was on.', voice_id='voice-two',
                       reference_hash='new-sample', settings={'temperature': .8}, model_hash='updated-model',
                       sample_rate=48000, runtime={'version': 'two'}, delivery='angry', performance={'pace': 'fast'},
                       context=[{'pronunciation': 'changed'}], pause=.2, attempt=1)
        for field, value in changes.items():
            with self.subTest(field=field):
                self.assertNotEqual(identity(original), identity(self.inputs(**{field: value})))
        self.assertEqual(original, self.inputs())
        self.assertNotEqual(original['seed'], self.inputs(attempt=1)['seed'])

    def test_only_verified_passes_are_reused_across_destinations(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            inputs, chunk, result = self.candidate(root)
            cache = SharedAudioCache(root / 'shared')
            for status in ('review', 'fail'):
                self.assertFalse(cache.publish(chunk, dict(result, status=status)))
            self.assertFalse(cache.publish(chunk, dict(result, audioSha256='0' * 64)))
            self.assertIsNone(cache.restore(inputs, root / 'book-one/chunk.wav'))
            self.assertTrue(cache.publish(chunk, result))
            for name in ('book-one', 'book-two'):
                destination = root / name / 'chunk.wav'
                self.assertIsNotNone(cache.restore(inputs, destination))
                self.assertEqual(Path(chunk['audioPath']).read_bytes(), destination.read_bytes())
            self.assertIsNone(cache.restore(self.inputs(voice_id='other'), root / 'wrong.wav'))

    def test_corrupt_and_partial_entries_are_misses(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            inputs, chunk, result = self.candidate(root)
            cache = SharedAudioCache(root / 'shared')
            cache.publish(chunk, result)
            folder = cache.directory(inputs)
            (folder / f"{chunk['sha256']}.wav").write_bytes(b'corrupt')
            self.assertIsNone(cache.restore(inputs, root / 'target.wav'))
            self.assertFalse((root / 'target.wav').exists())
            (folder / 'verified.json').write_text('{partial')
            self.assertIsNone(cache.restore(inputs, root / 'target.wav'))
            self.assertTrue(cache.publish(chunk, result))
            self.assertIsNotNone(cache.restore(inputs, root / 'target.wav'))
            Path(chunk['audioPath']).write_bytes(b'modified after verification')
            self.assertFalse(cache.publish(chunk, result))

    def test_disabled_cache_does_not_read_or_write(self):
        with tempfile.TemporaryDirectory() as temporary, patch.dict(os.environ, {'STORYCAST_CHUNK_CACHE': '0'}):
            root = Path(temporary)
            inputs, chunk, result = self.candidate(root)
            cache = SharedAudioCache(root / 'shared')
            self.assertFalse(cache.publish(chunk, result))
            self.assertIsNone(cache.restore(inputs, root / 'target.wav'))
            self.assertFalse((root / 'shared').exists())

    def test_concurrent_verified_writers_never_mix_receipts_and_audio(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            cache = SharedAudioCache(root / 'shared')
            inputs, chunk, result = self.candidate(root)
            def publish(number):
                source = root / f'source-{number}.wav'
                source.write_bytes(f'verified-audio-{number}'.encode())
                candidate = dict(chunk, audioPath=str(source), sha256=digest_file(source))
                verified = dict(result, audioSha256=candidate['sha256'])
                return cache.publish(candidate, verified)
            with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
                self.assertTrue(any(list(pool.map(publish, range(24)))))
            receipt = cache.restore(inputs, root / 'target.wav')
            self.assertIsNotNone(receipt)
            self.assertEqual(receipt['audioSha256'], digest_file(root / 'target.wav'))
            self.assertFalse(list(cache.directory(inputs).glob('*.tmp')))


if __name__ == '__main__':
    unittest.main()
