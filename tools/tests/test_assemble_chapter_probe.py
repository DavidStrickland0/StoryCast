import contextlib
import io
import json
import sys
import tempfile
import unittest
import wave
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import assemble_chapter_probe as assembler
import produce_chapter
from verify_assembly_probe import verify_assembly_integrity


class ReviewAssemblyTests(unittest.TestCase):
    def test_review_policy_reaches_real_assembly_and_pcm_verification(self):
        for status in ("review", "fail"):
            with self.subTest(status=status), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                source = root / "segment.wav"
                with wave.open(str(source), "wb") as audio:
                    audio.setparams((1, 2, 24000, 0, "NONE", "not compressed"))
                    audio.writeframes(b"\x10\x00" * 2400)
                manifest = {"chapterId": "chapter-001", "sourceSha256": "source", "segments": [
                    {"index": 0, "speakerId": "narrator", "voiceId": "voice", "sourceText": "Hello",
                     "audioPath": str(source)}]}
                verification = {"segments": [{"segmentIndex": 0, "status": status, "expectedText": "Hello"}]}
                path = root / "chapter.json"
                path.write_text(json.dumps(manifest))
                (root / "verification.json").write_text(json.dumps(verification))
                with patch.object(sys, "argv", ["assembler", str(path)]), self.assertRaisesRegex(RuntimeError, "verification status"):
                    assembler.main()
                with contextlib.redirect_stdout(io.StringIO()):
                    self.assertEqual(0, produce_chapter.assemble_verified_audio(Path(assembler.__file__).parent, path, .18, True))
                assembly = json.loads((root / "assembly.json").read_text())
                self.assertTrue(assembly["reviewRequired"])
                self.assertEqual(status, json.loads((root / "verification.json").read_text())["segments"][0]["status"])
                self.assertEqual(1, verify_assembly_integrity(assembly, manifest, verification, root / "chapter-001.wav", True)["verifiedSegmentCount"])
                verification["segments"].clear()
                (root / "verification.json").write_text(json.dumps(verification))
                with patch.object(sys, "argv", ["assembler", str(path), "--allow-review"]), self.assertRaisesRegex(RuntimeError, "no verification"):
                    assembler.main()

    def test_older_producer_uses_its_persisted_review_contract(self):
        with tempfile.TemporaryDirectory() as temporary:
            run = Path(temporary)
            chapter = run / "chapter-001"
            chapter.mkdir()
            audio = chapter / "segment.wav"
            with wave.open(str(audio), "wb") as output:
                output.setparams((1, 2, 24000, 0, "NONE", "not compressed"))
                output.writeframes(b"\x10\x00" * 2400)
            manifest = {"chapterId": "chapter-001", "sourceSha256": "source", "segments": [
                {"index": 0, "speakerId": "narrator", "voiceId": "voice", "sourceText": "Hello", "audioPath": str(audio)}]}
            path = chapter / "chapter.json"
            path.write_text(json.dumps(manifest))
            (chapter / "verification.json").write_text(json.dumps({"segments": [{"segmentIndex": 0, "status": "review"}]}))
            for flag, chapter_id, succeeds in ((True, "chapter-001", True), (False, "chapter-001", False), (True, "chapter-002", False)):
                (run / "run.json").write_text(json.dumps({"chapterId": chapter_id, "stageCheckpoints": {
                    "segmentVerification": {"settings": {"allowReview": flag}}}}))
                with patch.object(sys, "argv", ["assembler", str(path)]), contextlib.redirect_stdout(io.StringIO()):
                    if succeeds:
                        self.assertEqual(0, assembler.main())
                    else:
                        with self.assertRaisesRegex(RuntimeError, "verification status"):
                            assembler.main()


if __name__ == "__main__":
    unittest.main()
