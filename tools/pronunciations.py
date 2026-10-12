from __future__ import annotations

import hashlib
import json
import re
from pathlib import Path

from book_manifest import find_book_root

AUTOMATIC_HANDLING_VERSION = 2

def load_pronunciations(book: Path) -> list[dict]:
    """Load and validate generic pronunciation entries."""
    path = book / "pronunciations.json"

    automatic_path = book / "auto-pronunciations.json"
    if not path.is_file() and not automatic_path.is_file():
        return []

    document = json.loads(path.read_text(encoding="utf-8-sig")) if path.is_file() else {}
    entries = document.get("entries", [])
    manual_terms = {str(entry.get("canonical", "")).casefold() for entry in entries}
    if automatic_path.is_file():
        automatic = json.loads(automatic_path.read_text(encoding="utf-8-sig"))
        entries = entries + [entry for entry in automatic.get("entries", [])
                             if str(entry.get("canonical", "")).casefold() not in manual_terms]
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

        if entry.get("mode") in ("validated-word-split", "letter-spelling-fallback"):
            # Part of the synthesis/retry fingerprint: changed automatic
            # comparison or chat chunking must invalidate affected audio.
            entry["automaticHandlingVersion"] = AUTOMATIC_HANDLING_VERSION

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
        automatic = entry.get("mode") in ("validated-word-split", "letter-spelling-fallback")
        # Compare automatic usernames as spoken words, not as one compressed
        # token. Otherwise one ASR spelling error invalidates the whole label.
        replacement = str(entry["spokenText"]) if automatic else canonical
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
        if automatic and re.fullmatch(r"[A-Za-z0-9_]+", canonical):
            from chat_pronunciations import phonetic_spelling
            phonetic = phonetic_spelling(canonical)
            variants.add(phonetic)
            # ASR can spell the G codeword "gulf". This alternative
            # still decodes to G, and must match the entire encoded handle;
            # another codeword or a missing letter is never accepted.
            variants.add(phonetic.replace("Golf", "Gulf"))

        for variant in sorted(
            variants,
            key=len,
            reverse=True,
        ):
            if automatic:
                # Joining words or inserting punctuation does not change a
                # handle's letters/digits. Never accept changed characters.
                compact = re.sub(r"[^A-Za-z0-9]", "", variant)
                if compact:
                    pattern = r"(?<!\w)" + r"[\W_]*".join(re.escape(c) for c in compact) + r"(?!\w)"
                    result = re.sub(pattern, lambda match: replacement, result, flags=re.IGNORECASE)
            else:
                result = replace_term(result, variant, replacement)

        # A numeric handle may also be referred to by its exact text prefix
        # in prose (Noobkiller9000 -> Noobkiller). Expand that prefix without
        # adding the omitted numeric suffix or accepting a different name.
        suffix = re.fullmatch(r"([A-Za-z_]+)(\d+)", canonical) if automatic else None
        if suffix:
            from spoken_text import integer_to_words
            number = integer_to_words(int(suffix[2]))
            spoken = str(entry["spokenText"])
            if spoken.endswith(" " + number):
                result = replace_term(result, suffix[1], spoken[:-(len(number) + 1)])

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
