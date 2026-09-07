from __future__ import annotations

import argparse
import json
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
) -> None:
    print()
    print(f"===== {name} =====", flush=True)

    result = subprocess.run(
        arguments,
        check=False,
    )

    if result.returncode != 0:
        raise RuntimeError(
            f"{name} failed with exit code "
            f"{result.returncode}."
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


def main() -> int:
    args = parse_args()

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
        },
        "chapterDirectory":
            str(chapter_directory),
        "masteredAudioPath": None,
    }

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

        run_stage(
            "Segment verification",
            [
                sys.executable,
                str(
                    tools_directory /
                    "verify_chapter_probe.py"
                ),
                str(chapter_manifest),
                "--model",
                args.whisper_model,
            ],
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
