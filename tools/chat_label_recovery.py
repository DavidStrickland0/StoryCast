"""Source-preserving speech recovery for ambiguous structured chat labels."""
import re
from pronunciations import canonicalize_transcription
from spoken_text import normalize_spoken_text
from verify_voice_library import normalize_words

LABEL = re.compile(r"^\[(?P<name>[^\[\]\n:]+?)\s*\((?P<location>[^\[\]\n:]+)\)\s*:\s*(?P<body>.*)\]$", re.DOTALL)


def recover_chat_labels(manifest, verification, pronunciations):
    recovered = []
    segments = {segment["index"]: segment for segment in manifest["segments"]}
    words = lambda text: normalize_words(normalize_spoken_text(canonicalize_transcription(text, pronunciations)))
    for rejected in verification.get("chunks", []):
        if rejected["status"] == "pass":
            continue
        segment = segments[rejected["segmentIndex"]]
        index = rejected["chunkIndex"]
        previous = segment.get("chunkSpeechOverrides", {}).get(str(index))
        if previous and previous.get("version", 1) >= 5:
            continue
        chunk = next((chunk for chunk in segment.get("chunks", []) if chunk["index"] == index), None)
        if chunk is None:
            continue
        match = LABEL.fullmatch(chunk["sourceText"].strip())
        if match is None:
            continue
        entry = next((entry for entry in pronunciations if entry.get("mode") == "validated-word-split"
                      and ((previous and entry["canonical"] == previous["canonical"])
                           or entry["spokenText"].casefold() == match["name"].strip().casefold())), None)
        if entry is None:
            continue
        location = words(match["location"])
        # This only schedules fresh synthesis, never approves a transcript.
        # Keep the complete original message even if it also needs recovery.
        candidates = [words(rejected.get(key, "")) for key in ("transcription", "secondaryTranscription")]
        # An existing fallback already records why this label needs recovery.
        # Upgrade its failed clip once even when its latest take lost the
        # location or message as well. The original message is still retained.
        matching = [([], 0)] if previous else []
        for actual in candidates:
            boundaries = [position for position in range(len(actual)) if actual[position:position + len(location)] == location]
            if len(boundaries) == 1:
                matching.append((actual, boundaries[0]))
        if not matching:
            continue
        actual, boundary = matching[0]
        if actual[:boundary] == words(match["name"]):
            continue
        original = previous["originalText"] if previous else chunk["sourceText"]
        original_match = LABEL.fullmatch(original.strip())
        prepared = original.replace(original_match["name"], entry["spokenText"] + ".", 1)
        segment.setdefault("chunkSpeechOverrides", {})[str(index)] = {
            "originalText": original, "sourceText": prepared, "canonical": entry["canonical"],
            "reason": "ambiguous-chat-label-boundary", "version": 5, "spokenName": entry["spokenText"]}
        recovered.append((segment["index"], index))
    return recovered


def apply_chunk_speech_overrides(units, segment, *, discard_stale=False):
    result = list(units)
    overrides = (segment or {}).get("chunkSpeechOverrides", {})
    for key, override in list(overrides.items()):
        index = int(key)
        original = LABEL.fullmatch(override["originalText"].strip())
        prepared = LABEL.fullmatch(override["sourceText"].strip())
        if original is None or prepared is None or original["location"] != prepared["location"] or original["body"] != prepared["body"]:
            raise ValueError("Chat speech override changed message content.")
        version = override.get("version", 1)
        if version < 5:
            # Retire old letter/NATO fallbacks while preserving the message.
            if 0 <= index < len(result) and result[index][0] == override["sourceText"]:
                result[index] = (override["originalText"], result[index][1])
            del overrides[key]
            continue
        expected = override["spokenName"] + "."
        if version >= 5 and override["spokenName"].casefold() != original["name"].strip().casefold():
            raise ValueError("Chat speech override changed the spoken username.")
        if prepared["name"] != expected:
            raise ValueError("Chat speech override does not preserve literal handle spelling.")
        if index < 0 or index >= len(result) or result[index][0] not in (override["originalText"], override["sourceText"]):
            if not discard_stale:
                raise ValueError("Chat speech override no longer matches its original chunk.")
            # Chunk boundaries or speech preparation changed since this repair.
            # Synthesize the current text normally and verify the new audio;
            # never attach a stale repair to a different message.
            del overrides[key]
            print(f"Discarding stale chat speech repair for chunk {index}; using current speech text.", flush=True)
            continue
        result[index] = (override["sourceText"], result[index][1])
    return result
