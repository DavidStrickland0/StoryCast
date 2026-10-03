from __future__ import annotations

import torch


SILENCE_THRESHOLD = 0.006
FRAME_SECONDS = 0.01
EDGE_PADDING_SECONDS = 0.03
FADE_SECONDS = 0.02
MAX_INTERNAL_SILENCE_SECONDS = 1.25
TARGET_INTERNAL_SILENCE_SECONDS = 0.45


def _activity_frames(
    audio: torch.Tensor,
    sample_rate: int,
    threshold: float = SILENCE_THRESHOLD,
) -> tuple[torch.Tensor, int]:
    mono_peak = audio.abs().amax(dim=0)
    frame_size = max(1, round(sample_rate * FRAME_SECONDS))
    padding = (-mono_peak.numel()) % frame_size

    if padding:
        mono_peak = torch.nn.functional.pad(mono_peak, (0, padding))

    frames = mono_peak.reshape(-1, frame_size).amax(dim=1)
    return frames > threshold, frame_size


def analyze_audio(
    audio: torch.Tensor,
    sample_rate: int,
) -> dict[str, float]:
    active, frame_size = _activity_frames(audio, sample_rate)
    frame_seconds = frame_size / sample_rate
    inactive = ~active
    runs: list[tuple[int, int]] = []
    start: int | None = None

    for index, silent in enumerate(inactive.tolist()):
        if silent and start is None:
            start = index
        elif not silent and start is not None:
            runs.append((start, index))
            start = None

    if start is not None:
        runs.append((start, len(inactive)))

    leading = runs[0][1] * frame_seconds if runs and runs[0][0] == 0 else 0.0
    trailing = (
        (runs[-1][1] - runs[-1][0]) * frame_seconds
        if runs and runs[-1][1] == len(inactive)
        else 0.0
    )
    maximum = max(
        ((end - begin) * frame_seconds for begin, end in runs),
        default=0.0,
    )
    return {
        "leadingSilenceSeconds": round(leading, 3),
        "trailingSilenceSeconds": round(trailing, 3),
        "maximumSilenceSeconds": round(maximum, 3),
    }


def polish_audio(
    audio: torch.Tensor,
    sample_rate: int,
) -> tuple[torch.Tensor, dict]:
    """Trim dead air, compress pathological pauses, and soften edges."""
    if audio.ndim == 1:
        audio = audio.unsqueeze(0)

    before = analyze_audio(audio, sample_rate)
    active, frame_size = _activity_frames(audio, sample_rate)
    active_indexes = torch.nonzero(active, as_tuple=False).flatten()

    if active_indexes.numel() == 0:
        return audio, {"before": before, "after": before}

    edge_padding = round(sample_rate * EDGE_PADDING_SECONDS)
    first_sample = max(0, int(active_indexes[0]) * frame_size - edge_padding)
    last_sample = min(
        audio.shape[-1],
        (int(active_indexes[-1]) + 1) * frame_size + edge_padding,
    )
    polished = audio[..., first_sample:last_sample]

    active, frame_size = _activity_frames(polished, sample_rate)
    maximum_frames = round(MAX_INTERNAL_SILENCE_SECONDS / FRAME_SECONDS)
    target_frames = round(TARGET_INTERNAL_SILENCE_SECONDS / FRAME_SECONDS)
    slices: list[torch.Tensor] = []
    cursor = 0
    run_start: int | None = None

    for index, is_active in enumerate(active.tolist()):
        if not is_active and run_start is None:
            run_start = index
        elif is_active and run_start is not None:
            run_frames = index - run_start
            if run_start > 0 and run_frames > maximum_frames:
                silence_start = run_start * frame_size
                silence_end = min(index * frame_size, polished.shape[-1])
                slices.append(polished[..., cursor:silence_start])
                keep_samples = min(
                    target_frames * frame_size,
                    silence_end - silence_start,
                )
                slices.append(
                    polished[..., silence_start:silence_start + keep_samples]
                )
                cursor = silence_end
            run_start = None

    slices.append(polished[..., cursor:])
    polished = torch.cat(slices, dim=-1)

    fade_samples = min(
        round(sample_rate * FADE_SECONDS),
        polished.shape[-1] // 2,
    )
    if fade_samples:
        fade = torch.linspace(
            0.0,
            1.0,
            fade_samples,
            dtype=polished.dtype,
            device=polished.device,
        )
        polished[..., :fade_samples] *= fade
        polished[..., -fade_samples:] *= fade.flip(0)

    return polished, {
        "version": 1,
        "before": before,
        "after": analyze_audio(polished, sample_rate),
    }
