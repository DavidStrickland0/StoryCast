import base64
import hashlib
from io import BytesIO
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chapter_art as art
import export_chapter


def jpeg():
    from PIL import Image
    stream = BytesIO()
    Image.new("RGB", (256, 256), "navy").save(stream, "JPEG")
    return stream.getvalue()


class ChapterArtTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.book = Path(self.temporary.name)
        art.save(self.book / "book.json", dict(id="test", title="Book", author="Author", chapters=["one.md", "two.md"]))
        (self.book / "one.md").write_text("# One\nAdam stands beside the river.")
        (self.book / "two.md").write_text("# Two\nAdam walks into the forest.")
        art.save(self.book / "production/characters.json", dict(characters=[dict(id="adam", displayName="Adam", isNamed=True)]))
        art.save(self.book / "production/illustrations.json", dict(enabled=True, size=256))

    def test_reference_design_is_frozen_across_chapters_and_renders_are_cached(self):
        requests = []

        def request(url, payload, **kwargs):
            if url.endswith("/api/generate"):
                source = json.loads(payload["prompt"])["source"]
                return dict(response=json.dumps(dict(scene="Adam in the natural setting.", evidence=source.splitlines()[-1],
                    characters=[dict(id="adam", appearance="Young man with short black hair" if "river" in source else "Old man with white hair")])) )
            requests.append(payload)
            return dict(image=base64.b64encode(jpeg()).decode())

        with patch.object(art, "request_json", side_effect=request):
            first = art.ensure_art(self.book, "chapter-001")
            second = art.ensure_art(self.book, "chapter-002")
            self.assertTrue(first.is_file() and second.is_file())
            self.assertEqual(4, len(requests))  # Shared style, one character reference, two scenes.
            self.assertIn("Young man with short black hair", requests[-1]["prompt"])
            self.assertNotIn("Old man with white hair", requests[-1]["prompt"])
            self.assertEqual(requests[2]["images"], requests[3]["images"])
            art.ensure_art(self.book, "chapter-001")
            self.assertEqual(4, len(requests))
            first.write_bytes(b"corrupt image")
            art.ensure_art(self.book, "chapter-001")
            self.assertEqual(5, len(requests))

    def test_rejects_invented_evidence_and_foreign_characters(self):
        for evidence, character in [("This never happened.", "adam"), ("Adam stands beside the river.", "unknown")]:
            response = dict(response=json.dumps(dict(scene="A scene beside the river.", evidence=evidence,
                          characters=[dict(id=character, appearance="A young traveler")])) )
            with patch.object(art, "request_json", return_value=response):
                with self.assertRaises(ValueError):
                    art.ensure_art(self.book, "chapter-001")

    def test_disabled_books_do_not_call_models(self):
        art.save(self.book / "production/illustrations.json", dict(enabled=False))
        with patch.object(art, "request_json") as request:
            self.assertIsNone(art.ensure_art(self.book, "chapter-001"))
            request.assert_not_called()

    def test_invalid_render_is_never_promoted(self):
        target = self.book / "image.jpg"
        with patch.object(art, "request_json", return_value=dict(image=base64.b64encode(b"not an image").decode())):
            with self.assertRaises(Exception):
                art.render({}, "A long enough prompt", [], target, 1)
        self.assertFalse(target.exists())

    @unittest.skipUnless(shutil.which("ffmpeg") and shutil.which("ffprobe"), "FFmpeg required")
    def test_export_embeds_art_and_backfill_preserves_compressed_speech(self):
        audio = self.book / "chapter.wav"
        image = self.book / "image.jpg"
        image.write_bytes(jpeg())
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", str(audio)], check=True)
        arguments = ["export_chapter.py", str(self.book), "chapter-001", str(audio)]
        with patch.object(sys, "argv", arguments), patch.object(art, "ensure_art", return_value=image):
            self.assertEqual(0, export_chapter.main())
        mp3 = self.book / "output/final/chapter-001.mp3"

        def speech_bytes():
            return subprocess.run(["ffmpeg", "-v", "error", "-i", str(mp3), "-map", "0:a:0", "-c:a", "copy", "-f", "data", "-"], capture_output=True, check=True).stdout

        before = speech_bytes()
        run = self.book / "output/run"
        export_path = run / "chapter-001/chapter-export.json"
        art.save(export_path, dict(bookId="test", chapterId="chapter-001"))
        original_sha = hashlib.sha256(mp3.read_bytes()).hexdigest()
        art.save(run / "run.json", dict(status="completed", chapterId="chapter-001", stageCheckpoints=dict(mp3Export=dict(outputs=[
            dict(path="/books/output/final/chapter-001.mp3", sha256=original_sha),
            dict(path="/books/output/run/chapter-001/chapter-export.json", sha256="old")]))))
        old_sha, new_sha = art.embed_existing(mp3, image)
        art.update_export_receipts(self.book, "chapter-001", image, old_sha, new_sha)
        self.assertEqual(before, speech_bytes())
        receipt = art.load(run / "run.json")
        self.assertEqual(new_sha, receipt["stageCheckpoints"]["mp3Export"]["outputs"][0]["sha256"])
        self.assertEqual(hashlib.sha256(export_path.read_bytes()).hexdigest(), receipt["stageCheckpoints"]["mp3Export"]["outputs"][1]["sha256"])
        self.assertTrue(receipt["artworkUpdates"][0]["compressedAudioUnchanged"])
        probe = json.loads(subprocess.run(["ffprobe", "-v", "error", "-show_streams", "-show_format", "-of", "json", str(mp3)], capture_output=True, text=True, check=True).stdout)
        self.assertEqual("1/2", probe["format"]["tags"]["track"])
        self.assertTrue(any(stream["disposition"].get("attached_pic") == 1 for stream in probe["streams"]))
        # An unavailable renderer cannot prevent narration export.
        with patch.object(sys, "argv", arguments), patch.object(art, "ensure_art", side_effect=RuntimeError("renderer offline")):
            self.assertEqual(0, export_chapter.main())
        self.assertTrue(mp3.is_file())
        self.assertEqual("pending", art.load(self.book / "illustrations/pending/chapter-001.json")["status"])


if __name__ == "__main__":
    unittest.main()
