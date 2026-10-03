from __future__ import annotations

import argparse
import json
import re
import subprocess
import tempfile
from datetime import datetime, timezone
from pathlib import Path


VOLUME_PATTERN = re.compile(
    r"(?P<name>mean_volume|max_volume):\s*(?P<value>-?\d+(?:\.\d+)?)\s*dB"
)


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Analyze StoryCast voice samples with FFmpeg."
    )
    parser.add_argument("library", type=Path)
    parser.add_argument(
        "--output",
        type=Path,
        help="Report path. Defaults to <library>/voice-analysis.json.",
    )
    return parser.parse_args()


def run(command: list[str]) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(
        command,
        text=True,
        capture_output=True,
        check=False,
    )
    if result.returncode != 0:
        raise RuntimeError(
            f"{command[0]} failed with exit code {result.returncode}: "
            f"{result.stderr.strip()}"
        )
    return result


def analyze(manifest_path: Path) -> dict:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    voice_id = str(manifest.get("id", "")).strip()
    sample_name = str(manifest.get("sample", "")).strip()
    if not voice_id or not sample_name:
        raise RuntimeError(f"Invalid voice manifest: {manifest_path}")

    sample_path = (manifest_path.parent / sample_name).resolve()
    if sample_path.parent != manifest_path.parent.resolve():
        raise RuntimeError(f"Voice sample escapes its directory: {manifest_path}")
    if not sample_path.is_file():
        raise RuntimeError(f"Voice sample was not found: {sample_path}")

    probe_result = run(
        [
            "ffprobe",
            "-v",
            "error",
            "-select_streams",
            "a:0",
            "-show_entries",
            "stream=codec_name,sample_rate,channels,duration",
            "-show_entries",
            "format=duration",
            "-of",
            "json",
            str(sample_path),
        ]
    )
    probe = json.loads(probe_result.stdout)
    streams = probe.get("streams", [])
    if not streams:
        raise RuntimeError(f"Voice sample has no audio stream: {sample_path}")
    stream = streams[0]
    duration = stream.get("duration") or probe.get("format", {}).get("duration")

    volume_result = run(
        [
            "ffmpeg",
            "-hide_banner",
            "-nostats",
            "-i",
            str(sample_path),
            "-af",
            "volumedetect",
            "-f",
            "null",
            "/dev/null",
        ]
    )
    volumes = {
        match.group("name"): float(match.group("value"))
        for match in VOLUME_PATTERN.finditer(volume_result.stderr)
    }
    if "mean_volume" not in volumes or "max_volume" not in volumes:
        raise RuntimeError(f"FFmpeg did not report volume data: {sample_path}")

    return {
        "voiceId": voice_id,
        "durationSeconds": float(duration),
        "codecName": str(stream["codec_name"]),
        "sampleRate": int(stream["sample_rate"]),
        "channels": int(stream["channels"]),
        "meanVolumeDb": volumes["mean_volume"],
        "peakVolumeDb": volumes["max_volume"],
    }


def main() -> int:
    args = parse_arguments()
    library = args.library.resolve()
    output = (
        args.output.resolve()
        if args.output is not None
        else library / "voice-analysis.json"
    )
    manifests = sorted(library.glob("*/voice.json"))
    if not manifests:
        raise RuntimeError(f"The voice library contains no voices: {library}")

    print(f"Voice library: {library}")
    print(f"Voices:        {len(manifests)}")
    print("Analyzing samples...")
    print()

    analyses = []
    for index, manifest_path in enumerate(manifests, start=1):
        result = analyze(manifest_path)
        analyses.append(result)
        print(
            f"[{index:2}/{len(manifests)}] {result['voiceId']}  "
            f"{result['durationSeconds']:.2f}s  "
            f"{result['meanVolumeDb']:.1f} dB mean  "
            f"{result['peakVolumeDb']:.1f} dB peak"
        )

    report = {
        "schemaVersion": 1,
        "generatedUtc": datetime.now(timezone.utc).isoformat(),
        "voices": analyses,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(
        mode="w",
        encoding="utf-8",
        dir=output.parent,
        prefix=".voice-analysis-",
        suffix=".tmp",
        delete=False,
    ) as stream:
        json.dump(report, stream, indent=2)
        stream.write("\n")
        temporary_path = Path(stream.name)
    temporary_path.replace(output)

    clipping_risks = sum(item["peakVolumeDb"] >= 0 for item in analyses)
    print()
    print("Voice analysis complete.")
    print(f"Analyzed:      {len(analyses)}")
    print(f"Clipping risk: {clipping_risks}")
    print(f"Report:        {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
