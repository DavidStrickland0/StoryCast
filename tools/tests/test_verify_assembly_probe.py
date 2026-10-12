import sys
import unittest
import tempfile
import wave
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from verify_assembly_probe import assembly_expected_text, comparison_words, edit_distance, verify_assembly_integrity


class AssemblyComparisonTests(unittest.TestCase):
    def test_uses_actual_chunks_in_order_and_preserves_word_boundaries(self):
        manifest = {"segments": [
            {"index": 2, "sourceText": "original", "spokenText": "fallback",
             "chunks": [{"index": 1, "sourceText": "second"}, {"index": 0, "sourceText": "first"}]},
            {"index": 1, "sourceText": "raw", "spokenText": "prepared"},
        ]}
        self.assertEqual("prepared first second", assembly_expected_text(manifest))

    def test_legacy_source_only_manifest(self):
        self.assertEqual("Hello there", assembly_expected_text({"segments": [
            {"index": 0, "sourceText": "Hello"}, {"index": 1, "sourceText": "there"}]}))

    def test_shared_scoped_alias_does_not_accept_other_changes(self):
        entries = [{"canonical": "garbage chute kekeke", "spokenText": "garbage chute kekeke",
                    "acceptedTranscriptions": ["garbage shoot kekekek"]}]
        expected = comparison_words("Try garbage chute kekeke", entries)
        self.assertEqual(expected, comparison_words("Try garbage shoot kekekek", entries))
        self.assertNotEqual(expected, comparison_words("Try garbage shoot someone", entries))

    def test_missing_or_repeated_speech_still_exceeds_failure_threshold(self):
        expected = comparison_words("First sentence second sentence third sentence", [])
        for actual in ("First sentence", "First sentence second sentence third sentence " * 3):
            self.assertGreater(edit_distance(expected, comparison_words(actual, [])) / len(expected), .20)


class AssemblyIntegrityTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.first = b"\x10\x00" * 80
        self.second = b"\x20\x00" * 70
        self.silence = bytes(20)
        self.write("first.wav", self.first)
        self.write("second.wav", self.second)
        self.path = self.root / "chapter.wav"
        self.write("chapter.wav", self.first + self.silence + self.second)
        self.chapter = {"chapterId": "chapter-001", "sourceSha256": "source", "segments": [
            {"index": 0, "sourceText": "First", "audioPath": str(self.root / "first.wav")},
            {"index": 1, "sourceText": "Second", "audioPath": str(self.root / "second.wav")}]}
        self.assembly = {"chapterId": "chapter-001", "sourceSha256": "source", "sampleRate": 1000,
                         "channels": 1, "pauseSeconds": .01, "segments": [{"segmentIndex": 0}, {"segmentIndex": 1}]}
        self.verification = {"segments": [{"segmentIndex": 0, "expectedText": "First", "status": "pass"},
                                          {"segmentIndex": 1, "expectedText": "Second", "status": "pass"}]}

    def write(self, name, data):
        with wave.open(str(self.root / name), "wb") as audio:
            audio.setparams((1, 2, 1000, 0, "NONE", "not compressed"))
            audio.writeframes(data)

    def check(self):
        return verify_assembly_integrity(self.assembly, self.chapter, self.verification, self.path)

    def test_exact_verified_segments_pass_without_transcription(self):
        self.assertEqual(2, self.check()["verifiedSegmentCount"])

    def test_dropped_reordered_repeated_and_altered_audio_fail(self):
        for data in (self.first, self.second + self.silence + self.first,
                     self.first + self.silence + self.second + self.second,
                     self.first + self.first + self.silence + self.second,
                     self.first + self.silence + self.first[:len(self.second)]):
            self.write("chapter.wav", data)
            with self.assertRaises(ValueError):
                self.check()

    def test_missing_or_non_silent_pause_fails(self):
        for pause in (b"", self.silence * 2, b"\x10\x00" * 10):
            self.write("chapter.wav", self.first + pause + self.second)
            with self.assertRaises(ValueError):
                self.check()

    def test_stale_source_or_text_cannot_bypass_verification(self):
        self.assembly["sourceSha256"] = "changed"
        with self.assertRaises(ValueError):
            self.check()
        self.assembly["sourceSha256"] = "source"
        self.verification["segments"][0]["expectedText"] = "Changed"
        with self.assertRaises(ValueError):
            self.check()

    def test_review_or_missing_segment_result_fails(self):
        self.verification["segments"][0]["status"] = "review"
        with self.assertRaises(ValueError):
            self.check()
        self.verification["segments"].pop()
        with self.assertRaises(ValueError):
            self.check()

    def test_manifest_order_mismatch_fails(self):
        self.assembly["segments"].reverse()
        with self.assertRaises(ValueError):
            self.check()

    def attach_chunks(self):
        self.write("first-chunk.wav", self.first)
        self.chapter["segments"][0]["chunks"] = [{"index": 0, "sourceText": "First",
            "audioPath": str(self.root / "first-chunk.wav"), "pauseAfterSeconds": 0}]
        self.verification["segments"][0]["chunks"] = [{"chunkIndex": 0, "expectedText": "First", "status": "pass"}]

    def test_segment_must_match_its_verified_chunks(self):
        self.attach_chunks()
        self.assertEqual(2, self.check()["verifiedSegmentCount"])
        self.write("first-chunk.wav", b"\x30\x00" * 80)
        with self.assertRaisesRegex(ValueError, "differs from verified chunks"):
            self.check()

    def test_unverified_or_changed_chunk_text_cannot_pass(self):
        self.attach_chunks()
        self.verification["segments"][0]["chunks"][0]["status"] = "review"
        with self.assertRaises(ValueError):
            self.check()
        self.verification["segments"][0]["chunks"][0]["status"] = "pass"
        self.chapter["segments"][0]["chunks"][0]["sourceText"] = "Different"
        with self.assertRaises(ValueError):
            self.check()

    def test_chunk_apostrophes_match_transcription_normalization(self):
        self.attach_chunks()
        verified = self.verification["segments"][0]["chunks"][0]
        chunk = self.chapter["segments"][0]["chunks"][0]
        verified["expectedText"] = "You've completed the quest"
        for apostrophe in ("\u2019", "\u00e2\u20ac\u2122"):
            chunk["sourceText"] = "You" + apostrophe + "ve completed the quest"
            self.assertEqual(2, self.check()["verifiedSegmentCount"])
        for changed in ("You completed the quest", "You've completed another quest"):
            chunk["sourceText"] = changed
            with self.assertRaises(ValueError):
                self.check()

    def test_review_export_keeps_pcm_and_text_checks(self):
        self.attach_chunks()
        self.verification["segments"][0]["status"] = "review"
        self.verification["segments"][0]["chunks"][0]["status"] = "review"
        with self.assertRaises(ValueError):
            self.check()
        self.assertEqual(2, verify_assembly_integrity(self.assembly, self.chapter, self.verification, self.path, True)["verifiedSegmentCount"])
        self.write("first-chunk.wav", b"\x30\x00" * 80)
        with self.assertRaisesRegex(ValueError, "differs from verified chunks"):
            verify_assembly_integrity(self.assembly, self.chapter, self.verification, self.path, True)

    def test_failed_recognition_can_export_for_review_but_pcm_must_match(self):
        self.attach_chunks()
        self.verification["segments"][0]["chunks"][0]["status"] = "fail"
        with self.assertRaises(ValueError):
            self.check()
        self.assertEqual(2, verify_assembly_integrity(self.assembly, self.chapter, self.verification, self.path, True)["verifiedSegmentCount"])
        self.write("first-chunk.wav", b"\x30\x00" * 80)
        with self.assertRaises(ValueError):
            verify_assembly_integrity(self.assembly, self.chapter, self.verification, self.path, True)


if __name__ == "__main__":
    unittest.main()
