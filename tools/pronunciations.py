from __future__ import annotations

import hashlib
import json
import re
from pathlib import Path


def find_book_root(path: Path) -> Path:
    """Find the nearest ancestor containing book.json."""
    current = path.resolve()

    if current.is_file():
        current = current.parent

    for candidate in (current, *current.parents):
        if (candidate / "book.json").is_file():
            return candidate

    raise FileNotFoundError(
        f"Could not locate book.json above: {path}"
    )


def load_pronunciations(book: Path) -> list[dict]:
    """Load and validate generic pronunciation entries."""
    path = book / "pronunciations.json"

    if not path.is_file():
        return []

    with path.open("r", encoding="utf-8-sig") as stream:
        document = json.load(stream)

    entries = document.get("entries", [])
    canonical_terms: set[str] = set()

    for entry in entries:
        canonical = str(entry.get("canonical", "")).strip()
        spoken_text = str(entry.get("spokenText", "")).strip()

        if not canonical:
            raise ValueError(
                f"Pronunciation entry has no canonical term: {path}"
            )

        if not spoken_text:
            raise ValueError(
                f"Pronunciation entry '{canonical}' has no spokenText."
            )

        key = canonical.casefold()

        if key in canonical_terms:
            raise ValueError(
                f"Duplicate pronunciation term: {canonical}"
            )

        canonical_terms.add(key)

        accepted = entry.get("acceptedTranscriptions", [])

        if not isinstance(accepted, list):
            raise ValueError(
                f"acceptedTranscriptions for '{canonical}' must be an array."
            )

    return sorted(
        entries,
        key=lambda entry: len(str(entry["canonical"])),
        reverse=True,
    )


def replace_term(
    text: str,
    term: str,
    replacement: str,
) -> str:
    """Replace a complete term without changing surrounding text."""
    pattern = re.compile(
        rf"(?<!\w){re.escape(term)}(?!\w)",
        flags=re.IGNORECASE,
    )

    return pattern.sub(replacement, text)


def apply_pronunciations(
    text: str,
    entries: list[dict],
) -> str:
    """Convert configured terms into synthesis-friendly text."""
    result = text

    for entry in entries:
        result = replace_term(
            result,
            str(entry["canonical"]),
            str(entry["spokenText"]),
        )

    return result


def canonicalize_transcription(
    text: str,
    entries: list[dict],
) -> str:
    """Map accepted ASR variants back to their canonical terms."""
    result = text

    for entry in entries:
        canonical = str(entry["canonical"])
        variants = {
            canonical,
            str(entry["spokenText"]),
            *(
                str(value)
                for value in entry.get(
                    "acceptedTranscriptions",
                    [],
                )
            ),
        }

        for variant in sorted(
            variants,
            key=len,
            reverse=True,
        ):
            result = replace_term(
                result,
                variant,
                canonical,
            )

    return result


def pronunciation_fingerprint(
    text: str,
    entries: list[dict],
) -> str:
    """Hash only pronunciation entries applicable to the supplied text."""
    applicable = [
        entry
        for entry in entries
        if re.search(
            rf"(?<!\w){re.escape(str(entry['canonical']))}(?!\w)",
            text,
            flags=re.IGNORECASE,
        )
    ]

    serialized = json.dumps(
        applicable,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    )

    return hashlib.sha256(
        serialized.encode("utf-8")
    ).hexdigest()
