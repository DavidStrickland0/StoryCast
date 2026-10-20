"""Discover reversible spoken forms for structured chat usernames."""
from __future__ import annotations

import json
import re
import urllib.request
from pathlib import Path

from spoken_text import normalize_spoken_text, integer_to_words, ordinal_to_words

CHAT_HANDLE = re.compile(r"\[([A-Za-z0-9_]+)\s*\([^\[\]\n:]+\)\s*:")


def spelling(handle: str) -> str:
    # Unknown joined words can always be read as letters without guessing a name.
    return " ".join("underscore" if c == "_" else c.upper() for c in handle)


def phonetic_spelling(handle: str) -> str:
    names = dict(zip("abcdefghijklmnopqrstuvwxyz", "Alpha Bravo Charlie Delta Echo Foxtrot Golf Hotel India Juliett Kilo Lima Mike November Oscar Papa Quebec Romeo Sierra Tango Uniform Victor Whiskey Xray Yankee Zulu".split()))
    return ", ".join(names[c.lower()] if c.isalpha() else
                     "underscore" if c == "_" else integer_to_words(int(c)) for c in handle)


def validate_split(handle: str, proposed: str) -> str:
    if not isinstance(proposed, str) or not proposed.strip():
        raise ValueError("Empty username pronunciation")
    if re.sub(r"[\s-]", "", proposed).casefold() != handle.replace("_", "").casefold():
        raise ValueError("Username pronunciation changed letters or digits")
    if not re.fullmatch(r"[A-Za-z0-9\s-]+", proposed):
        raise ValueError("Unsupported username pronunciation")
    # Split digits from letters before normalizing them as numbers, not years.
    parts = re.findall(r"\d+(?:st|nd|rd|th)|[A-Za-z]+|\d+", proposed)
    return " ".join(integer_to_words(int(p)) if p.isdigit() else
                    ordinal_to_words(int(re.match(r"\d+", p)[0])) if re.fullmatch(r"\d+(?:st|nd|rd|th)", p) else p
                    for p in parts)


def save_document(path: Path, document: dict) -> None:
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(document, indent=2, ensure_ascii=False), encoding="utf-8")
    temporary.replace(path)


def prepare_chat_pronunciations(book: Path, segments: list[dict], model: str, ollama_url: str) -> None:
    path = Path(book) / "auto-pronunciations.json"
    saved = json.loads(path.read_text(encoding="utf-8")) if path.exists() else {"schemaVersion": 1, "entries": []}
    refreshed = False
    # Short uppercase handles are initials, never NATO codewords.
    for entry in saved["entries"]:
        if re.fullmatch(r"[A-Z]{2,4}", entry.get("canonical", "")):
            spoken = spelling(entry["canonical"])
            if entry.get("spokenText") != spoken:
                entry.update(spokenText=spoken, acceptedTranscriptions=[spoken], mode="letter-spelling-fallback")
                refreshed = True
    for entry in saved["entries"]:
        if entry.get("mode") == "validated-word-split":
            split = entry["acceptedTranscriptions"][-1]
            spoken = validate_split(entry["canonical"], split)
            if spoken != entry["spokenText"]:
                entry.update(spokenText=spoken, acceptedTranscriptions=[normalize_spoken_text(spoken), split])
                refreshed = True
    configured = Path(book) / "pronunciations.json"
    manual = json.loads(configured.read_text(encoding="utf-8-sig")) if configured.exists() else {}
    known = {e["canonical"].casefold() for e in saved["entries"] + manual.get("entries", [])}
    handles = sorted({m[1] for s in segments for m in CHAT_HANDLE.finditer(s["sourceText"]) if m[1].casefold() not in known})
    if not handles:
        if refreshed:
            save_document(path, saved)
        return
    schema = {"type": "object", "properties": {h: {"type": "string"} for h in handles},
              "required": handles, "additionalProperties": False}
    request = urllib.request.Request(ollama_url.rstrip("/") + "/api/generate", data=json.dumps({
        "model": model, "stream": False, "think": False, "format": schema,
        "system": "Split chat usernames into readable words by inserting spaces only. Preserve every letter and digit in order. Replace underscores with spaces. Do not expand numbers, abbreviations, correct spelling, paraphrase or invent names. Example: Whostolemyduck becomes Who stole my duck; Noobkiller9000 becomes Noob killer 9000. Return an object mapping each supplied handle to its spaced spelling.",
        "prompt": json.dumps(handles), "options": {"temperature": 0, "num_ctx": 4096}
    }).encode(), headers={"Content-Type": "application/json"})
    result = {}
    try:
        with urllib.request.urlopen(request, timeout=120) as response:
            result = json.loads(json.loads(response.read())["response"])
        if not isinstance(result, dict):
            result = {}
    except (OSError, ValueError, KeyError, TypeError) as error:
        print(f"Chat pronunciation discovery using spelling fallback: {error}", flush=True)
    for handle in handles:
        mode = "validated-word-split"
        try:
            split = result.get(handle)
            spoken = spelling(handle) if re.fullmatch(r"[A-Z]{2,4}", handle) else validate_split(handle, split)
        except (ValueError, TypeError):
            spoken, mode, split = spelling(handle), "letter-spelling-fallback", None
        saved["entries"].append({"canonical": handle, "spokenText": spoken,
                                 "acceptedTranscriptions": [normalize_spoken_text(spoken)] + ([split] if split else []),
                                 "mode": mode, "model": model})
    save_document(path, saved)
    print(f"Prepared {len(handles)} chat username pronunciations: {path}", flush=True)
