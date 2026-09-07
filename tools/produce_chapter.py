from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Produce and verify one complete StoryCast chapter."
        )
    )
    parser.add_argument(
        "book",
        type=Path,
    )
    parser.add_argument(
        "voice_library",
        type=Path,
    )
    parser.add_argument(
        "--chapter",
        required=True,
    )
    parser.add_argument(
        "--whisper-model",
        default="small.en",
    )
    parser.add_argument(
        "--pause",
        type=float,
        default=0.18,
    )
    parser.add_argument(
        "--max-segment-attempts",
        type=int,
        default=3,
    )
    return parser.parse_args()


def create_run_path(book: Path) -> Path:
    timestamp = datetime.now(
        timezone.utc
    ).strftime("%Y%m%d-%H%M%S-%f")[:-3]

    run_id = (
        f"{timestamp}-production-"
        f"{uuid.uuid4().hex[:8]}"
    )

    return (
        book / "output" / run_id
    ).resolve()


def run_stage(
    name: str,
    arguments: list[str],
    allowed_exit_codes: tuple[int, ...] = (0,),
) -> int:
    print()
    print(f"===== {name} =====", flush=True)

    result = subprocess.run(
        arguments,
        check=False,
    )

    if result.returncode not in allowed_exit_codes:
        raise RuntimeError(
            f"{name} failed with exit code "
            f"{result.returncode}."
        )

    return result.returncode


def load_json(path: Path) -> dict:
    with path.open(
        "r",
        encoding="utf-8-sig",
    ) as stream:
        return json.load(stream)


def write_run_manifest(
    path: Path,
    report: dict,
) -> None:
    path.parent.mkdir(
        parents=True,
        exist_ok=True,
    )

    temporary_path = path.with_name(
        f".{path.name}.tmp"
    )

    with temporary_path.open(
        "w",
        encoding="utf-8",
        newline="\n",
    ) as stream:
        json.dump(
            report,
            stream,
            indent=2,
            ensure_ascii=False,
        )
        stream.write("\n")

    temporary_path.replace(path)


def verify_segments_with_retries(
    tools_directory: Path,
    book: Path,
    voice_library: Path,
    chapter_id: str,
    run_directory: Path,
    chapter_manifest: Path,
    whisper_model: str,
    max_attempts: int,
    attempt_history: list[dict],
) -> None:
    verification_path = (
        chapter_manifest.parent /
        "verification.json"
    )

    for attempt in range(
        1,
        max_attempts + 1,
    ):
        exit_code = run_stage(
            f"Segment verification attempt {attempt}",
            [
                sys.executable,
                str(
                    tools_directory /
                    "verify_chapter_probe.py"
                ),
                str(chapter_manifest),
                "--model",
                whisper_model,
            ],
            allowed_exit_codes=(0, 2),
        )

        verification = load_json(
            verification_path
        )

        rejected_segments = [
            segment
            for segment in verification["segments"]
            if segment["status"] != "pass"
        ]

        attempt_report_path = (
            chapter_manifest.parent /
            f"verification-attempt-{attempt}.json"
        )

        shutil.copy2(
            verification_path,
            attempt_report_path,
        )

        attempt_history.append(
            {
                "attempt": attempt,
                "verificationPath":
                    str(attempt_report_path),
                "passed":
                    verification["summary"]["passed"],
                "review":
                    verification["summary"]["review"],
                "failed":
                    verification["summary"]["failed"],
                "rejectedSegmentIndexes": [
                    segment["segmentIndex"]
                    for segment in rejected_segments
                ],
            }
        )

        if exit_code == 0:
            if rejected_segments:
                raise RuntimeError(
                    "Verification returned success while "
                    "reporting rejected segments."
                )

            return

        if not rejected_segments:
            raise RuntimeError(
                "Verification returned failure without "
                "identifying rejected segments."
            )

        if attempt >= max_attempts:
            indexes = [
                segment["segmentIndex"]
                for segment in rejected_segments
            ]

            raise RuntimeError(
                f"Segments {indexes} did not pass after "
                f"{max_attempts} attempts."
            )

        synthesis_arguments = [
            sys.executable,
            str(
                tools_directory /
                "synthesize_chapter_probe.py"
            ),
            str(book),
            str(voice_library),
            "--chapter",
            chapter_id,
            "--run-directory",
            str(run_directory),
            "--attempt",
            str(attempt),
        ]

        for segment in rejected_segments:
            synthesis_arguments.extend(
                [
                    "--segment-index",
                    str(segment["segmentIndex"]),
                ]
            )

        run_stage(
            f"Selective synthesis retry {attempt}",
            synthesis_arguments,
        )

