from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

import torch
import torchaudio

from audio_postprocessing import polish_audio


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def save_audio(path: Path, audio: torch.Tensor, sample_rate: int) -> None:
    temporary = path.with_name(f".{path.name}.tmp")
    torchaudio.save(
        str(temporary),
        audio,
        sample_rate,
        format="wav",
    )
    temporary.replace(path)


def write_json(path: Path, value: dict) -> None:
    temporary = path.with_name(f".{path.name}.tmp")
    with temporary.open("w", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, indent=2, ensure_ascii=False)
        stream.write("\n")
    temporary.replace(path)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Polish persisted StoryCast chunks and rebuild segments."
    )
    parser.add_argument("chapter_manifest", type=Path)
    parser.add_argument(
        "--mark-run-for-resume-only",
        action="store_true",
    )
    args = parser.parse_args()
    manifest_path = args.chapter_manifest.resolve()

    with manifest_path.open("r", encoding="utf-8-sig") as stream:
        manifest = json.load(stream)

    run_manifest_path = manifest_path.parent.parent / "run.json"
    if args.mark_run_for_resume_only:
        with run_manifest_path.open("r", encoding="utf-8-sig") as stream:
            run_manifest = json.load(stream)
        run_manifest["status"] = "failed"
        run_manifest["failedStage"] = "audio polishing"
        run_manifest["completedUtc"] = None
        write_json(run_manifest_path, run_manifest)
        print(f"Marked for resume: {run_manifest_path}")
        return 0

    for segment in manifest["segments"]:
        assembled: list[torch.Tensor] = []
        sample_rate: int | None = None

        for position, chunk in enumerate(segment.get("chunks", [])):
            chunk_path = Path(chunk["audioPath"]).resolve()
            audio, current_rate = torchaudio.load(str(chunk_path))
            if sample_rate is None:
                sample_rate = current_rate
            elif current_rate != sample_rate:
                raise RuntimeError("Chunk sample rates do not match.")

            polished, processing = polish_audio(audio, current_rate)
            save_audio(chunk_path, polished, current_rate)
            chunk["durationSeconds"] = polished.shape[-1] / current_rate
            chunk["sha256"] = sha256_file(chunk_path)
            chunk["audioProcessing"] = processing
            assembled.append(polished)

            if position + 1 < len(segment["chunks"]):
                pause_frames = round(
                    current_rate * float(chunk["pauseAfterSeconds"])
                )
                assembled.append(
                    torch.zeros(
                        (*polished.shape[:-1], pause_frames),
                        dtype=polished.dtype,
                    )
                )

            print(
                f"segment {segment['index']} chunk {chunk['index']} | "
                f"max silence {processing['before']['maximumSilenceSeconds']:.2f}s "
                f"-> {processing['after']['maximumSilenceSeconds']:.2f}s",
                flush=True,
            )

        if not assembled or sample_rate is None:
            continue

        segment_audio = torch.cat(assembled, dim=-1)
        segment_path = Path(segment["audioPath"]).resolve()
        save_audio(segment_path, segment_audio, sample_rate)
        segment["durationSeconds"] = segment_audio.shape[-1] / sample_rate
        segment["sha256"] = sha256_file(segment_path)

    write_json(manifest_path, manifest)
    print(f"Updated manifest: {manifest_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
