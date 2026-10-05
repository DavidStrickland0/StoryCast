"""Runtime settings shared by CPU and CUDA transcription workers."""

import os


def whisper_options() -> dict[str, str]:
    """Keep CUDA defaults unless the local CPU profile is selected."""
    if os.environ.get("STORYCAST_DEVICE", "").lower() == "cpu":
        return {"device": "cpu", "compute_type": "int8"}
    return {"device": "cuda", "compute_type": "float16"}
