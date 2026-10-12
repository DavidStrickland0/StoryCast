import re
import unittest
from synthesize_chapter_probe import split_synthesis_units


class RewriteChunkFallbackTests(unittest.TestCase):
    def test_smaller_chunks_preserve_every_word(self):
        text = ("Three lights means it is in kill mode. Physical fitness determines how effective the shot will be. "
                "To be certain of a kill shot, it is still best to aim for the head. ") * 3
        units = split_synthesis_units(text, set(), max_characters=160)
        self.assertGreater(len(units), 1)
        self.assertTrue(all(len(chunk) <= 160 for chunk, pause in units))
        self.assertEqual(re.findall(r"\w+", text), re.findall(r"\w+", " ".join(chunk for chunk, pause in units)))
