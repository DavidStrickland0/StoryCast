from __future__ import annotations

import argparse
import hashlib
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
    run_directory_group = (
        parser.add_mutually_exclusive_group()
    )
    run_directory_group.add_argument(
        "--run-directory",
        type=Path,
        default=None,
    )
    run_directory_group.add_argument(
        "--resume-run-directory",
        type=Path,
        default=None,
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


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()

    with path.open("rb") as stream:
        for block in iter(
            lambda: stream.read(1024 * 1024),
            b"",
        ):
            digest.update(block)

    return digest.hexdigest()


def describe_files(
    paths: list[Path],
) -> list[dict]:
    return [
        {
            "path": str(path.resolve()),
            "sha256": sha256_file(
                path.resolve()
            ),
        }
        for path in paths
    ]


def create_stage_checkpoint(
    input_paths: list[Path],
    settings: dict,
    output_paths: list[Path],
) -> dict:
    return {
        "completedUtc": datetime.now(
            timezone.utc
        ).isoformat(),
        "inputs": describe_files(input_paths),
        "settings": settings,
        "outputs": describe_files(output_paths),
    }


def stage_checkpoint_is_reusable(
    checkpoint: dict | None,
    input_paths: list[Path],
    settings: dict,
    output_paths: list[Path],
) -> bool:
    if checkpoint is None:
        return False

    if checkpoint.get("settings") != settings:
        return False

    try:
        inputs = describe_files(input_paths)
        outputs = describe_files(output_paths)
    except (FileNotFoundError, OSError):
        return False

    return (
        checkpoint.get("inputs") == inputs and
        checkpoint.get("outputs") == outputs
    )


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

def segment_audio_paths(
    chapter_manifest: Path,
) -> list[Path]:
    manifest = load_json(chapter_manifest)

    return [
        Path(segment["audioPath"]).resolve()
        for segment in manifest["segments"]
    ]


def run_checkpointed_stage(
    name: str,
    checkpoint_name: str,
    report: dict,
    run_manifest_path: Path,
    input_paths: list[Path],
    settings: dict,
    output_paths: list[Path],
    action,
) -> bool:
    checkpoints = report.setdefault(
        "stageCheckpoints",
        {},
    )

    if stage_checkpoint_is_reusable(
        checkpoints.get(checkpoint_name),
        input_paths,
        settings,
        output_paths,
    ):
        print()
        print(
            f"===== {name} (reused) =====",
            flush=True,
        )
        return False

    action()

    checkpoints[checkpoint_name] = (
        create_stage_checkpoint(
            input_paths,
            settings,
            output_paths,
        )
    )

    write_run_manifest(
        run_manifest_path,
        report,
    )

    return True


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

    is_resume = (
        args.resume_run_directory is not None
    )

    if is_resume:
        run_directory = (
            args.resume_run_directory.resolve()
        )

        if not run_directory.is_dir():
            raise FileNotFoundError(
                f"Resume run directory was not found: "
                f"{run_directory}"
            )
    elif args.run_directory is not None:
        run_directory = (
            args.run_directory.resolve()
        )

        if run_directory.exists():
            raise FileExistsError(
                f"New run directory already exists: "
                f"{run_directory}"
            )
    else:
        run_directory = create_run_path(
            book
        )

    chapter_directory = (
        run_directory / args.chapter
    )

    run_manifest_path = (
        run_directory / "run.json"
    )

    resumed_utc = datetime.now(
        timezone.utc
    ).isoformat()

    if is_resume:
        if not run_manifest_path.is_file():
            raise FileNotFoundError(
                f"Run manifest was not found: "
                f"{run_manifest_path}"
            )

        report = load_json(
            run_manifest_path
        )

        if (
            Path(report.get("bookPath", "")).resolve() !=
                book or
            Path(
                report.get(
                    "voiceLibraryPath",
                    "",
                )
            ).resolve() != voice_library or
            report.get("chapterId") != args.chapter or
            Path(
                report.get(
                    "chapterDirectory",
                    "",
                )
            ).resolve() != chapter_directory
        ):
            raise RuntimeError(
                "Resume run identity does not match the "
                "requested book, voice library, or chapter."
            )

        if report.get("status") == "completed":
            raise RuntimeError(
                "A completed production run cannot be resumed."
            )

        expected_settings = {
            "whisperModel":
                args.whisper_model,
            "pauseSeconds": args.pause,
            "maxSegmentAttempts":
                args.max_segment_attempts,
        }

        if report.get("settings") != expected_settings:
            raise RuntimeError(
                "Resume settings do not match the original run."
            )

        report.setdefault(
            "resumeHistory",
            [],
        ).append(
            {
                "resumedUtc": resumed_utc,
                "previousStatus":
                    report.get("status"),
                "previousFailedStage":
                    report.get("failedStage"),
                "previousVerificationAttempts":
                    report.get(
                        "segmentVerificationAttempts",
                        [],
                    ),
            }
        )

        report["resumeCount"] = (
            report.get("resumeCount", 0) + 1
        )
        report["lastResumedUtc"] = resumed_utc
        report["completedUtc"] = None
        report["status"] = "running"
        report["failedStage"] = None
        report["segmentVerificationAttempts"] = []
        report["masteredAudioPath"] = None
        report.setdefault(
            "stageCheckpoints",
            {},
        )
    else:
        report = {
            "schemaVersion": 1,
            "runId": run_directory.name,
            "bookPath": str(book),
            "voiceLibraryPath":
                str(voice_library),
            "chapterId": args.chapter,
            "startedUtc": resumed_utc,
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
            "stageCheckpoints": {},
            "resumeCount": 0,
            "resumeHistory": [],
            "lastResumedUtc": None,
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

        synthesis_arguments = [
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
        ]

        if is_resume:
            synthesis_arguments.append(
                "--resume"
            )

        run_stage(
            (
                "Resume synthesis"
                if is_resume
                else "Synthesis"
            ),
            synthesis_arguments,
        )

        chapter_manifest = (
            chapter_directory /
            "chapter.json"
        )

        verification_path = (
            chapter_directory /
            "verification.json"
        )

        segment_inputs = [
            chapter_manifest,
            tools_directory /
            "verify_chapter_probe.py",
            *segment_audio_paths(
                chapter_manifest
            ),
        ]

        current_stage = "segment verification"

        run_checkpointed_stage(
            "Segment verification",
            "segmentVerification",
            report,
            run_manifest_path,
            segment_inputs,
            {
                "whisperModel":
                    args.whisper_model,
                "maxSegmentAttempts":
                    args.max_segment_attempts,
            },
            [verification_path],
            lambda: verify_segments_with_retries(
                tools_directory,
                book,
                voice_library,
                args.chapter,
                run_directory,
                chapter_manifest,
                args.whisper_model,
                args.max_segment_attempts,
                report[
                    "segmentVerificationAttempts"
                ],
            ),
        )

        raw_chapter_audio = (
            chapter_directory /
            f"{args.chapter}.wav"
        )

        assembly_manifest = (
            chapter_directory /
            "assembly.json"
        )

        assembly_inputs = [
            chapter_manifest,
            tools_directory /
            "assemble_chapter_probe.py",
            *segment_audio_paths(
                chapter_manifest
            ),
        ]

        current_stage = "assembly"

        run_checkpointed_stage(
            "Assembly",
            "assembly",
            report,
            run_manifest_path,
            assembly_inputs,
            {
                "pauseSeconds": args.pause,
            },
            [
                assembly_manifest,
                raw_chapter_audio,
            ],
            lambda: run_stage(
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
            ),
        )

        assembly_verification_path = (
            chapter_directory /
            "assembly-verification.json"
        )

        current_stage = "assembly verification"

        run_checkpointed_stage(
            "Assembly verification",
            "assemblyVerification",
            report,
            run_manifest_path,
            [
                assembly_manifest,
                raw_chapter_audio,
                tools_directory /
                "verify_assembly_probe.py",
            ],
            {
                "whisperModel":
                    args.whisper_model,
            },
            [assembly_verification_path],
            lambda: run_stage(
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
            ),
        )

        mastered_audio = (
            chapter_directory /
            f"{args.chapter}.mastered.wav"
        )

        mastering_report_path = (
            chapter_directory /
            "mastering.json"
        )

        current_stage = "mastering"

        run_checkpointed_stage(
            "Mastering",
            "mastering",
            report,
            run_manifest_path,
            [
                assembly_manifest,
                raw_chapter_audio,
                tools_directory /
                "master_chapter_probe.py",
            ],
            {},
            [
                mastering_report_path,
                mastered_audio,
            ],
            lambda: run_stage(
                "Mastering",
                [
                    sys.executable,
                    str(
                        tools_directory /
                        "master_chapter_probe.py"
                    ),
                    str(assembly_manifest),
                ],
            ),
        )

        mastered_verification_path = (
            chapter_directory /
            "mastered-verification.json"
        )

        current_stage = "mastered verification"

        run_checkpointed_stage(
            "Mastered verification",
            "masteredVerification",
            report,
            run_manifest_path,
            [
                assembly_manifest,
                mastered_audio,
                tools_directory /
                "verify_assembly_probe.py",
            ],
            {
                "whisperModel":
                    args.whisper_model,
            },
            [mastered_verification_path],
            lambda: run_stage(
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
            ),
        )
        final_audio = (
            book /
            "output" /
            "final" /
            f"{args.chapter}.mp3"
        )

        export_report_path = (
            chapter_directory /
            "chapter-export.json"
        )

        current_stage = "MP3 export"

        run_checkpointed_stage(
            "MP3 export",
            "mp3Export",
            report,
            run_manifest_path,
            [
                book / "book.json",
                mastered_audio,
                mastered_verification_path,
                tools_directory /
                "export_chapter.py",
            ],
            {
                "bitrate": "128k",
            },
            [
                export_report_path,
                final_audio,
            ],
            lambda: run_stage(
                "MP3 export",
                [
                    sys.executable,
                    str(
                        tools_directory /
                        "export_chapter.py"
                    ),
                    str(book),
                    args.chapter,
                    str(mastered_audio),
                    "--bitrate",
                    "128k",
                ],
            ),
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
        report["finalAudioPath"] = (
            str(final_audio)
        )

        write_run_manifest(
            run_manifest_path,
            report,
        )

        print()
        print("===== Production complete =====")
        print(f"Run:     {run_directory}")
        print(f"Chapter: {args.chapter}")
        print(f"Master:  {mastered_audio}")
        print(f"MP3:     {final_audio}")

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
