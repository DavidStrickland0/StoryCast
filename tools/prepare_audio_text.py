"""Conservative, audited AI preparation of a chapter's spoken text."""
from __future__ import annotations

import hashlib
import json
import re
import urllib.request
from difflib import SequenceMatcher
from pathlib import Path

from spoken_text import normalize_spoken_text
from spoken_text import has_spoken_content

VERSION = 4


def chapter_vocabulary(segments):
    candidates = {}
    for segment in segments:
        for word in re.findall(r"\b[A-Za-z]+\b", segment["sourceText"]):
            if re.search(r"([A-Za-z])\1{2,}", word):
                continue
            key = re.sub(r"(.)\1+", r"\1", word.casefold())
            candidates.setdefault(key, {})[word.casefold()] = word
    return {key: next(iter(words.values())) for key, words in candidates.items() if len(words) == 1}


def safe_text(text, vocabulary=None):
    # Only normalize obvious typographic elongation, leaving ordinary double
    # letters and acronyms alone. Numeric expansion already used by synthesis.
    def unstretch(match):
        word = match[0]
        if not re.search(r"([A-Za-z])\1{2,}", word):
            return word
        key = re.sub(r"(.)\1+", r"\1", word.casefold())
        # Preserve ambiguous spellings rather than inventing a name.
        return (vocabulary or {}).get(key, word)
    text = re.sub(r"\b[A-Za-z]+\b", unstretch, text)
    return normalize_spoken_text(text)


def tokens(text, vocabulary=None):
    return re.findall(r"[a-z0-9]+(?:'[a-z0-9]+)?", safe_text(text, vocabulary).casefold())


def validate_replacement(source, replacement, vocabulary=None):
    if not has_spoken_content(source) and has_spoken_content(replacement):
        raise ValueError("Cannot add speech to an empty source segment.")
    if not isinstance(replacement, str) or not replacement.strip():
        raise ValueError("Empty spoken text.")
    if tokens(source, vocabulary) != tokens(replacement, vocabulary):
        raise ValueError("Changed or omitted spoken words, names, or numbers.")
    return replacement


def preparation_path(book, chapter):
    return Path(book) / "production" / "audio-preparation" / f"{chapter}.json"


def script_fingerprint(artifact):
    return hashlib.sha256(json.dumps(artifact, sort_keys=True, ensure_ascii=False).encode()).hexdigest()


def load_prepared(book, chapter, artifact):
    path = preparation_path(book, chapter)
    if not path.exists():
        return {}
    report = json.loads(path.read_text(encoding="utf-8"))
    if report.get("version") != VERSION or report.get("scriptSha256") != script_fingerprint(artifact):
        raise ValueError("Audio preparation does not match the current production script.")
    originals = {s["index"]: s["sourceText"] for s in artifact["script"]["segments"]}
    vocabulary = chapter_vocabulary(artifact["script"]["segments"])
    replacements = {}
    for segment in report["segments"]:
        index = segment["index"]
        if index in replacements or index not in originals or segment["sourceText"] != originals[index]:
            raise ValueError("Audio preparation segment identity mismatch.")
        replacements[index] = validate_replacement(originals[index], segment["spokenText"], vocabulary)
    if replacements.keys() != originals.keys():
        raise ValueError("Audio preparation omitted segments.")
    return replacements


