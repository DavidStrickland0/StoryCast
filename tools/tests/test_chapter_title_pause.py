import json
from pathlib import Path
import tempfile
import unittest
from synthesize_chapter_probe import load_markdown_headings, split_synthesis_units, HEADING_PAUSE_SECONDS
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

    def test_prose_is_not_treated_as_title(self):
        with tempfile.TemporaryDirectory() as directory:
            book = Path(directory)
            (book / 'book.json').write_text(json.dumps({'chapters': ['00001.txt']}))
            (book / '00001.txt').write_text('The train arrived.\nChapter 2 was different.')
            self.assertEqual(set(), load_markdown_headings(book, 'chapter-001'))
