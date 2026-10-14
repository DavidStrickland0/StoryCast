import json
import sys
import tempfile
import unittest
from io import BytesIO
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import context_pronunciations as cp
import prepare_audio_text


class ContextPronunciationTests(unittest.TestCase):
    def segments(self):
        return [dict(index=0, speakerId="narrator", sourceText="She drew her bow. Then she gave a bow.")]

    def reply(self, request, **kwargs):
        payload = json.loads(request.data)
        self.last_payload = payload
        prompt = json.loads(payload["prompt"])
        result = {item["id"]: dict(sense="weapon_or_ribbon" if item["occurrence"] == 0 else "bend_or_ship_front",
                                  evidence=item["text"]) for item in prompt["occurrences"]}
        return BytesIO(json.dumps(dict(response=json.dumps(result))).encode())

    def test_two_senses_in_one_segment_and_across_chunks_preserve_verification_text(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(cp.urllib.request, "urlopen", side_effect=self.reply):
            book = Path(directory)
            manuscript = book / "chapter.md"
            manuscript.write_text(self.segments()[0]["sourceText"])
            before = manuscript.read_bytes()
            path = cp.prepare_context_pronunciations(book, "chapter-001", self.segments(), "model", "http://ollama")
            plan = cp.load_context_plan(book, "chapter-001")["0"]
            units = [("She drew her bow.", .12), ("Then she gave a bow.", .12)]
            rendered = cp.render_units(units, plan, plan["baseText"], include_decisions=True)
            self.assertEqual(["She drew her boh.", "Then she gave a bough."], [item[0] for item in rendered])
            self.assertEqual(["weapon_or_ribbon", "bend_or_ship_front"], [item[1][0]["sense"] for item in rendered])
            self.assertEqual("She drew her bow.", units[0][0])
            self.assertEqual(before, manuscript.read_bytes())
            self.assertEqual([], json.loads(path.read_text())["unresolved"])

    def test_cache_reused_but_changed_context_invalidates_decisions(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(cp.urllib.request, "urlopen", side_effect=self.reply) as request:
            for _ in range(2):
                cp.prepare_context_pronunciations(directory, "chapter-001", self.segments(), "model", "http://ollama")
            self.assertEqual(1, request.call_count)
            changed = [dict(index=0, sourceText="She drew her bow. Then she gave a bow. They left.")]
            cp.prepare_context_pronunciations(directory, "chapter-001", changed, "model", "http://ollama")
            self.assertEqual(2, request.call_count)
            with self.assertRaisesRegex(ValueError, "no longer matches"):
                cp.render_units([("bow", 0)], cp.load_context_plan(directory, "chapter-001")["0"], "different")

    def test_no_model_request_without_ambiguous_words_or_when_manual_override_exists(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(cp.urllib.request, "urlopen") as request:
            cp.prepare_context_pronunciations(directory, "chapter-001", [dict(index=0, sourceText="They walked away.")], "m", "url")
            (Path(directory)/"pronunciations.json").write_text(json.dumps(dict(entries=[dict(canonical="bow", spokenText="boh") ])))
            cp.prepare_context_pronunciations(directory, "chapter-001", self.segments(), "m", "url")
            request.assert_not_called()

    def test_invalid_evidence_or_unapproved_sense_cannot_change_speech(self):
        candidate = cp.candidates_for(self.segments(), [])[0]
        for choice in (dict(sense="weapon_or_ribbon", evidence="invented bow"), dict(sense="rewrite everything", evidence=candidate["text"])):
            with self.assertRaises(ValueError):
                cp.validate_choice(candidate, choice)
        self.assertIsNone(cp.validate_choice(candidate, dict(sense="uncertain")))

    def test_invalid_response_is_retried_and_unresolved_words_keep_their_spelling(self):
        response = lambda *args, **kwargs: BytesIO(json.dumps(dict(response="{}")).encode())
        with tempfile.TemporaryDirectory() as directory, patch.object(cp.urllib.request, "urlopen", side_effect=response) as request:
            path = cp.prepare_context_pronunciations(directory, "chapter-001", self.segments(), "m", "http://ollama")
            self.assertEqual(3, request.call_count)
            saved = json.loads(path.read_text())
            self.assertEqual(2, len(saved["unresolved"]))
            self.assertEqual([], saved["segments"]["0"]["decisions"])

    def test_tampered_synthesis_spelling_is_rejected_on_load(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(cp.urllib.request, "urlopen", side_effect=self.reply):
            path = cp.prepare_context_pronunciations(directory, "chapter-001", self.segments(), "m", "http://ollama")
            saved = json.loads(path.read_text())
            saved["segments"]["0"]["decisions"][0]["synthesisText"] = "new dialogue"
            path.write_text(json.dumps(saved))
            with self.assertRaisesRegex(ValueError, "Unapproved"):
                cp.load_context_plan(directory, "chapter-001")


if __name__ == "__main__":
    unittest.main()
