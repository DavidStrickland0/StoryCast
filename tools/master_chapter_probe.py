from __future__ import annotations

import argparse
import json
import re
import subprocess
from datetime import datetime, timezone
from pathlib import Path


TARGET_LUFS = -20.0
TARGET_TRUE_PEAK = -3.0
TARGET_LRA = 7.0


def load_json(path: Path) -> dict:
    with path.open(
        "r",
        encoding="utf-8-sig",
    ) as stream:
        return json.load(stream)


def run_ffmpeg(
    arguments: list[str],
) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(
        ["ffmpeg", *arguments],
        capture_output=True,
        text=True,
        check=False,
    )

    if result.returncode != 0:
        raise RuntimeError(
            f"FFmpeg failed with exit code "
            f"{result.returncode}:\n{result.stderr}"
        )

    return result


def extract_loudnorm_json(
    output: str,
) -> dict:
    matches = re.findall(
        r"\{\s*"
        r'"input_i".*?'
        r"\}",
        output,
        flags=re.DOTALL,
    )

    if not matches:
        raise RuntimeError(
            "FFmpeg did not return loudness measurements."
        )

    return json.loads(matches[-1])


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Master a verified StoryCast chapter."
    )
    parser.add_argument(
        "assembly_manifest",
        type=Path,
    )

    args = parser.parse_args()

    assembly_path = args.assembly_manifest.resolve()
    chapter_directory = assembly_path.parent

    assembly = load_json(assembly_path)

    verification_path = (
        chapter_directory /
        "assembly-verification.json"
    )

    if not verification_path.is_file():
        raise FileNotFoundError(
            f"Assembly verification was not found: "
            f"{verification_path}"
        )

    verification = load_json(
        verification_path
    )

    if verification["status"] != "pass":
        raise RuntimeError(
            "Only a verified chapter may be mastered."
        )

    input_path = Path(
        assembly["audioPath"]
    ).resolve()

    chapter_id = assembly["chapterId"]

    output_path = (
        chapter_directory /
        f"{chapter_id}.mastered.wav"
    )

    temporary_path = (
        chapter_directory /
        f".{chapter_id}.mastered.wav.tmp"
    )

    analysis_filter = (
        f"loudnorm="
        f"I={TARGET_LUFS}:"
        f"TP={TARGET_TRUE_PEAK}:"
        f"LRA={TARGET_LRA}:"
        f"print_format=json"
    )

    print("Measuring chapter loudness...", flush=True)

    first_pass = run_ffmpeg(
        [
            "-hide_banner",
            "-nostats",
            "-i",
            str(input_path),
            "-af",
            analysis_filter,
            "-f",
            "null",
            "-",
        ]
    )

    measured = extract_loudnorm_json(
        first_pass.stderr
    )

    mastering_filter = (
        f"loudnorm="
        f"I={TARGET_LUFS}:"
        f"TP={TARGET_TRUE_PEAK}:"
        f"LRA={TARGET_LRA}:"
        f"measured_I={measured['input_i']}:"
        f"measured_TP={measured['input_tp']}:"
        f"measured_LRA={measured['input_lra']}:"
        f"measured_thresh={measured['input_thresh']}:"
        f"offset={measured['target_offset']}:"
        f"linear=true:"
        f"print_format=json"
    )

    print("Applying two-pass loudness mastering...", flush=True)

    second_pass = run_ffmpeg(
        [
            "-hide_banner",
            "-nostats",
            "-y",
            "-i",
            str(input_path),
            "-af",
            mastering_filter,
            "-ar",
            str(assembly["sampleRate"]),
            "-ac",
            str(assembly["channels"]),
            "-c:a",
            "pcm_s16le",
            "-f",
            "wav",
            str(temporary_path),
        ]
    )

    output_measurements = extract_loudnorm_json(
        second_pass.stderr
    )

    temporary_path.replace(output_path)

    report = {
        "schemaVersion": 1,
        "generatedUtc": datetime.now(
            timezone.utc
        ).isoformat(),
        "chapterId": chapter_id,
        "inputPath": str(input_path),
        "outputPath": str(output_path),
        "targets": {
            "integratedLoudnessLufs": TARGET_LUFS,
            "truePeakDb": TARGET_TRUE_PEAK,
            "loudnessRangeLu": TARGET_LRA,
        },
        "inputMeasurements": measured,
        "outputMeasurements": output_measurements,
    }

    report_path = (
        chapter_directory /
        "mastering.json"
    )

    temporary_report_path = (
        chapter_directory /
        ".mastering.json.tmp"
    )

    with temporary_report_path.open(
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

    temporary_report_path.replace(
        report_path
    )

    print()
    print("Chapter mastering complete.")
    print(f"Input LUFS:  {measured['input_i']}")
    print(f"Input peak:  {measured['input_tp']} dBTP")
    print(f"Output LUFS: {output_measurements['output_i']}")
    print(f"Output peak: {output_measurements['output_tp']} dBTP")
    print(f"Audio:       {output_path}")
    print(f"Report:      {report_path}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
