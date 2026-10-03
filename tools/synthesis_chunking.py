"""Choose stable text boundaries for individual speech synthesis calls."""

from __future__ import annotations

import re
import argparse


MAX_SYNTHESIS_CHARACTERS = 280


def split_synthesis_text(
    text: str,
    max_characters: int = MAX_SYNTHESIS_CHARACTERS,
) -> list[str]:
    if max_characters < 1:
        raise ValueError(
            "Maximum synthesis characters must be positive."
        )

    if len(text) <= max_characters:
        return [text.strip()]

    chunks: list[str] = []
    cursor = 0

    while cursor < len(text):
        remaining = text[cursor:]

        if len(remaining) <= max_characters:
            chunk = remaining
            cursor = len(text)
        else:
            window = text[cursor:cursor + max_characters]
            sentence_matches = list(
                re.finditer(
                    r'[.!?]["\u201d\u2019]?\s+',
                    window,
                )
            )

            if sentence_matches:
                # A short sentence ending is preferable to cutting the next
                # sentence merely to keep the chunk above half the limit.
                end = cursor + sentence_matches[-1].end()
            else:
                minimum_boundary = max_characters // 2
                clause_matches = list(
                    re.finditer(
                        r'[,;:\u2014]\s+',
                        window,
                    )
                )
                clause_end = next(
                    (
                        match.end()
                        for match in reversed(clause_matches)
                        if match.end() >= minimum_boundary
                    ),
                    None,
                )
                if clause_end is not None:
                    end = cursor + clause_end
                else:
                    whitespace = window.rfind(
                        " ",
                        minimum_boundary,
                    )
                    end = (
                        cursor + whitespace + 1
                        if whitespace >= minimum_boundary
                        else cursor + max_characters
                    )

            chunk = text[cursor:end]
            cursor = end

        normalized = chunk.strip()
        if normalized:
            chunks.append(normalized)

    return chunks


def ends_at_sentence_boundary(text: str) -> bool:
    """Report whether a chunk ends after sentence punctuation."""
    return re.search(r'[.!?]["\u201d\u2019]?$', text.rstrip()) is not None


def parse_chunk_selector(value: str) -> tuple[int, int]:
    try:
        segment_text, chunk_text = value.split(":", 1)
        segment_index = int(segment_text)
        chunk_index = int(chunk_text)
    except (ValueError, TypeError) as exception:
        raise argparse.ArgumentTypeError(
            "Chunk selectors must use <segment-index>:<chunk-index>."
        ) from exception
    if segment_index < 0 or chunk_index < 0:
        raise argparse.ArgumentTypeError(
            "Chunk selector indexes must be nonnegative."
        )
    return segment_index, chunk_index
