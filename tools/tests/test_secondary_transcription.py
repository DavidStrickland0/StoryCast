import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch

from secondary_transcription import SecondaryTranscriber
from verify_chapter_probe import verify_audio


class IndependentVerificationTests(unittest.TestCase):
    def verify(self, expected, primary, secondary, quality=None):
        model = SimpleNamespace(transcribe=Mock(return_value=([SimpleNamespace(text=primary)], None)))
        backup = SimpleNamespace(model_name="medium.en", transcribe=Mock(return_value=secondary))
        quality = quality or {"maximumSilenceSeconds": .1, "leadingSilenceSeconds": .02, "trailingSilenceSeconds": .02}
        with patch("torchaudio.load", return_value=(None, 24000)), patch("audio_postprocessing.analyze_audio", return_value=quality):
            report = verify_audio(model, Path("clip.wav"), expected, [], [], backup)
        return backup, report

    def test_independent_decode_can_resolve_primary_omission_without_text_prompt(self):
        backup, report = self.verify("Super hear of angirl Earth behind you", "Earth behind you", "Super hear of angirl Earth behind you")
        self.assertEqual("pass", report["status"])
        self.assertEqual("transcript-independent-asr", report["verificationMode"])
        self.assertEqual("Earth behind you", report["primaryTranscription"])
        backup.transcribe.assert_called_once_with(Path("clip.wav"))

    def test_missing_or_changed_speech_and_repetition_still_fail(self):
        expected = "Super hear of angirl Earth behind you"
        for secondary in ("Earth behind you", "Super hear of angirl Earth behind me", expected + " " + expected):
            with self.subTest(secondary=secondary):
                _, report = self.verify(expected, "Earth behind you", secondary)
                self.assertNotEqual("pass", report["status"])
                self.assertFalse(report["secondaryAccepted"])

    def test_changed_currency_cannot_pass_a_low_chapter_wer(self):
        expected = "She paid twelve dollars and " + "then walked down the quiet corridor " * 8
        actual = expected.replace("twelve", "thirteen")
        _, report = self.verify(expected, "Unrelated", actual)
        self.assertEqual("fail", report["status"])
        self.assertFalse(report["secondaryAccepted"])

    def test_good_primary_clip_does_not_load_secondary(self):
        backup, report = self.verify("Hello there", "Hello there", "unused")
        self.assertEqual("pass", report["status"])
        backup.transcribe.assert_not_called()

    def test_secondary_failure_does_not_approve_primary_failure(self):
        model = SimpleNamespace(transcribe=Mock(return_value=([SimpleNamespace(text="Missing")], None)))
        backup = SimpleNamespace(model_name="medium.en", transcribe=Mock(side_effect=OSError("offline")))
        quality = {"maximumSilenceSeconds": .1, "leadingSilenceSeconds": .02, "trailingSilenceSeconds": .02}
        with patch("torchaudio.load", return_value=(None, 24000)), patch("audio_postprocessing.analyze_audio", return_value=quality):
            report = verify_audio(model, Path("clip.wav"), "Hello there", [], [], backup)
        self.assertEqual("fail", report["status"])
        self.assertEqual("offline", report["secondaryError"])

    def test_silence_gate_remains_even_when_secondary_text_passes(self):
        _, report = self.verify("Hello there", "Missing", "Hello there",
            {"maximumSilenceSeconds": 2, "leadingSilenceSeconds": .02, "trailingSilenceSeconds": .02})
        self.assertEqual("fail", report["status"])
        self.assertEqual("audio-quality-silence", report["verificationMode"])


class SecondaryTranscriberTests(unittest.TestCase):
    def test_model_is_lazy_and_receives_no_expected_text_or_name_prompt(self):
        model = SimpleNamespace(transcribe=Mock(return_value=([SimpleNamespace(text="Actual audio")], None)))
        with patch("faster_whisper.WhisperModel", return_value=model) as constructor:
            verifier = SecondaryTranscriber()
            constructor.assert_not_called()
            self.assertEqual("Actual audio", verifier.transcribe(Path("clip.wav")))
            verifier.transcribe(Path("second.wav"))
            constructor.assert_called_once()
            self.assertNotIn("initial_prompt", model.transcribe.call_args.kwargs)
            self.assertNotIn("prefix", model.transcribe.call_args.kwargs)

    def test_model_load_failure_is_not_retried_for_every_clip(self):
        with patch("faster_whisper.WhisperModel", side_effect=OSError("offline")) as constructor:
            verifier = SecondaryTranscriber()
            for _ in range(2):
                with self.assertRaises((OSError, RuntimeError)):
                    verifier.transcribe(Path("clip.wav"))
            constructor.assert_called_once()
