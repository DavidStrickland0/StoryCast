from __future__ import annotations

import json
import io
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
    def test_default_uses_three_tries_and_three_rewrites(self) -> None:
        with patch.object(
            sys,
            "argv",
            ["produce_chapter.py", "book", "voices", "--chapter", "chapter-001"],
        ):
            arguments = produce_chapter.parse_args()

        self.assertEqual(3, arguments.max_segment_attempts)
        self.assertEqual(3, arguments.max_fragment_rewrites)
        self.assertEqual(
            12,
            arguments.max_segment_attempts *
            (arguments.max_fragment_rewrites + 1),
        )

    def create_short_fragment_fixture(
        self,
        root: Path,
        indexes: tuple[int, ...] = (7,),
    ) -> tuple[Path, Path, dict[int, Path]]:
        chapter_directory = root / "chapter-001"
        chapter_directory.mkdir()
        audio_paths: dict[int, Path] = {}
        segments: list[dict] = []

        for index in indexes:
            audio_path = chapter_directory / f"segment-{index:04d}.wav"
            audio_path.write_bytes(f"initial-{index}".encode())
            audio_paths[index] = audio_path
            segments.append(
                {
                    "index": index,
                    "speakerId": "speaker",
                    "sourceText": f"Fragment {index}",
                    "audioPath": str(audio_path),
                }
            )

        chapter_manifest = chapter_directory / "chapter.json"
        chapter_manifest.write_text(
            json.dumps({"segments": segments}),
            encoding="utf-8",
        )
        return (
            chapter_manifest,
            chapter_directory / "verification.json",
            audio_paths,
        )

    @staticmethod
    def write_verification(
        path: Path,
        audio_paths: dict[int, Path],
        statuses: dict[int, str],
        attempt: int,
    ) -> None:
        segments = []

        for index, status in statuses.items():
            segments.append(
                {
                    "segmentIndex": index,
                    "speakerId": "speaker",
                    "voiceId": "voice",
                    "expectedWordCount": 2,
                    "transcribedWordCount": 2 + attempt,
                    "editDistance": attempt,
                    "wordErrorRate": float(attempt),
                    "status": status,
                    "verificationMode": "transcript-short-utterance",
                    "expectedText": f"Fragment {index}",
                    "transcription": f"attempt {attempt}",
                    "audioPath": str(audio_paths[index]),
                }
            )

        report = {
            "summary": {
                "total": len(segments),
                "passed": sum(item["status"] == "pass" for item in segments),
                "review": sum(item["status"] == "review" for item in segments),
                "failed": sum(item["status"] == "fail" for item in segments),
            },
            "segments": segments,
        }
        path.write_text(json.dumps(report), encoding="utf-8")

    def test_completed_retry_state_with_review_is_reverified(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manifest, verification, audio = self.create_short_fragment_fixture(root)
            self.write_verification(verification, audio, {7: "review"}, 1)
            state_path = manifest.parent / 'segment-retry-state.json'
            state_path.write_text(json.dumps({
                'settings': {'maxAttemptsPerWording': 3, 'maxFragmentRewrites': 3, 'totalAttempts': 12},
                'completed': True, 'attempt': 1, 'pendingAction': 'complete',
                'rewriteCounts': {}, 'bestCandidates': {},
            }))
            def verify(name, arguments, allowed_exit_codes=(0,)):
                self.write_verification(verification, audio, {7: 'pass'}, 1)
                return 0
            with patch.object(produce_chapter, 'run_stage', side_effect=verify) as runner:
                produce_chapter.verify_segments_with_retries(
                    TOOLS_DIRECTORY, root, root / 'voices', 'chapter-001', root,
                    manifest, 'small.en', 3, [],
                )
            self.assertEqual(1, runner.call_count)
            self.assertEqual('pass', json.loads(verification.read_text())['segments'][0]['status'])
            self.assertTrue(json.loads(state_path.read_text())['completed'])

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

    def test_uses_three_candidates_for_each_of_four_wordings(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manifest, verification, audio_paths = (
                self.create_short_fragment_fixture(root)
            )
            calls: list[tuple[str, list[str]]] = []
            rewrites: list[tuple[int, int]] = []

            def fake_run_stage(
                name: str,
                arguments: list[str],
                allowed_exit_codes: tuple[int, ...] = (0,),
            ) -> int:
                calls.append((name, arguments))

                if name.startswith("Segment verification"):
                    attempt = int(name.rsplit(" ", 1)[1])
                    audio_paths[7].write_bytes(f"candidate-{attempt}".encode())
                    self.write_verification(
                        verification,
                        audio_paths,
                        {7: "fail"},
                        attempt,
                    )
                    return 2

                return 0

            def fake_rewrite(
                chapter_manifest: Path,
                segment_index: int,
                rewrite_number: int,
                model: str,
                ollama_url: str,
            ) -> str:
                rewrites.append((segment_index, rewrite_number))
                return f"rewrite {rewrite_number}"

            history: list[dict] = []

            with (
                patch.object(produce_chapter, "run_stage", side_effect=fake_run_stage),
                patch.object(
                    produce_chapter,
                    "rewrite_short_fragment",
                    side_effect=fake_rewrite,
                ),
            ):
                produce_chapter.verify_segments_with_retries(
                    TOOLS_DIRECTORY,
                    root,
                    root / "voices",
                    "chapter-001",
                    root,
                    manifest,
                    "small.en",
                    3,
                    history,
                    max_fragment_rewrites=3,
                )

            verification_attempts = [
                name
                for name, _ in calls
                if name.startswith("Segment verification")
            ]
            self.assertEqual(12, len(verification_attempts))
            self.assertEqual([(7, 1), (7, 2), (7, 3)], rewrites)
            self.assertTrue(
                all(
                    (
                        manifest.parent /
                        "short-utterance-candidates" /
                        f"segment-0007-attempt-{attempt:02d}.wav"
                    ).is_file()
                    for attempt in range(1, 13)
                )
            )

            final_report = json.loads(verification.read_text(encoding="utf-8"))
            selected = final_report["segments"][0]
            self.assertEqual("review", selected["status"])
            self.assertEqual(1, selected["selectedCandidateAttempt"])
            self.assertEqual(12, selected["candidateAttempts"])
            self.assertEqual(b"candidate-1", audio_paths[7].read_bytes())

    def test_resume_continues_pending_rewrite_phase(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manifest, verification, audio_paths = (
                self.create_short_fragment_fixture(root)
            )
            verification_attempts: list[int] = []
            rewrites: list[tuple[int, int]] = []
            fail_synthesis_once = True

            def fake_run_stage(
                name: str,
                arguments: list[str],
                allowed_exit_codes: tuple[int, ...] = (0,),
            ) -> int:
                nonlocal fail_synthesis_once

                if name.startswith("Segment verification"):
                    attempt = int(name.rsplit(" ", 1)[1])
                    verification_attempts.append(attempt)
                    audio_paths[7].write_bytes(f"candidate-{attempt}".encode())
                    status = "pass" if attempt == 11 else "fail"
                    self.write_verification(
                        verification,
                        audio_paths,
                        {7: status},
                        attempt,
                    )
                    return 0 if status == "pass" else 2

                if name == "Selective synthesis retry 10" and fail_synthesis_once:
                    fail_synthesis_once = False
                    raise RuntimeError("simulated worker interruption")

                return 0

            def fake_rewrite(
                chapter_manifest: Path,
                segment_index: int,
                rewrite_number: int,
                model: str,
                ollama_url: str,
            ) -> str:
                rewrites.append((segment_index, rewrite_number))
                return f"rewrite {rewrite_number}"

            arguments = (
                TOOLS_DIRECTORY,
                root,
                root / "voices",
                "chapter-001",
                root,
                manifest,
                "small.en",
                10,
            )

            with (
                patch.object(produce_chapter, "run_stage", side_effect=fake_run_stage),
                patch.object(
                    produce_chapter,
                    "rewrite_short_fragment",
                    side_effect=fake_rewrite,
                ),
            ):
                with self.assertRaisesRegex(RuntimeError, "interruption"):
                    produce_chapter.verify_segments_with_retries(
                        *arguments,
                        [],
                        max_fragment_rewrites=2,
                    )

                produce_chapter.verify_segments_with_retries(
                    *arguments,
                    [],
                    max_fragment_rewrites=2,
                )

            self.assertEqual(list(range(1, 12)), verification_attempts)
            self.assertEqual([(7, 1)], rewrites)
            state = json.loads(
                (manifest.parent / "segment-retry-state.json").read_text(
                    encoding="utf-8"
                )
            )
            self.assertTrue(state["completed"])

    def test_migrates_existing_ten_attempt_run_without_repeating_it(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manifest, verification, audio_paths = (
                self.create_short_fragment_fixture(root)
            )
            candidate_directory = manifest.parent / "short-utterance-candidates"
            candidate_directory.mkdir()

            for attempt in range(1, 11):
                self.write_verification(
                    verification,
                    audio_paths,
                    {7: "fail"},
                    attempt,
                )
                (manifest.parent / f"verification-attempt-{attempt}.json").write_text(
                    verification.read_text(encoding="utf-8"),
                    encoding="utf-8",
                )
                (candidate_directory / f"segment-0007-attempt-{attempt:02d}.wav").write_bytes(
                    f"legacy-{attempt}".encode()
                )

            verification_attempts: list[int] = []
            synthesis_attempts: list[int] = []
            rewrites: list[tuple[int, int]] = []

            def fake_run_stage(
                name: str,
                arguments: list[str],
                allowed_exit_codes: tuple[int, ...] = (0,),
            ) -> int:
                if name.startswith("Selective synthesis"):
                    synthesis_attempts.append(int(name.rsplit(" ", 1)[1]))
                    return 0

                if name.startswith("Segment verification"):
                    attempt = int(name.rsplit(" ", 1)[1])
                    verification_attempts.append(attempt)
                    self.write_verification(
                        verification,
                        audio_paths,
                        {7: "pass"},
                        attempt,
                    )
                    return 0

                return 0

            def fake_rewrite(
                chapter_manifest: Path,
                segment_index: int,
                rewrite_number: int,
                model: str,
                ollama_url: str,
            ) -> str:
                rewrites.append((segment_index, rewrite_number))
                return "rewritten fragment"

            with (
                patch.object(produce_chapter, "run_stage", side_effect=fake_run_stage),
                patch.object(
                    produce_chapter,
                    "rewrite_short_fragment",
                    side_effect=fake_rewrite,
                ),
            ):
                produce_chapter.verify_segments_with_retries(
                    TOOLS_DIRECTORY,
                    root,
                    root / "voices",
                    "chapter-001",
                    root,
                    manifest,
                    "small.en",
                    10,
                    [],
                    max_fragment_rewrites=2,
                )

            self.assertEqual([(7, 1)], rewrites)
            self.assertEqual([10], synthesis_attempts)
            self.assertEqual([11], verification_attempts)

    def test_two_segments_progress_independently(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manifest, verification, audio_paths = (
                self.create_short_fragment_fixture(root, (3, 7))
            )
            selected_by_attempt: dict[int, list[str]] = {}

            def fake_run_stage(
                name: str,
                arguments: list[str],
                allowed_exit_codes: tuple[int, ...] = (0,),
            ) -> int:
                if not name.startswith("Segment verification"):
                    return 0

                attempt = int(name.rsplit(" ", 1)[1])
                selected_by_attempt[attempt] = [
                    arguments[index + 1]
                    for index, value in enumerate(arguments)
                    if value == "--segment-index"
                ]
                statuses = {
                    3: "pass" if attempt >= 2 else "fail",
                    7: "pass" if attempt >= 3 else "fail",
                }
                for index in audio_paths:
                    audio_paths[index].write_bytes(
                        f"candidate-{index}-{attempt}".encode()
                    )
                self.write_verification(
                    verification,
                    audio_paths,
                    statuses,
                    attempt,
                )
                return 0 if all(value == "pass" for value in statuses.values()) else 2

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
                    manifest,
                    "small.en",
                    10,
                    [],
                )

            self.assertEqual([], selected_by_attempt[1])
            self.assertEqual(["3", "7"], selected_by_attempt[2])
            self.assertEqual(["7"], selected_by_attempt[3])

    def test_rewrite_changes_only_target_spoken_text(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manifest, _, _ = self.create_short_fragment_fixture(
                root,
                (3, 7),
            )
            original = json.loads(manifest.read_text(encoding="utf-8"))

            class FakeResponse:
                def __enter__(self):
                    return self

                def __exit__(self, exception_type, exception, traceback):
                    return False

                @staticmethod
                def read() -> bytes:
                    return json.dumps(
                        {
                            "response": json.dumps(
                                {"spokenText": "Hey, asshole!"}
                            )
                        }
                    ).encode("utf-8")

            with patch.object(
                produce_chapter.urllib.request,
                "urlopen",
                return_value=FakeResponse(),
            ):
                result = produce_chapter.rewrite_short_fragment(
                    manifest,
                    7,
                    1,
                    "model",
                    "http://ollama/",
                )

            updated = json.loads(manifest.read_text(encoding="utf-8"))
            by_index = {
                int(segment["index"]): segment
                for segment in updated["segments"]
            }
            original_by_index = {
                int(segment["index"]): segment
                for segment in original["segments"]
            }

            self.assertEqual("Hey, asshole!", result)
            self.assertEqual(
                original_by_index[7]["sourceText"],
                by_index[7]["sourceText"],
            )
            self.assertEqual("Hey, asshole!", by_index[7]["spokenText"])
            self.assertEqual(original_by_index[3], by_index[3])
            self.assertEqual(1, len(by_index[7]["fragmentRewrites"]))


class FragmentRewriteRecoveryTests(unittest.TestCase):
    def make_manifest(self, root):
        path = root / "chapter.json"
        path.write_text(json.dumps({"segments": [
            {"index": 22, "speakerId": "elias", "sourceText": '"Run!"',
             "spokenText": "Run!", "fragmentRewrites": [{"spokenText": "Run quickly!"}]},
            {"index": 23, "speakerId": "narrator", "sourceText": "He pushed Kaelen toward the entrance."},
        ]}), encoding="utf-8")
        return path

    def response(self, text):
        return io.BytesIO(json.dumps({"response": json.dumps({"spokenText": text})}).encode())

    def test_corrects_unchanged_and_repeated_words_without_mutating_other_segments(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = self.make_manifest(Path(temporary))
            original = json.loads(path.read_text())
            with patch.object(produce_chapter.urllib.request, "urlopen", side_effect=[
                self.response('" RUN, "'), self.response("Run quickly."), self.response("Run now!"),
            ]) as request:
                result = produce_chapter.rewrite_short_fragment(path, 22, 2, "model", "http://ollama")
            self.assertEqual("Run now!", result)
            self.assertEqual(3, request.call_count)
            prompt = json.loads(request.call_args_list[1].args[0].data)["prompt"]
            self.assertIn("Correction:", prompt)
            updated = json.loads(path.read_text())
            self.assertEqual(original["segments"][1], updated["segments"][1])
            self.assertEqual(original["segments"][0]["sourceText"], updated["segments"][0]["sourceText"])
            self.assertEqual(2, len(updated["segments"][0]["fragmentRewrites"]))

    def test_exhaustion_is_bounded_and_preserves_manifest(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = self.make_manifest(Path(temporary))
            original = path.read_bytes()
            with patch.object(produce_chapter.urllib.request, "urlopen", side_effect=[
                self.response("Run!"), self.response("Run!"), self.response("Run!"),
            ]) as request:
                with self.assertRaisesRegex(RuntimeError, "after 3 corrective attempts"):
                    produce_chapter.rewrite_short_fragment(path, 22, 2, "model", "http://ollama")
            self.assertEqual(3, request.call_count)
            self.assertEqual(original, path.read_bytes())

    def test_malformed_response_recovers_and_transport_failure_does_not_retry(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = self.make_manifest(Path(temporary))
            with patch.object(produce_chapter.urllib.request, "urlopen", side_effect=[
                io.BytesIO(b"not JSON"), self.response("Run now!"),
            ]):
                self.assertEqual("Run now!", produce_chapter.rewrite_short_fragment(path, 22, 2, "model", "http://ollama"))
            with patch.object(produce_chapter.urllib.request, "urlopen", side_effect=produce_chapter.urllib.error.URLError("offline")) as request:
                with self.assertRaisesRegex(RuntimeError, "Ollama fragment rewrite failed"):
                    produce_chapter.rewrite_short_fragment(path, 22, 3, "model", "http://ollama")
            self.assertEqual(1, request.call_count)


class ChunkRetryTests(unittest.TestCase):
    def test_retries_only_rejected_chunks(self) -> None:
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
            synthesis_calls: list[list[str]] = []

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
                            (0, 0, "pass"),
                            (0, 1, "review"),
                            (1, 0, "fail"),
                        ]
                        exit_code = 2
                    else:
                        statuses = [
                            (0, 0, "pass"),
                            (0, 1, "pass"),
                            (1, 0, "pass"),
                        ]
                        exit_code = 0

                    report = {
                        "summary": {
                            "passed": sum(
                                status == "pass"
                                for _, _, status in statuses
                            ),
                            "review": sum(
                                status == "review"
                                for _, _, status in statuses
                            ),
                            "failed": sum(
                                status == "fail"
                                for _, _, status in statuses
                            ),
                        },
                        "segments": [
                            {
                                "segmentIndex": 0,
                                "status": (
                                    "review"
                                    if verification_number == 1
                                    else "pass"
                                ),
                            },
                            {
                                "segmentIndex": 1,
                                "status": (
                                    "fail"
                                    if verification_number == 1
                                    else "pass"
                                ),
                            }
                        ],
                        "chunks": [
                            {
                                "segmentIndex": segment_index,
                                "chunkIndex": chunk_index,
                                "status": status,
                            }
                            for segment_index, chunk_index, status in statuses
                        ],
                    }

                    verification_path.write_text(
                        json.dumps(report),
                        encoding="utf-8",
                    )

                    return exit_code

                return 0

            history: list[dict] = []

            def fake_synthesis_runner(
                name: str,
                arguments: list[str],
            ) -> int:
                self.assertTrue(
                    name.startswith("Selective synthesis")
                )
                synthesis_calls.append(arguments)
                return 0

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
                    synthesis_runner=fake_synthesis_runner,
                )

            self.assertEqual(
                1,
                len(synthesis_calls),
            )

            retry_arguments = synthesis_calls[0]

            selected_chunks = [
                retry_arguments[index + 1]
                for index, value in enumerate(
                    retry_arguments
                )
                if value == "--chunk"
            ]

            self.assertEqual(
                ["0:1", "1:0"],
                selected_chunks,
            )
            self.assertEqual(
                2,
                len(history),
            )
            self.assertEqual(
                [],
                history[0]["rejectedSegmentIndexes"],
            )
            self.assertEqual(
                [
                    {"segmentIndex": 0, "chunkIndex": 1},
                    {"segmentIndex": 1, "chunkIndex": 0},
                ],
                history[0]["rejectedChunks"],
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
