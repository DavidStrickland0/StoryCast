"""Shared, content-addressed storage for independently verified speech chunks."""
from __future__ import annotations

import hashlib
import importlib.metadata
import json
import os
import shutil
import sys
import uuid
from pathlib import Path

SCHEMA = 1


def digest_file(path):
    digest = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def identity(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, ensure_ascii=False,
                                     separators=(',', ':'), allow_nan=False).encode()).hexdigest()


def chunk_identity(text, synthesis_text, voice_id, reference_hash, settings, model_hash,
                   sample_rate, runtime, delivery='', performance=None, context=None,
                   pause=0, attempt=0):
    inputs = dict(schemaVersion=SCHEMA, text=text, synthesisText=synthesis_text,
                  voiceId=voice_id, referenceSha256=reference_hash, settings=settings,
                  modelSha256=model_hash, sampleRate=sample_rate, language='en', runtime=runtime,
                  delivery=delivery, performance=performance, context=context, pause=pause)
    # Identical input/settings need an identical actual seed across books and
    # positions. A retry deliberately changes the seed and thus the cache key.
    inputs['seed'] = (int(identity(inputs)[:8], 16) + attempt * 100000) % (2 ** 31)
    return inputs


class SharedAudioCache:
    def __init__(self, root=None):
        self.root = Path(root or os.environ.get('STORYCAST_CHUNK_CACHE_DIR', '/models/chunk-audio-cache'))
        self.enabled = os.environ.get('STORYCAST_CHUNK_CACHE', '1') != '0'

    def directory(self, inputs):
        key = identity(inputs)
        return self.root / key[:2] / key

    def restore(self, inputs, destination):
        if not self.enabled:
            return None
        temporary = None
        try:
            folder = self.directory(inputs)
            receipt = json.loads((folder / 'verified.json').read_text(encoding='utf-8'))
            checksum = receipt['audioSha256']
            if (receipt.get('schemaVersion') != SCHEMA or receipt.get('inputs') != inputs
                    or receipt.get('verification', {}).get('status') != 'pass'
                    or not receipt.get('verification', {}).get('verificationPolicy')
                    or receipt.get('verification', {}).get('audioSha256') != checksum
                    or not isinstance(checksum, str) or len(checksum) != 64
                    or any(c not in '0123456789abcdef' for c in checksum)):
                return None
            source = folder / f'{checksum}.wav'
            destination = Path(destination)
            destination.parent.mkdir(parents=True, exist_ok=True)
            temporary = destination.with_name(f'.{destination.name}.{uuid.uuid4().hex}.cache.tmp')
            shutil.copyfile(source, temporary)
            if digest_file(temporary) != checksum:
                return None
            temporary.replace(destination)
            return receipt
        except (OSError, ValueError, KeyError, TypeError):
            # A cache failure must not prevent normal synthesis.
            return None
        finally:
            if temporary is not None:
                try:
                    temporary.unlink(missing_ok=True)
                except OSError:
                    pass

    def publish(self, chunk, result):
        inputs = chunk.get('sharedCacheInputs')
        if not self.enabled or not inputs or result.get('status') != 'pass':
            return False
        audio = Path(chunk['audioPath'])
        checksum = chunk.get('sha256')
        if (not isinstance(checksum, str) or len(checksum) != 64
                or any(c not in '0123456789abcdef' for c in checksum)
                or result.get('audioSha256') != checksum
                or not result.get('verificationPolicy')
                or chunk.get('sourceText') != inputs.get('text')
                or chunk.get('synthesisText') != inputs.get('synthesisText')
                or chunk.get('seed') != inputs.get('seed')
                or chunk.get('sampleRate') != inputs.get('sampleRate')):
            return False
        temporary = None
        try:
            folder = self.directory(inputs)
            folder.mkdir(parents=True, exist_ok=True)
            temporary = folder / f'.{uuid.uuid4().hex}.tmp'
            shutil.copyfile(audio, temporary)
            if digest_file(temporary) != checksum:
                return False
            # Immutable audio objects and an atomic receipt prevent readers
            # seeing partially written or mismatched concurrent publications.
            temporary.replace(folder / f'{checksum}.wav')
            receipt = dict(schemaVersion=SCHEMA, inputs=inputs, audioSha256=checksum,
                           audioProcessing=chunk.get('audioProcessing', {}), verification=result)
            temporary = folder / f'.{uuid.uuid4().hex}.json.tmp'
            temporary.write_text(json.dumps(receipt, ensure_ascii=False, allow_nan=False), encoding='utf-8')
            temporary.replace(folder / 'verified.json')
            return True
        except (OSError, ValueError, TypeError):
            return False
        finally:
            if temporary is not None:
                try:
                    temporary.unlink(missing_ok=True)
                except OSError:
                    pass


def load_model_with_identity(device):
    """Fingerprint the exact snapshot loaded, rather than a model name/tag."""
    import chatterbox.mtl_tts as mtl
    model_file = mtl._resolve_multilingual_t3_model('v3')
    files = ['ve.pt', model_file, 's3gen.pt', 'grapheme_mtl_merged_expanded_v1.json', 'conds.pt', 'Cangjie5_TC.json']
    snapshot = Path(mtl.snapshot_download(repo_id=mtl.REPO_ID, repo_type='model', revision='main',
                    allow_patterns=files, token=os.getenv('HF_TOKEN')))
    fingerprint = identity({name: digest_file(snapshot / name) for name in files})
    model = mtl.ChatterboxMultilingualTTS.from_local(snapshot, device, t3_model=model_file)
    return model, fingerprint


def runtime_identity(device, torch):
    import chatterbox
    package = Path(chatterbox.__file__).parent
    code = {str(path.relative_to(package)): digest_file(path) for path in sorted(package.rglob('*.py'))}
    tools = Path(__file__).parent
    for filename in ('synthesize_chapter_probe.py', 'shared_audio_cache.py', 'audio_postprocessing.py',
                     'prosody.py', 'delivery_planning.py', 'pronunciations.py', 'spoken_text.py',
                     'context_pronunciations.py', 'heading_pauses.py', 'synthesis_chunking.py', 'chat_label_recovery.py',
                     'chatterbox_safety.py'):
        code['tools/' + filename] = digest_file(tools / filename)
    versions = {name: importlib.metadata.version(name) for name in ('torch', 'torchaudio', 'numpy', 'chatterbox-tts')}
    return dict(codeSha256=identity(code), packages=versions, device=str(device),
                python=sys.version, cpuThreads=torch.get_num_threads(), interopThreads=torch.get_num_interop_threads(),
                gpu=torch.cuda.get_device_name() if str(device).startswith('cuda') else None,
                cuda=torch.version.cuda, cudnn=torch.backends.cudnn.version(),
                matmulPrecision=torch.get_float32_matmul_precision(),
                defaultDtype=str(torch.get_default_dtype()), autocast=torch.is_autocast_enabled(),
                deterministic=torch.are_deterministic_algorithms_enabled(),
                cudnnBenchmark=torch.backends.cudnn.benchmark,
                cudnnDeterministic=torch.backends.cudnn.deterministic,
                tf32=torch.backends.cuda.matmul.allow_tf32,
                cudnnTf32=torch.backends.cudnn.allow_tf32)
