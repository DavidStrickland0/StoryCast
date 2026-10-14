"""Run in the Docker worker: exercises real FFmpeg on synthetic fixture audio."""
import hashlib
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import ambience


class AmbienceMixingTests(unittest.TestCase):
    def fixture(self, root):
        import numpy as np
        import soundfile as sf
        sr = 22050
        narration = root / "chapter-001.mastered.wav"
        time = np.arange(sr * 12) / sr
        sf.write(narration, 0.12 * np.sin(2 * np.pi * 220 * time), sr)
        rain = root / "fixture-rain.wav"
        sf.write(rain, np.random.default_rng(1).normal(0, 0.1, (sr * 6, 2)), sr)
        manifest = root / "chapter.json"
        assembly = root / "assembly.json"
        ambience.save(manifest, dict(chapterId="chapter-001", segments=[]))
        ambience.save(assembly, dict(durationSeconds=12, segments=[]))
        return narration, rain, manifest, assembly

    def test_real_mix_retains_clean_stem_and_exact_duration(self):
        import soundfile as sf
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            narration, rain, manifest, assembly = self.fixture(root)
            original_hash = hashlib.sha256(narration.read_bytes()).hexdigest()
            cue = dict(startSeconds=2, endSeconds=10, audioPath=str(rain), asset={"fixture": True})
            with patch.object(ambience, "plan_ambience", return_value=[]), \
                 patch.object(ambience, "timed_cues", return_value=[cue]), \
                 patch.object(ambience, "provision_cues", return_value=[cue]):
                result = ambience.add_ambience(manifest, assembly, narration, "fixture", "http://fixture", "http://fixture")
            audio, sr = sf.read(result)
            self.assertEqual(2, audio.shape[1])
            self.assertAlmostEqual(12, len(audio) / sr, places=2)
            self.assertEqual(original_hash, hashlib.sha256(narration.read_bytes()).hexdigest())
            self.assertEqual(original_hash, ambience.load(root / "ambience.json")["narrationSha256"])
            self.assertTrue((root / "ambience.json").is_file())

    def test_no_supported_ambience_preserves_clean_audio_exactly(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            narration, _, manifest, assembly = self.fixture(root)
            with patch.object(ambience, "plan_ambience", return_value=[]):
                result = ambience.add_ambience(manifest, assembly, narration, "fixture", "http://fixture", "http://fixture")
            self.assertEqual(narration.read_bytes(), result.read_bytes())


if __name__ == "__main__":
    unittest.main()
