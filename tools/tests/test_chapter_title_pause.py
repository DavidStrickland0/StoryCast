import json
from pathlib import Path
import tempfile
import unittest
from heading_pauses import load_markdown_headings, split_synthesis_units, HEADING_PAUSE_SECONDS
from spoken_text import normalize_spoken_text


class TitlePauseTests(unittest.TestCase):
    def test_imported_plain_text_title_has_heading_pause(self):
        with tempfile.TemporaryDirectory() as directory:
            book = Path(directory)
            (book / 'book.json').write_text(json.dumps({'chapters': ['00001.txt']}))
            text = 'Chapter 1 – Arrival\n\nThe train arrived.'
            (book / '00001.txt').write_text(text, encoding='utf-8')
            headings = load_markdown_headings(book, 'chapter-001')
            units = split_synthesis_units(text, headings)
            self.assertEqual(('Chapter 1 – Arrival', HEADING_PAUSE_SECONDS), units[0])
            self.assertEqual('The train arrived.', units[1][0])
            spoken_units = split_synthesis_units(normalize_spoken_text(text), headings)
            self.assertEqual(('Chapter one – Arrival', HEADING_PAUSE_SECONDS), spoken_units[0])

    def test_number_and_title_in_opening_block_both_pause(self):
        with tempfile.TemporaryDirectory() as directory:
            book = Path(directory)
            (book / 'book.json').write_text(json.dumps({'chapters': ['00001.txt']}))
            text = 'Chapter 1\nArrival\n\nThe train arrived.'
            (book / '00001.txt').write_text(text, encoding='utf-8')
            units = split_synthesis_units(normalize_spoken_text(text), load_markdown_headings(book, 'chapter-001'))
            self.assertEqual([('Chapter one', HEADING_PAUSE_SECONDS), ('Arrival', HEADING_PAUSE_SECONDS)], units[:2])
            self.assertEqual('The train arrived.', units[2][0])

    def test_import_metadata_identifies_title_after_blank_line(self):
        with tempfile.TemporaryDirectory() as directory:
            book = Path(directory)
            (book / 'book.json').write_text(json.dumps({'chapters': ['00001.txt']}))
            (book / 'source.json').write_text(json.dumps({'chapters': [{'path': '00001.txt', 'title': 'Arrival'}]}))
            text = 'Chapter 1\n\nArrival\n\nThe train arrived.'
            (book / '00001.txt').write_text(text)
            units = split_synthesis_units(text, load_markdown_headings(book, 'chapter-001'))
            self.assertEqual(('Arrival', HEADING_PAUSE_SECONDS), units[1])

    def test_title_joined_to_narration_is_split_without_losing_words(self):
        units = split_synthesis_units('Arrival The train arrived.', {'Arrival'})
        self.assertEqual(('Arrival', HEADING_PAUSE_SECONDS), units[0])
        self.assertEqual('The train arrived.', units[1][0])
        self.assertEqual('Arrival The train arrived.', ' '.join(text for text, _ in units))

    def test_number_only_heading_does_not_claim_next_paragraph(self):
        with tempfile.TemporaryDirectory() as directory:
            book = Path(directory)
            (book / 'book.json').write_text(json.dumps({'chapters': ['00001.txt']}))
            (book / '00001.txt').write_text('Chapter 1\n\nThe train arrived.')
            self.assertEqual({'Chapter 1'}, load_markdown_headings(book, 'chapter-001'))

    def test_prose_is_not_treated_as_title(self):
        with tempfile.TemporaryDirectory() as directory:
            book = Path(directory)
            (book / 'book.json').write_text(json.dumps({'chapters': ['00001.txt']}))
            (book / '00001.txt').write_text('The train arrived.\nChapter 2 was different.')
            self.assertEqual(set(), load_markdown_headings(book, 'chapter-001'))
