import hashlib
import tempfile
import unittest
from pathlib import Path
from synthesize_chapter_probe import resume_chunk_is_reusable


class ResumeChunkTests(unittest.TestCase):
    def test_only_the_changed_or_interrupted_chunk_needs_new_audio(self):
        with tempfile.TemporaryDirectory() as temporary:
            audio = Path(temporary) / "chunk.wav"
            audio.write_bytes(b"original audio")
            chunk = {"sourceText": "Original message", "pauseAfterSeconds": .12,
                     "sha256": hashlib.sha256(audio.read_bytes()).hexdigest()}
            self.assertTrue(resume_chunk_is_reusable(chunk, "Original message", .12, audio))
            self.assertFalse(resume_chunk_is_reusable(chunk, "Changed pronunciation", .12, audio))
            self.assertFalse(resume_chunk_is_reusable(chunk, "Original message", .25, audio))
            audio.write_bytes(b"interrupted replacement")
            self.assertFalse(resume_chunk_is_reusable(chunk, "Original message", .12, audio))
            audio.unlink()
            self.assertFalse(resume_chunk_is_reusable(chunk, "Original message", .12, audio))


if __name__ == "__main__":
    unittest.main()
