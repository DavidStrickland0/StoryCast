from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from produce_chapter import (
    create_stage_checkpoint,
    stage_checkpoint_is_reusable,
)


class StageCheckpointTests(unittest.TestCase):
    def test_reuses_unchanged_inputs_settings_and_outputs(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            input_path = root / "input.json"
            output_path = root / "output.wav"

            input_path.write_text(
                '{"value": 1}',
                encoding="utf-8",
            )
            output_path.write_bytes(b"audio")

            settings = {"model": "small.en"}

            checkpoint = create_stage_checkpoint(
                [input_path],
                settings,
                [output_path],
            )

            self.assertTrue(
                stage_checkpoint_is_reusable(
                    checkpoint,
                    [input_path],
                    settings,
                    [output_path],
                )
            )

    def test_rejects_changed_input(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            input_path = root / "input.json"
            output_path = root / "output.wav"

            input_path.write_text(
                '{"value": 1}',
                encoding="utf-8",
            )
            output_path.write_bytes(b"audio")

            checkpoint = create_stage_checkpoint(
                [input_path],
                {},
                [output_path],
            )

            input_path.write_text(
                '{"value": 2}',
                encoding="utf-8",
            )

            self.assertFalse(
                stage_checkpoint_is_reusable(
                    checkpoint,
                    [input_path],
                    {},
                    [output_path],
                )
            )

    def test_rejects_changed_output(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            input_path = root / "input.json"
            output_path = root / "output.wav"

            input_path.write_text(
                "{}",
                encoding="utf-8",
            )
            output_path.write_bytes(b"audio")

            checkpoint = create_stage_checkpoint(
                [input_path],
                {},
                [output_path],
            )

            output_path.write_bytes(b"replacement")

            self.assertFalse(
                stage_checkpoint_is_reusable(
                    checkpoint,
                    [input_path],
                    {},
                    [output_path],
                )
            )

    def test_rejects_changed_settings_or_missing_output(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            input_path = root / "input.json"
            output_path = root / "output.wav"

            input_path.write_text(
                "{}",
                encoding="utf-8",
            )
            output_path.write_bytes(b"audio")

            checkpoint = create_stage_checkpoint(
                [input_path],
                {"pause": 0.18},
                [output_path],
            )

            self.assertFalse(
                stage_checkpoint_is_reusable(
                    checkpoint,
                    [input_path],
                    {"pause": 0.25},
                    [output_path],
                )
            )

            output_path.unlink()

            self.assertFalse(
                stage_checkpoint_is_reusable(
                    checkpoint,
                    [input_path],
                    {"pause": 0.18},
                    [output_path],
                )
            )


if __name__ == "__main__":
    unittest.main()