def main() -> int:
    args = parse_args()

    if args.max_segment_attempts < 1:
        raise ValueError(
            "--max-segment-attempts must be at least 1."
        )

    book = args.book.resolve()
    voice_library = (
        args.voice_library.resolve()
    )

    tools_directory = Path(
        __file__
    ).resolve().parent

    run_directory = create_run_path(book)
    chapter_directory = (
        run_directory / args.chapter
    )

    run_manifest_path = (
        run_directory / "run.json"
    )

    report = {
        "schemaVersion": 1,
        "runId": run_directory.name,
        "bookPath": str(book),
        "voiceLibraryPath":
            str(voice_library),
        "chapterId": args.chapter,
        "startedUtc": datetime.now(
            timezone.utc
        ).isoformat(),
        "completedUtc": None,
        "status": "running",
        "failedStage": None,
        "settings": {
            "whisperModel":
                args.whisper_model,
            "pauseSeconds": args.pause,
            "maxSegmentAttempts":
                args.max_segment_attempts,
        },
        "segmentVerificationAttempts": [],
        "chapterDirectory":
            str(chapter_directory),
        "masteredAudioPath": None,
    }

    write_run_manifest(
        run_manifest_path,
        report,
    )

    current_stage = "initialization"

    try:
        current_stage = "synthesis"

        run_stage(
            "Synthesis",
            [
                sys.executable,
                str(
                    tools_directory /
                    "synthesize_chapter_probe.py"
                ),
                str(book),
                str(voice_library),
                "--chapter",
                args.chapter,
                "--run-directory",
                str(run_directory),
            ],
        )

        chapter_manifest = (
            chapter_directory /
            "chapter.json"
        )

        current_stage = "segment verification"

        verify_segments_with_retries(
            tools_directory,
            book,
            voice_library,
            args.chapter,
            run_directory,
            chapter_manifest,
            args.whisper_model,
            args.max_segment_attempts,
            report["segmentVerificationAttempts"],
        )

        current_stage = "assembly"

        run_stage(
            "Assembly",
            [
                sys.executable,
                str(
                    tools_directory /
                    "assemble_chapter_probe.py"
                ),
                str(chapter_manifest),
                "--pause",
                str(args.pause),
            ],
        )

        assembly_manifest = (
            chapter_directory /
            "assembly.json"
        )

        current_stage = "assembly verification"

        run_stage(
            "Assembly verification",
            [
                sys.executable,
                str(
                    tools_directory /
                    "verify_assembly_probe.py"
                ),
                str(assembly_manifest),
                "--model",
                args.whisper_model,
            ],
        )

        current_stage = "mastering"

        run_stage(
            "Mastering",
            [
                sys.executable,
                str(
                    tools_directory /
                    "master_chapter_probe.py"
                ),
                str(assembly_manifest),
            ],
        )

        mastered_audio = (
            chapter_directory /
            f"{args.chapter}.mastered.wav"
        )

        current_stage = "mastered verification"

        run_stage(
            "Mastered verification",
            [
                sys.executable,
                str(
                    tools_directory /
                    "verify_assembly_probe.py"
                ),
                str(assembly_manifest),
                "--audio",
                str(mastered_audio),
                "--report-name",
                "mastered-verification.json",
                "--model",
                args.whisper_model,
            ],
        )

        report["status"] = "completed"
        report["completedUtc"] = (
            datetime.now(
                timezone.utc
            ).isoformat()
        )
        report["masteredAudioPath"] = (
            str(mastered_audio)
        )

        write_run_manifest(
            run_manifest_path,
            report,
        )

        print()
        print("===== Production complete =====")
        print(f"Run:     {run_directory}")
        print(f"Chapter: {args.chapter}")
        print(f"Audio:   {mastered_audio}")

        return 0
    except Exception:
        report["status"] = "failed"
        report["failedStage"] = current_stage
        report["completedUtc"] = (
            datetime.now(
                timezone.utc
            ).isoformat()
        )

        write_run_manifest(
            run_manifest_path,
            report,
        )

        print()
        print(
            f"Production failed during "
            f"{current_stage}.",
            file=sys.stderr,
        )

        raise


if __name__ == "__main__":
    raise SystemExit(main())
