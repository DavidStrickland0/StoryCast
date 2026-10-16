from __future__ import annotations

import copy
import hashlib
import json
import tempfile
import unittest
from io import BytesIO
from pathlib import Path
from unittest.mock import patch

import delivery_planning as delivery
from prosody import resolve_segment_synthesis_settings


class DeliveryPlanningTests(unittest.TestCase):
    def test_analytical_out_of_range_temperature_is_bounded_and_audited(self):
        segments = [{"index": 0, "speakerId": "elara", "sourceText": "Look at the ropes.", "delivery": "analytical"}]
        proposed = {"exaggeration": -.1, "cfgWeight": .05, "temperature": .7}
        with tempfile.TemporaryDirectory() as temporary:
            with patch.object(delivery.urllib.request, "urlopen", return_value=BytesIO(
                    json.dumps({"response": json.dumps(proposed)}).encode())) as request:
                path = delivery.prepare_delivery(temporary, "chapter-003", segments, "model", "http://ollama")
            self.assertEqual(1, request.call_count)
            saved = json.loads(path.read_text())["directions"]["analytical"]
            self.assertEqual(proposed, saved["requestedAdjustments"])
            self.assertEqual(["temperature"], saved["clampedAdjustments"])
            self.assertEqual(.1, saved["adjustments"]["temperature"])
            plan = delivery.load_delivery(temporary, "chapter-003", segments)[0]
            settings = resolve_segment_synthesis_settings({}, "elara", segments[0]["sourceText"], "analytical", plan)
            self.assertLessEqual(settings["temperature"], .85)

    def test_generated_plan_still_rejects_invalid_types_and_identity_changes(self):
        baseline = {"exaggeration": 0, "cfgWeight": 0, "temperature": 0}
        for value in (True, "0.7", None, float("nan"), float("inf")):
            with self.subTest(value=value), self.assertRaises(ValueError):
                delivery.normalize_generated_plan({**baseline, "temperature": value}, "analytical")
        with self.assertRaises(ValueError):
            delivery.normalize_generated_plan({**baseline, "voiceId": "other-character"}, "analytical")
        bounded = delivery.normalize_generated_plan({"exaggeration": -99, "cfgWeight": 99, "temperature": -99}, "any style")
        self.assertEqual({"exaggeration": -.3, "cfgWeight": .1, "temperature": -.2}, bounded["adjustments"])

    def test_free_form_directions_keep_identity_and_words_and_cache(self):
        segments = [{"index": i, "speakerId": speaker, "sourceText": "Stay behind me.", "delivery": direction}
                    for i, (speaker, direction) in enumerate([
                        ("maren", "barely containing excitement"), ("torin", "dryly amused through tears"),
                        ("maren", "whispering"), ("torin", "yelling"), ("maren", "screaming"),
                        ("torin", "quiet"), ("maren", "thoughtful")])]
        originals = copy.deepcopy(segments)
        with tempfile.TemporaryDirectory() as temporary:
            with patch.object(delivery.urllib.request, "urlopen", side_effect=lambda *a, **k:
                    BytesIO(json.dumps({"response": json.dumps({"exaggeration": -.1, "cfgWeight": .05, "temperature": -.1})}).encode())) as request:
                delivery.prepare_delivery(temporary, "chapter-001", segments, "model", "http://ollama")
                delivery.prepare_delivery(temporary, "chapter-001", segments, "model", "http://ollama")
                self.assertEqual(len(segments), request.call_count)
            plans = delivery.load_delivery(temporary, "chapter-001", segments)
            self.assertEqual(len(segments), len(plans))
            for segment in segments:
                assignment = {"voiceId": segment["speakerId"] + "-unique", "synthesis": {"exaggeration": .65}}
                before = copy.deepcopy(assignment)
                settings = resolve_segment_synthesis_settings(assignment, segment["speakerId"], segment["sourceText"], segment["delivery"], plans[segment["index"]])
                self.assertEqual("free-form", settings["profile"])
                self.assertEqual(segment["delivery"], settings["deliveryDirection"])
                self.assertEqual(before, assignment)
            self.assertEqual(originals, segments)
            changed = copy.deepcopy(segments)
            changed[0]["delivery"] = "sarcastically"
            with self.assertRaises(ValueError):
                delivery.load_delivery(temporary, "chapter-001", changed)

    def test_untrusted_plan_cannot_change_voice_or_exceed_limits(self):
        valid = {"exaggeration": 0, "cfgWeight": 0, "temperature": 0}
        for plan in ({**valid, "voiceId": "someone-else"}, {**valid, "temperature": 99},
                     {**valid, "temperature": float("nan")}, {**valid, "exaggeration": True}):
            with self.subTest(plan=plan), self.assertRaises(ValueError):
                delivery.validate_plan(plan, "any direction")

    def test_only_approved_same_voice_references_can_be_selected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            base = root / "sample.wav"
            base.write_bytes(b"Maren neutral")
            variant = root / "whisper.wav"
            variant.write_bytes(b"Maren whisper")
            entry = {"direction": "whispering", "aliases": ["whispered"], "voiceId": "maren-unique", "sample": variant.name,
                     "sha256": hashlib.sha256(variant.read_bytes()).hexdigest(), "approved": True}
            manifest = {"id": "maren-unique", "deliveryVariants": [entry]}
            def save():
                (root / "voice.json").write_text(json.dumps(manifest))
            save()
            reference, settings = delivery.reference_settings({}, base, "maren-unique", "whispered")
            self.assertEqual(variant, reference)
            self.assertEqual("approved-same-voice-reference", settings["referenceMode"])
            entry["approved"] = False
            save()
            self.assertEqual(base, delivery.select_reference(base, "maren-unique", "whispering")[0])
            entry.update(approved=True, voiceId="torin-unique")
            save()
            with self.assertRaises(ValueError):
                delivery.select_reference(base, "maren-unique", "whispering")
            entry["voiceId"] = "maren-unique"
            save()
            variant.write_bytes(b"replaced audio")
            with self.assertRaises(ValueError):
                delivery.select_reference(base, "maren-unique", "whispering")

    def test_direction_changes_invalidate_synthesis_even_with_same_numeric_controls(self):
        a = resolve_segment_synthesis_settings({}, "maren", "Hello.", "sarcastically")
        b = resolve_segment_synthesis_settings({}, "maren", "Hello.", "thoughtfully")
        self.assertNotEqual(a, b)
        self.assertEqual("neutral", resolve_segment_synthesis_settings({}, "maren", "Never whisper or scream.")["profile"])


if __name__ == "__main__":
    unittest.main()
