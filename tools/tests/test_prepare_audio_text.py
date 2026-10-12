import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
from io import BytesIO

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import prepare_audio_text as preparation


class AudioPreparationTests(unittest.TestCase):
    def test_accepts_abbreviation_and_roman_expansions_without_losing_identity(self):
        source = "The OS describes World War I and World War II."
        spoken = "The O S describes World War one and World War two."
        self.assertEqual(spoken, preparation.safe_text(source))
        self.assertEqual(spoken, preparation.validate_replacement(source, spoken))
        for changed in (spoken.replace("one", "two"), spoken.replace("O S", "is")):
            with self.assertRaises(ValueError):
                preparation.validate_replacement(source, changed)

    def test_cannot_invent_dialogue_from_quoted_blank_lines(self):
        with self.assertRaisesRegex(ValueError, "empty source"):
            preparation.validate_replacement('"\n\n"', "I will tell you.")
    def test_elongated_name_uses_chapter_spelling_and_preserves_double_letters(self):
        vocabulary = preparation.chapter_vocabulary([{"sourceText": "Jenny saw Adam. JJJEEEEENNNNNYYYYY!"}])
        self.assertEqual("Jenny!", preparation.safe_text("JJJEEEEENNNNNYYYYY!", vocabulary))
        self.assertEqual("bookkeeper", preparation.safe_text("bookkeeper", vocabulary))
        self.assertEqual("ZZZAAA!", preparation.safe_text("ZZZAAA!", vocabulary))

    def test_rejects_omissions_new_facts_names_and_numbers(self):
        source = "Jenny paid $12 to Viktor at 5 PM."
        for replacement in ("Jenny paid $13 to Viktor at 5 PM.", "Denny paid $12 to Viktor at 5 PM.",
                            "Jenny paid $12.", source + " Then he died."):
            with self.subTest(replacement=replacement), self.assertRaises(ValueError):
                preparation.validate_replacement(source, replacement)
        self.assertEqual("Jenny paid twelve dollars to Viktor at five PM!", preparation.validate_replacement(
            source, "Jenny paid twelve dollars to Viktor at five PM!"))

    def test_caches_audited_preparation_and_falls_back_without_losing_text(self):
        for valid in (True, False):
            with self.subTest(valid=valid), tempfile.TemporaryDirectory() as temporary:
                book = Path(temporary)
                scripts = book / "production" / "scripts"
                scripts.mkdir(parents=True)
                artifact = {"script": {"segments": [{"index": 0, "speakerId": "narrator", "sourceText": "Jenny paid $12."},
                                                     {"index": 1, "speakerId": "adam", "sourceText": "JJJEEEEENNNNNYYYYY!"}]}}
                (scripts / "chapter-001.json").write_text(json.dumps(artifact))
                def reply(*args, **kwargs):
                    prompt = json.loads(json.loads(args[0].data)["prompt"])
                    response = {"segments": {str(s["index"]):
                        ("Jenny paid twelve dollars!" if valid else "Jenny died.") if s["index"] == 0 else "Jenny!"
                        for s in prompt["segments"]}}
                    return BytesIO(json.dumps({"response": json.dumps(response)}).encode())
                with patch.object(preparation.urllib.request, "urlopen", side_effect=reply) as request:
                    path = preparation.prepare_chapter(book, "chapter-001", "model", "http://ollama")
                    self.assertEqual(1 if valid else 3, request.call_count)
                    preparation.prepare_chapter(book, "chapter-001", "model", "http://ollama")
                    self.assertEqual(1 if valid else 3, request.call_count)
                saved = json.loads(path.read_text())
                self.assertEqual("ai-validated" if valid else "deterministic-fallback", saved["segments"][0]["mode"])
                self.assertEqual("ai-validated", saved["segments"][1]["mode"])
                self.assertEqual("Jenny paid $12.", saved["segments"][0]["sourceText"])
                self.assertEqual("Jenny!", preparation.load_prepared(book, "chapter-001", artifact)[1])
                saved["segments"].pop()
                path.write_text(json.dumps(saved))
                with self.assertRaisesRegex(ValueError, "omitted segments"):
                    preparation.load_prepared(book, "chapter-001", artifact)


if __name__ == "__main__":
    unittest.main()
