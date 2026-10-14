import hashlib
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import ambience


class AmbienceTests(unittest.TestCase):
    def test_unrepairable_cue_does_not_block_chapter_or_discard_valid_cue(self):
        with tempfile.TemporaryDirectory() as temporary:
            manifest = Path(temporary) / "chapter.json"
            ambience.save(manifest, dict(segments=self.segments()))
            invalid = {**self.cue(), "startSegment": 2, "endSegment": 2, "evidence": "invented waves"}
            response = {"response": json.dumps({"cues": [self.cue(), invalid]})}
            with patch.object(ambience, "request_json", return_value=response) as request:
                result = ambience.plan_ambience(manifest, "model", "http://ollama")
            self.assertEqual(3, request.call_count)
            self.assertEqual(1, len(result))
            self.assertTrue(ambience.load(manifest.parent / "ambience-plan.json")["warnings"])
            self.assertIn("acceptedCues", request.call_args[0][1]["prompt"])

    def test_all_invalid_cues_allow_narration_without_ambience(self):
        with tempfile.TemporaryDirectory() as temporary:
            manifest = Path(temporary) / "chapter.json"
            ambience.save(manifest, dict(segments=self.segments()))
            response = {"response": json.dumps({"cues": [{**self.cue(), "evidence": "invented"}]})}
            with patch.object(ambience, "request_json", return_value=response):
                self.assertEqual([], ambience.plan_ambience(manifest, "model", "http://ollama"))

    def test_evidence_omissions_require_every_fragment_in_source_order(self):
        source = "Rain fell on leaves. They waited. Inside, the fire crackled."
        for quote in ("Rain fell on leaves...the fire crackled.", "Rain fell on leaves…They waited."):
            self.assertTrue(ambience.supported_evidence(quote, source))
        for quote in ("Rain fell on leaves...ocean waves", "the fire crackled...Rain fell", "...", "Rain fell...x"):
            self.assertFalse(ambience.supported_evidence(quote, source))

    def cue(self):
        return dict(startSegment=0, endSegment=1, evidence="Rain fell on leaves.", prompt="Soft rain on forest leaves, no voices or music.",
                    setting="forest", tags=["rain", "forest"], intensity="soft")

    def segments(self):
        return [dict(index=0, sourceText="Rain fell on leaves."), dict(index=1, sourceText="They waited."),
                dict(index=2, sourceText="Inside, the fire crackled.")]

    def test_planner_rejects_unsupported_evidence_and_overlapping_ranges(self):
        self.assertEqual(1, len(ambience.validate_cues([self.cue()], self.segments())))
        for cues in ([{**self.cue(), "evidence": "Ocean waves"}], [self.cue(), self.cue()],
                     [{**self.cue(), "endSegment": 5}], [{**self.cue(), "startSegment": True}]):
            with self.subTest(cues=cues), self.assertRaises(ValueError):
                ambience.validate_cues(cues, self.segments())

    def test_verified_speech_timings_define_cue_boundaries(self):
        assembly = dict(durationSeconds=12, segments=[dict(segmentIndex=0, startSeconds=0, endSeconds=4),
                                                     dict(segmentIndex=1, startSeconds=4.18, endSeconds=8)])
        timed = ambience.timed_cues([self.cue()], assembly)
        self.assertEqual((0, 8), (timed[0]["startSeconds"], timed[0]["endSeconds"]))

    def test_plan_caches_and_corrects_invalid_model_output(self):
        with tempfile.TemporaryDirectory() as temporary:
            manifest = Path(temporary) / "chapter.json"
            ambience.save(manifest, dict(segments=self.segments()))
            replies = [{"response": json.dumps({"cues": [{**self.cue(), "evidence": "invented sound"}]})},
                       {"response": json.dumps({"cues": [self.cue()]})}]
            with patch.object(ambience, "request_json", side_effect=replies) as request:
                result = ambience.plan_ambience(manifest, "model", "http://ollama")
                self.assertEqual(2, request.call_count)
                self.assertEqual(result, ambience.plan_ambience(manifest, "model", "http://ollama"))
                self.assertEqual(2, request.call_count)
                self.assertIn("exact evidence", request.call_args[0][1]["prompt"])
                changed = self.segments()
                changed[0]["voiceId"] = "a different cast"
                ambience.save(manifest, dict(segments=changed))
                self.assertEqual(result, ambience.plan_ambience(manifest, "model", "http://ollama"))
                self.assertEqual(2, request.call_count)

    def test_mixer_uses_fades_ducking_and_preserves_narration_duration(self):
        command = ambience.mix_command("clean.wav", [{**self.cue(), "startSeconds": 2, "endSeconds": 10, "audioPath": "rain.wav"}], "mix.wav")
        graph = command[command.index("-filter_complex") + 1]
        for required in ("sidechaincompress", "duration=first", "afade", "adelay=2000", "volume=-22dB"):
            self.assertIn(required, graph)
        with self.assertRaises(ValueError):
            ambience.mix_command("clean.wav", [], "mix.wav", 0)

    def test_service_mismatch_is_rejected_before_downloading_audio(self):
        with tempfile.TemporaryDirectory() as temporary:
            asset = dict(id="soundforge-" + "a" * 32, verification=dict(passed=True), loopable=True,
                         category="ambience", setting="tavern", tags=["fire"], intensity="soft")
            with patch.object(ambience, "request_json", return_value=dict(asset=asset, reused=True)), \
                 patch.object(ambience.urllib.request, "urlopen") as download:
                with self.assertRaisesRegex(ValueError, "does not match"):
                    ambience.provision_cues([self.cue()], temporary, "http://soundforge")
                download.assert_not_called()


if __name__ == "__main__":
    unittest.main()
