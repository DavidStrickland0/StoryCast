from __future__ import annotations

from pathlib import Path


def resolve_book_manifest(book: Path) -> Path:
    """Return the manifest used by a book's root or shared production layout."""
    for manifest in (book / "book.json", book / "production" / "book.json"):
        if manifest.is_file():
            return manifest

    raise FileNotFoundError(f"Book manifest was not found in: {book}")


def find_book_root(path: Path) -> Path:
    """Find the nearest ancestor with a supported book manifest."""
    current = path.resolve()
    if current.is_file():
        current = current.parent

    for candidate in (current, *current.parents):
        if (candidate / "book.json").is_file() or (
            candidate / "production" / "book.json"
        ).is_file():
            return candidate

    raise FileNotFoundError(f"Could not locate book.json above: {path}")
