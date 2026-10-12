import unittest
from copy import deepcopy
from chat_label_recovery import recover_chat_labels, apply_chunk_speech_overrides
from chat_pronunciations import phonetic_spelling
from pronunciations import canonicalize_transcription


class ChatLabelRecoveryTests(unittest.TestCase):
    def test_phonetic_labels_match_only_the_exact_original_letters(self):
        entries = self.fixture()[2]
        phrase = phonetic_spelling("Superhearofangirl")
        self.assertEqual("Super hear of angirl", canonicalize_transcription(phrase, entries))
        self.assertEqual("Super hear of angirl", canonicalize_transcription(phrase.replace("Golf", "Gulf"), entries))
        for changed in (phrase.replace("Hotel", "Bravo"), phrase.replace("Hotel, ", "")):
            self.assertNotEqual("Super hear of angirl", canonicalize_transcription(changed, entries))

    def fixture(self, heard="Earth behind you"):
        text = "[Super hear of angirl (Earth): BEHIND YOU!]"
        manifest = {"segments": [{"index": 0, "sourceText": "Original chapter source",
            "chunks": [{"index": 2, "sourceText": text}, {"index": 3, "sourceText": "Passed speech"}]}]}
        report = {"chunks": [{"segmentIndex": 0, "chunkIndex": 2, "status": "fail", "transcription": heard},
                              {"segmentIndex": 0, "chunkIndex": 3, "status": "pass", "transcription": "Passed speech"}]}
        entries = [{"canonical": "Superhearofangirl", "spokenText": "Super hear of angirl", "mode": "validated-word-split"}]
        return manifest, report, entries

    def test_only_ambiguous_label_is_changed_and_message_and_source_stay_exact(self):
        manifest, report, entries = self.fixture()
        original = deepcopy(manifest)
        self.assertEqual([(0, 2)], recover_chat_labels(manifest, report, entries))
        segment = manifest["segments"][0]
        self.assertEqual(original["segments"][0]["sourceText"], segment["sourceText"])
        self.assertEqual(original["segments"][0]["chunks"], segment["chunks"])
        override = segment["chunkSpeechOverrides"]["2"]
        self.assertEqual("[Sierra, Uniform, Papa, Echo, Romeo, Hotel, Echo, Alpha, Romeo, Oscar, Foxtrot, Alpha, November, Golf, India, Romeo, Lima. (Earth): BEHIND YOU!]", override["sourceText"])
        self.assertEqual([], recover_chat_labels(manifest, report, entries))

    def test_message_errors_never_change_the_source_message(self):
        for heard in ("Earth behind me", "Earth you", "Earth behind you behind you"):
            with self.subTest(heard=heard):
                manifest, report, entries = self.fixture(heard)
                self.assertEqual([(0, 2)], recover_chat_labels(manifest, report, entries))
                self.assertTrue(manifest["segments"][0]["chunkSpeechOverrides"]["2"]["sourceText"].endswith("BEHIND YOU!]"))
                self.assertEqual("fail", report["chunks"][0]["status"])
        manifest, report, entries = self.fixture("Mars behind you")
        self.assertEqual([], recover_chat_labels(manifest, report, entries))

    def test_correct_label_with_other_error_is_not_rewritten(self):
        manifest, report, entries = self.fixture("Super hear of angirl Earth behind me")
        self.assertEqual([], recover_chat_labels(manifest, report, entries))

    def test_old_spelling_can_upgrade_once_without_retrying_passed_chunks(self):
        manifest, report, entries = self.fixture("S U P E R H E R O F A N G I R L Earth behind you")
        segment = manifest["segments"][0]
        original = segment["chunks"][0]["sourceText"]
        old = "[S U P E R H E A R O F A N G I R L (Earth): BEHIND YOU!]"
        segment["chunks"][0]["sourceText"] = old
        segment["chunkSpeechOverrides"] = {"2": {"originalText": original, "sourceText": old,
            "canonical": "Superhearofangirl", "version": 1}}
        self.assertEqual([(0, 2)], recover_chat_labels(manifest, report, entries))
        self.assertEqual(4, segment["chunkSpeechOverrides"]["2"]["version"])
        self.assertEqual(original, segment["chunkSpeechOverrides"]["2"]["originalText"])
        self.assertEqual([], recover_chat_labels(manifest, report, entries))

    def test_overrides_preserve_chunk_indexes_and_pauses_and_survive_resume(self):
        manifest, report, entries = self.fixture()
        recover_chat_labels(manifest, report, entries)
        segment = manifest["segments"][0]
        units = [("Passed zero", .1), ("Passed one", .2), (segment["chunks"][0]["sourceText"], .3), ("Passed speech", 0)]
        result = apply_chunk_speech_overrides(units, segment)
        self.assertEqual(units[:2], result[:2])
        self.assertEqual(units[3], result[3])
        self.assertEqual(.3, result[2][1])
        self.assertEqual(result, apply_chunk_speech_overrides(result, segment))

    def test_stale_or_tampered_override_is_rejected(self):
        manifest, report, entries = self.fixture()
        recover_chat_labels(manifest, report, entries)
        segment = manifest["segments"][0]
        units = [("a", 0), ("b", 0), ("Changed source", 0)]
        with self.assertRaises(ValueError):
            apply_chunk_speech_overrides(units, segment)
        units[2] = (segment["chunks"][0]["sourceText"], 0)
        segment["chunkSpeechOverrides"]["2"]["sourceText"] = segment["chunkSpeechOverrides"]["2"]["sourceText"].replace("BEHIND YOU", "BEHIND ME")
        with self.assertRaises(ValueError):
            apply_chunk_speech_overrides(units, segment)
