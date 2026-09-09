from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


TOOLS_DIRECTORY = Path(
    __file__
).resolve().parents[1]

sys.path.insert(
    0,
    str(TOOLS_DIRECTORY),
)

import produce_chapter


class SegmentRetryTests(unittest.TestCase):
    def test_retries_only_rejected_segments(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            chapter_directory = root / "chapter-001"
            chapter_directory.mkdir()

            chapter_manifest = (
                chapter_directory /
                "chapter.json"
            )
            chapter_manifest.write_text(
                "{}\n",
                encoding="utf-8",
            )

            verification_path = (
                chapter_directory /
                "verification.json"
            )

            verification_number = 0
            calls: list[tuple[str, list[str]]] = []

            def fake_run_stage(
                name: str,
                arguments: list[str],
                allowed_exit_codes: tuple[int, ...] = (0,),
            ) -> int:
                nonlocal verification_number
                calls.append((name, arguments))

                if name.startswith(
                    "Segment verification"
                ):
                    verification_number += 1

                    if verification_number == 1:
                        statuses = [
                            (0, "pass"),
                            (1, "review"),
                            (2, "fail"),
                        ]
                        exit_code = 2
                    else:
                        statuses = [
                            (0, "pass"),
                            (1, "pass"),
                            (2, "pass"),
                        ]
                        exit_code = 0

                    report = {
                        "summary": {
                            "passed": sum(
                                status == "pass"
                                for _, status in statuses
                            ),
                            "review": sum(
                                status == "review"
                                for _, status in statuses
                            ),
                            "failed": sum(
                                status == "fail"
                                for _, status in statuses
                            ),
                        },
                        "segments": [
                            {
                                "segmentIndex": index,
                                "status": status,
                            }
                            for index, status in statuses
                        ],
                    }

                    verification_path.write_text(
                        json.dumps(report),
                        encoding="utf-8",
                    )

                    return exit_code

                return 0

            history: list[dict] = []

            with patch.object(
                produce_chapter,
                "run_stage",
                side_effect=fake_run_stage,
            ):
                produce_chapter.verify_segments_with_retries(
                    TOOLS_DIRECTORY,
                    root,
                    root / "voices",
                    "chapter-001",
                    root,
                    chapter_manifest,
                    "small.en",
                    3,
                    history,
                )

            synthesis_calls = [
                arguments
                for name, arguments in calls
                if name.startswith(
                    "Selective synthesis"
                )
            ]

            self.assertEqual(
                1,
                len(synthesis_calls),
            )

            retry_arguments = synthesis_calls[0]

            selected_indexes = [
                retry_arguments[index + 1]
                for index, value in enumerate(
                    retry_arguments
                )
                if value == "--segment-index"
            ]

            self.assertEqual(
                ["2"],
                selected_indexes,
            )
            self.assertEqual(
                2,
                len(history),
            )
            self.assertEqual(
                [2],
                history[0]["rejectedSegmentIndexes"],
            )
            self.assertEqual(
                [],
                history[1]["rejectedSegmentIndexes"],
            )
            self.assertTrue(
                (
                    chapter_directory /
                    "verification-attempt-1.json"
                ).is_file()
            )
            self.assertTrue(
                (
                    chapter_directory /
                    "verification-attempt-2.json"
                ).is_file()
            )


if __name__ == "__main__":
    unittest.main()
