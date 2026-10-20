import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import chatterbox.mtl_tts as mtl
import torch
from shared_audio_cache import load_model_with_identity, runtime_identity


class SharedCacheModelIdentityTests(unittest.TestCase):
    def test_actual_asset_bytes_change_model_identity(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            name = mtl._resolve_multilingual_t3_model('v3')
            for filename in ['ve.pt', name, 's3gen.pt', 'grapheme_mtl_merged_expanded_v1.json', 'conds.pt', 'Cangjie5_TC.json']:
                (root / filename).write_bytes(filename.encode())
            marker = object()
            with patch.object(mtl, 'snapshot_download', return_value=str(root)), \
                 patch.object(mtl.ChatterboxMultilingualTTS, 'from_local', return_value=marker) as load:
                model, original = load_model_with_identity('cpu')
                self.assertIs(marker, model)
                load.assert_called_with(root, 'cpu', t3_model=name)
                (root / name).write_bytes(b'replacement checkpoint')
                _, changed = load_model_with_identity('cpu')
                self.assertNotEqual(original, changed)

    def test_installed_runtime_identity_is_stable(self):
        first = runtime_identity('cpu', torch)
        self.assertEqual(first, runtime_identity('cpu', torch))
        self.assertEqual('cpu', first['device'])
        self.assertEqual(64, len(first['codeSha256']))
        self.assertIn('chatterbox-tts', first['packages'])
