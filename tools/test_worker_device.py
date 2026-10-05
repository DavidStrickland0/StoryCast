import os
import unittest
from unittest.mock import patch

from worker_device import whisper_options


class WorkerDeviceTests(unittest.TestCase):
    def test_cpu_profile_uses_supported_integer_precision(self):
        with patch.dict(os.environ, {"STORYCAST_DEVICE": "cpu"}):
            self.assertEqual(whisper_options(), {"device": "cpu", "compute_type": "int8"})

    def test_cpu_profile_is_case_insensitive(self):
        with patch.dict(os.environ, {"STORYCAST_DEVICE": "CPU"}):
            self.assertEqual(whisper_options()["device"], "cpu")

    def test_existing_cuda_profile_is_preserved(self):
        with patch.dict(os.environ, {}, clear=True):
            self.assertEqual(whisper_options(), {"device": "cuda", "compute_type": "float16"})


if __name__ == "__main__":
    unittest.main()
