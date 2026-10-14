# Contextual pronunciation

Chapter production automatically checks supported English heteronyms during audio
preparation, including when reusing a cached punctuation preparation. Words include
bow, lead, read, wind, wound, tear, bass, row, sow, does, live, close, use, minute,
and content, plus selected inflections. The catalogue is in
`tools/context_pronunciations.py`.

Each occurrence is classified independently from nearby prose and adjacent
segments. The text model selects a meaning from a fixed list and supplies an
exact supporting quotation covering that occurrence. It cannot invent replacement
dialogue or change arbitrary words. Invalid output receives up to three correction
attempts; uncertain or unresolved occurrences retain their spelling and are listed
in the report with a warning. Names and ambiguous/foreign usages should remain
uncertain. Entries in the existing book pronunciation dictionaries take precedence.

Reports are saved under `production/pronunciation-plans/<chapter-id>.json` with
original speech input, per-occurrence meanings, evidence, synthesis hints, and
unresolved occurrence IDs. These files are separate from the manuscript and the
ordinary punctuation preparation. Original manuscript files and production script
source text remain unchanged.

Chatterbox's current API accepts text rather than IPA or SSML. Pronunciation hints
therefore use a bounded set of English respellings **only in synthesis input**.
Chunk `sourceText` stays canonical; `synthesisText` records what the engine received.
Per-chunk pronunciation metadata lets transcription verification account for the
selected spelling without introducing a book-wide override for a homograph.
Changed pronunciation plans invalidate affected segment audio on resume; rewrites
trigger a fresh contextual plan before selective synthesis.

Neither contextual classification nor respelling guarantees correct acoustic
pronunciation. Ordinary ASR can transcribe different pronunciations identically.
Listening remains necessary when reviewing a disputed occurrence; the saved hints
and evidence identify exactly what was requested.

After updating this code, rebuild `storycast-worker:dev` before production:

```powershell
cd C:\repos\SRD\StoryCast
docker build -f Dockerfile.worker -t storycast-worker:dev .
```
