import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import cache_tokenizer


class TokenizerCacheTests(unittest.TestCase):
    def test_updates_imported_configuration_to_persistent_cache(self):
        with tempfile.TemporaryDirectory() as temporary, patch.dict(os.environ, {"PKUSEG_HOME": str(Path(temporary) / "pkuseg")}), \
             patch.object(cache_tokenizer.spacy_pkuseg.config, "pkuseg_home", "/root/.pkuseg"), \
             patch.object(cache_tokenizer.download, "urlopen"):
            path = cache_tokenizer.configure_tokenizer_cache()
            self.assertTrue(path.is_dir())
            self.assertEqual(str(path), cache_tokenizer.spacy_pkuseg.config.pkuseg_home)
            self.assertIs(cache_tokenizer.download_with_long_timeout, cache_tokenizer.download.urlopen)

    def test_download_uses_long_timeout_and_preserves_other_request_options(self):
        with patch.object(cache_tokenizer.requests, "get") as get:
            cache_tokenizer.download_with_long_timeout("https://example.test/model.zip", stream=True, timeout=5)
            get.assert_called_once_with("https://example.test/model.zip", stream=True, timeout=(30, 300))
