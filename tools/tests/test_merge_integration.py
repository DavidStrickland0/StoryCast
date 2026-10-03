import json
import sys
import tempfile
import types
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import verify_chapter_probe as verification


class MergeVerificationTests(unittest.TestCase):
    def test_selective_verification_preserves_chunks_and_flags_rewrites(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manifest = root / "chapter.json"
            manifest.write_text(json.dumps({
                "chapterId": "chapter-001",
                "segments": [{"index": 1, "speakerId": "speaker", "voiceId": "voice",
                              "sourceText": "Hello", "spokenText": "Hello there",
                              "audioPath": str(root / "new.wav"),
                              "chunks": [{"index": 0, "sourceText": "Hello there",
                                          "audioPath": str(root / "chunk.wav")}]}],
            }))
            old_chunk = {"segmentIndex": 0, "chunkIndex": 0, "status": "pass"}
            (root / "verification.json").write_text(json.dumps({
                "segments": [{"segmentIndex": 0, "status": "pass", "chunks": [old_chunk]}]
            }))
            result = {"status": "pass", "expectedWordCount": 2,
                      "transcribedWordCount": 2, "editDistance": 0,
                      "wordErrorRate": 0, "verificationMode": "transcript",
                      "expectedText": "Hello there", "transcription": "Hello there",
                      "audioPath": str(root / "chunk.wav")}
            fake_whisper = types.SimpleNamespace(WhisperModel=lambda *a, **k: object())
            fake_voice = types.SimpleNamespace(edit_distance=lambda a,b: 0, normalize_words=str.split)
            with patch.dict(sys.modules, {"faster_whisper": fake_whisper, "verify_voice_library": fake_voice}), \
                 patch.object(sys, "argv", ["verify", str(manifest), "--segment-index", "1"]), \
                 patch.object(verification, "find_book_root", return_value=root), \
                 patch.object(verification, "load_pronunciations", return_value=[]), \
                 patch.object(verification, "verify_audio", return_value=result) as verify:
                self.assertEqual(2, verification.main())
            verify.assert_called_once()
            report = json.loads((root / "verification.json").read_text())
            self.assertEqual(old_chunk, report["chunks"][0])
            self.assertEqual("review", report["chunks"][1]["status"])
            self.assertTrue(report["segments"][1]["wasRewritten"])
            self.assertEqual({"total": 2, "passed": 1, "review": 1, "failed": 0}, report["summary"])


if __name__ == "__main__":
    unittest.main()
