from __future__ import annotations

import re


DEFAULT_NARRATOR_EXAGGERATION = 0.4
DEFAULT_CHARACTER_EXAGGERATION = 0.65
DEFAULT_CFG_WEIGHT = 0.5
DEFAULT_TEMPERATURE = 0.7

LIMITS = {
    "exaggeration": (0.25, 0.85),
    "cfgWeight": (0.35, 0.65),
    "temperature": (0.40, 0.85),
}

PROFILE_ADJUSTMENTS = {
    "neutral": {
        "exaggeration": 0.0,
        "cfgWeight": 0.0,
        "temperature": 0.0,
    },
    "quiet-controlled": {
        "exaggeration": -0.10,
        "cfgWeight": 0.05,
        "temperature": -0.05,
    },
    "sad-weary": {
        "exaggeration": -0.05,
        "cfgWeight": 0.0,
        "temperature": -0.05,
    },
    "urgent-afraid": {
        "exaggeration": 0.10,
        "cfgWeight": 0.0,
        "temperature": 0.05,
    },
    "angry-shouting": {
        "exaggeration": 0.20,
        "cfgWeight": -0.05,
        "temperature": 0.05,
    },
    "whispering": {
        "exaggeration": -0.15,
        "cfgWeight": 0.05,
        "temperature": -0.10,
    },
}


def clamp(value: float, minimum: float, maximum: float) -> float:
    """Clamp one synthesis value to its supported range."""
    return max(minimum, min(maximum, value))


def contains_any(text: str, values: tuple[str, ...]) -> bool:
    """Return whether text contains any complete configured term."""
    return any(
        re.search(
            rf"\b{re.escape(value)}\b",
            text,
        )
        is not None
        for value in values
    )


def classify_delivery(
    source_text: str,
    delivery: str = "",
) -> str:
    """Classify prose and delivery instructions into a prosody profile."""
    combined = f"{delivery} {source_text}".casefold()

    if contains_any(
        combined,
        (
            "whisper",
            "whispered",
            "whispering",
            "murmur",
            "murmured",
            "under his breath",
            "under her breath",
            "hushed",
        ),
    ):
        return "whispering"

    if contains_any(
        combined,
        (
            "angry",
            "furious",
            "rage",
            "raged",
            "shout",
            "shouted",
            "shouting",
            "yell",
            "yelled",
            "scream",
            "screamed",
            "roar",
            "roared",
            "snarled",
        ),
    ):
        return "angry-shouting"

    if contains_any(
        combined,
        (
            "afraid",
            "fearful",
            "frightened",
            "terrified",
            "panicked",
            "urgent",
            "urgently",
            "desperate",
            "desperately",
            "gasped",
            "cried",
        ),
    ):
        return "urgent-afraid"

    if contains_any(
        combined,
        (
            "sad",
            "sadly",
            "grief",
            "grieving",
            "weary",
            "wearily",
            "tired",
            "sorrow",
            "broken",
            "defeated",
        ),
    ):
        return "sad-weary"

    if contains_any(
        combined,
        (
            "quiet",
            "quietly",
            "soft",
            "softly",
            "calm",
            "calmly",
            "controlled",
            "carefully",
            "flatly",
        ),
    ):
        return "quiet-controlled"

    stripped = source_text.strip()

    if stripped.endswith("!") or stripped.count("!") > 0:
        return "urgent-afraid"

    alphabetic = [
        character
        for character in stripped
        if character.isalpha()
    ]

    if (
        len(alphabetic) >= 4
        and all(character.isupper() for character in alphabetic)
    ):
        return "angry-shouting"

    return "neutral"


def resolve_segment_synthesis_settings(
    assignment: dict,
    speaker_id: str,
    source_text: str,
    delivery: str = "",
) -> dict[str, float | str]:
    """Resolve final synthesis settings from casting and segment prose."""
    configured = assignment.get("synthesis") or {}

    default_exaggeration = (
        DEFAULT_NARRATOR_EXAGGERATION
        if speaker_id.casefold() == "narrator"
        else DEFAULT_CHARACTER_EXAGGERATION
    )

    baseline = {
        "exaggeration": float(
            configured.get(
                "exaggeration",
                default_exaggeration,
            )
        ),
        "cfgWeight": float(
            configured.get(
                "cfgWeight",
                DEFAULT_CFG_WEIGHT,
            )
        ),
        "temperature": float(
            configured.get(
                "temperature",
                DEFAULT_TEMPERATURE,
            )
        ),
    }

    profile = classify_delivery(
        source_text,
        delivery,
    )

    adjustments = PROFILE_ADJUSTMENTS[profile]
    resolved: dict[str, float | str] = {
        "profile": profile,
    }

    for name, baseline_value in baseline.items():
        minimum, maximum = LIMITS[name]
        resolved[name] = round(
            clamp(
                baseline_value + adjustments[name],
                minimum,
                maximum,
            ),
            4,
        )

    return resolved
