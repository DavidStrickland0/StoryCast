import json
import sys
import tempfile
import unittest
from io import BytesIO
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import chat_pronunciations as chat
from pronunciations import apply_pronunciations, canonicalize_transcription, load_pronunciations
from spoken_text import normalize_spoken_text


class ChatPronunciationTests(unittest.TestCase):
    def test_punctuation_only_chat_is_read_literally_without_inventing_dialogue(self):
        self.assertEqual('[User (Earth): three question marks]', normalize_spoken_text('[User (Earth): ???]'))
        self.assertEqual('Really??? [User (Earth): Why???]', normalize_spoken_text('Really??? [User (Earth): Why???]'))

    def test_split_preserves_names_and_numeric_identity(self):
        self.assertEqual('Who stole my duck', chat.validate_split('Whostolemyduck', 'Who stole my duck'))
        self.assertEqual('Noob killer nine thousand', chat.validate_split('Noobkiller9000', 'Noob killer 9000'))
        self.assertEqual('Forever first Commenter', chat.validate_split('Forever1stCommenter', 'Forever 1st Commenter'))
        for proposed in ('Nuclear 9000', 'Noob killer 9001', 'Noob killer'):
            with self.assertRaises(ValueError):
                chat.validate_split('Noobkiller9000', proposed)

    def test_discovery_is_cached_and_shared_with_verification(self):
        with tempfile.TemporaryDirectory() as directory:
            book = Path(directory)
            segments = [{'sourceText': '[Whostolemyduck (Earth): Where?]\n[Noobkiller9000 (Earth): ???]'}]
            reply = {'Whostolemyduck': 'Who stole my duck', 'Noobkiller9000': 'Noob killer 9000'}
            with patch.object(chat.urllib.request, 'urlopen', return_value=BytesIO(json.dumps({'response': json.dumps(reply)}).encode())) as request:
                chat.prepare_chat_pronunciations(book, segments, 'model', 'http://ollama')
                chat.prepare_chat_pronunciations(book, segments, 'model', 'http://ollama')
                self.assertEqual(1, request.call_count)
            entries = load_pronunciations(book)
            self.assertEqual('Who stole my duck: Where?', apply_pronunciations('Whostolemyduck: Where?', entries))
            for text in ('Noob killer 9000', 'Noob killer nine thousand'):
                self.assertEqual('Noob killer nine thousand', canonicalize_transcription(text, entries))
            self.assertNotEqual('Noob killer nine thousand', canonicalize_transcription('Noob killer 9001', entries))
            self.assertEqual('Who stole my duck: Gone?', canonicalize_transcription('Who stole my duck: Gone?', entries))

    def test_comparison_accepts_only_identical_handle_letters_and_digits(self):
        entries = [{'canonical': 'SciFifanboy', 'spokenText': 'Sci Fi fan boy', 'mode': 'validated-word-split'},
                   {'canonical': 'Forever1stCommenter', 'spokenText': 'Forever first Commenter', 'mode': 'validated-word-split'},
                   {'canonical': 'Noobkiller9000', 'spokenText': 'Noob killer nine thousand',
                    'acceptedTranscriptions': ['Noob killer 9000'], 'mode': 'validated-word-split'}]
        for text in ('Sci-fi fanboy', 'SCI FI FANBOY', 'SciFifanboy'):
            self.assertEqual('Sci Fi fan boy', canonicalize_transcription(text, entries))
        self.assertEqual('Forever first Commenter: Why?', canonicalize_transcription('Forever, first, commenter: Why?', entries))
        self.assertEqual('Noob killer nine thousand', canonicalize_transcription('NoobKiller 9000', entries))
        self.assertEqual('Noob killer was right', canonicalize_transcription('Noobkiller was right', entries))
        self.assertEqual('W T F, I R L, P O V', normalize_spoken_text('WTF, IRL, POV'))
        self.assertEqual('Noob killer 9001', canonicalize_transcription('NoobKiller 9001', entries))
        for changed in ('Who stole my dark', 'SciFi fan girl'):
            self.assertEqual(changed, canonicalize_transcription(changed, entries))

    def test_chat_chunking_keeps_label_attached_and_preserves_message(self):
        from synthesis_chunking import split_chat_message
        text = '[Noob killer nine thousand (Earth): He will be able to get in that way. ' + 'The door is locked. ' * 12 + ']'
        chunks = split_chat_message(text, 60)
        self.assertTrue(chunks[0].startswith('[Noob killer nine thousand (Earth): He'))
        self.assertEqual(''.join(text.split()), ''.join(''.join(chunks).split()))
        self.assertTrue(all('Noob' not in chunk for chunk in chunks[1:]))
        self.assertIsNone(split_chat_message('Ordinary prose.', 60))
        rewritten = 'Who stole my duck from Earth: Why is the door locked?'
        self.assertEqual([rewritten], split_chat_message(rewritten, 60))

    def test_invalid_ai_uses_spelling_and_manual_entries_take_precedence(self):
        with tempfile.TemporaryDirectory() as directory:
            book = Path(directory)
            with patch.object(chat.urllib.request, 'urlopen', side_effect=OSError('offline')):
                chat.prepare_chat_pronunciations(book, [{'sourceText': '[ABC123 (Mars): Hi]'}], 'model', 'http://ollama')
            self.assertEqual('A B C 1 2 3', load_pronunciations(book)[0]['spokenText'])
            (book / 'pronunciations.json').write_text(json.dumps({'entries': [{'canonical': 'ABC123', 'spokenText': 'Alpha'}]}))
            self.assertEqual('Alpha', load_pronunciations(book)[0]['spokenText'])
            with patch.object(chat.urllib.request, 'urlopen') as request:
                chat.prepare_chat_pronunciations(book, [{'sourceText': 'The narrator discussed ABC123.'}], 'model', 'http://ollama')
                request.assert_not_called()