def prepare_chapter(book, chapter, model, ollama_url):
    artifact = json.loads((Path(book) / "production" / "scripts" / f"{chapter}.json").read_text(encoding="utf-8-sig"))
    from chat_pronunciations import prepare_chat_pronunciations
    prepare_chat_pronunciations(Path(book), artifact["script"]["segments"], model, ollama_url)
    path = preparation_path(book, chapter)
    fingerprint = script_fingerprint(artifact)
    if path.exists():
        saved = json.loads(path.read_text(encoding="utf-8"))
        if saved.get("version") == VERSION and saved.get("scriptSha256") == fingerprint and saved.get("model") == model:
            load_prepared(book, chapter, artifact)
            from context_pronunciations import prepare_context_pronunciations
            prepare_context_pronunciations(book, chapter, saved["segments"], model, ollama_url)
            print(f"Using existing audio preparation: {path}", flush=True)
            return path
    segments = artifact["script"]["segments"]
    vocabulary = chapter_vocabulary(segments)
    prepared, batches, batch = [], [], []
    for segment in segments:
        if batch and sum(len(s["sourceText"]) for s in batch) + len(segment["sourceText"]) > 6000:
            batches.append(batch)
            batch = []
        batch.append(segment)
    if batch:
        batches.append(batch)
    for number, batch in enumerate(batches, 1):
        baseline = [{"index": s["index"], "speakerId": s["speakerId"], "spokenText": safe_text(s["sourceText"], vocabulary)} for s in batch]
        errors, accepted = [], {}
        originals = {s["index"]: s["sourceText"] for s in batch}
        for attempt in range(3):
            pending = {index for index in originals if index not in accepted}
            keys = [str(index) for index in sorted(pending)]
            schema = {"type": "object", "properties": {"segments": {"type": "object",
                "properties": {key: {"type": "string"} for key in keys}, "required": keys,
                "additionalProperties": False}}, "required": ["segments"], "additionalProperties": False}
            prompt = json.dumps({"segments": [s for s in baseline if s["index"] in pending], "previousValidationErrors": errors}, ensure_ascii=False)
            request = urllib.request.Request(ollama_url.rstrip("/") + "/api/generate", data=json.dumps({
                "model": model, "stream": False, "think": False, "format": schema,
                "system": "Prepare these audiobook segments for speech. Adjust punctuation and typographic shouting only. Preserve every spoken word in order, including profanity, names, numbers and fragments. Do not expand fragments, abbreviations or acronyms, paraphrase, add exposition, censor or remove anything. Return a segments object mapping each requested index to its spoken text string. Keep each speaker's text separate.",
                "prompt": prompt, "options": {"temperature": 0.1, "num_ctx": 16384, "num_predict": max(2048, len(prompt))}
            }).encode(), headers={"Content-Type": "application/json"})
            print(f"Audio preparation batch {number}/{len(batches)}, attempt {attempt + 1}", flush=True)
            try:
                with urllib.request.urlopen(request, timeout=300) as response:
                    result = json.loads(json.loads(response.read())["response"])["segments"]
                if not isinstance(result, dict):
                    raise ValueError("Expected a segments object mapping indexes to spoken text.")
                if set(result) != set(keys):
                    missing = sorted(set(keys) - set(result), key=int)
                    unknown = sorted(set(result) - set(keys))
                    raise ValueError(f"Segment index mismatch. Missing: {missing}; unknown: {unknown}.")
                failures = []
                for key, replacement in result.items():
                    index = int(key)
                    try:
                        accepted[index] = validate_replacement(originals[index], replacement, vocabulary)
                    except (ValueError, TypeError) as error:
                        detail = (
                            f"Segment {index}: {error}\n"
                            f"  Original text: {json.dumps(originals[index], ensure_ascii=False)}\n"
                            f"  Proposed text: {json.dumps(replacement, ensure_ascii=False)}"
                        )
                        if isinstance(replacement, str):
                            original_words = tokens(originals[index], vocabulary)
                            proposed_words = tokens(replacement, vocabulary)
                            for kind, old_start, old_end, new_start, new_end in SequenceMatcher(
                                    None, original_words, proposed_words, autojunk=False).get_opcodes():
                                if kind == "equal":
                                    continue
                                old = " ".join(original_words[old_start:old_end])
                                new = " ".join(proposed_words[new_start:new_end])
                                detail += f"\n  {kind.capitalize()} spoken words: {json.dumps(old, ensure_ascii=False)} -> {json.dumps(new, ensure_ascii=False)}"
                        errors.append(detail)
                        failures.append(index)
                        print(f"Audio preparation validation failed:\n{detail}", flush=True)
                if accepted.keys() != originals.keys():
                    raise ValueError(f"Text validation failed for segments {sorted(failures)}; retrying only these segments.")
                break
            except (OSError, ValueError, KeyError, TypeError) as error:
                errors.append(str(error))
                print(f"Audio preparation correction needed: {error}", flush=True)
        for segment in batch:
            source = segment["sourceText"]
            spoken = accepted.get(segment["index"], safe_text(source, vocabulary))
            prepared.append({"index": segment["index"], "speakerId": segment["speakerId"], "sourceText": source,
                             "spokenText": spoken, "changed": spoken != source,
                             "mode": "ai-validated" if segment["index"] in accepted else "deterministic-fallback",
                             "validationErrors": errors})
    report = {"version": VERSION, "chapterId": chapter, "model": model, "scriptSha256": fingerprint,
              "segments": prepared, "changedSegments": sum(s["changed"] for s in prepared),
              "fallbackSegments": [s["index"] for s in prepared if s["mode"] == "deterministic-fallback"]}
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    temporary.replace(path)
    load_prepared(book, chapter, artifact)
    from context_pronunciations import prepare_context_pronunciations
    prepare_context_pronunciations(book, chapter, prepared, model, ollama_url)
    print(f"Audio preparation saved: {path} ({report['changedSegments']} changed segments)", flush=True)
    return path


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("book", type=Path)
    parser.add_argument("chapter")
    parser.add_argument("--model", default="qwen3.5:4b")
    parser.add_argument("--ollama-url", default="http://localhost:11434/")
    args = parser.parse_args()
    prepare_chapter(args.book, args.chapter, args.model, args.ollama_url)
