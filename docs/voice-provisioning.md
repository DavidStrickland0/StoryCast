# Automatic attributed voices

Casting and chapter production check existing assignments against verified voice
metadata. A matching voice must have the required presentation and all requested
voice qualities. Narrators also require narration suitability. Missing metadata
does not count as a match. Voice IDs and filenames are not attribute evidence.

For each role that lacks a match, StoryCast first reuses an unused, verified
attributed voice. Otherwise it automatically builds the dedicated
`voiceforge-design:dev` image from VoiceForge's `Dockerfile.design` and requests a
new voice. No host Python installation or manual `--auto-voices` flag is required.
The emotion editor remains a separate image and is not needed by this process.

Generated candidates are kept under `production/voiceforge/candidates`. Before
library import, the reference must pass audio and speech checks and a fresh
Chatterbox audition must pass transcription verification. Up to three seeds are
tried for verification failures. Infrastructure errors are reported directly.
Verified files are hashed before import. Generated attribute metadata records
the voice design request; it is not an independent acoustic certification of
subjective traits such as gruffness. Listen when reviewing cast quality.

Old casting remains active until every required replacement is ready. A backup
is saved under `production/voiceforge/casting-before-*.json`, and replaced voices
are retained as retired assignments. Matching existing assignments stay in place.
Locked mismatches require unlocking; they are never silently replaced. Changed
voice IDs invalidate affected speech segments on audio resume.

Book defaults live in `production/storycast.json`:

```json
{
  "voiceLibraryPath": "C:\\Users\\User\\Documents\\StoryCast\\samples",
  "autoVoicesEnabled": true,
  "voiceForgeRoot": "C:\\repos\\SRD\\VoiceForge",
  "voiceForgeImage": "voiceforge-design:dev",
  "rewriteModel": "qwen3.5:9b",
  "audioPreparationModel": "qwen3.5:9b",
  "ambienceEnabled": true,
  "ambienceGainDb": -22
}
```

Audio preparation defaults to the selected rewrite model. `--library`,
`--voiceforge`, `--voiceforge-image`, `--rewrite-model`, and
`--audio-preparation-model` override the corresponding production defaults.
`--no-auto-voices` disables provisioning and reports missing matches rather than
substituting voices without the attributes.

To regenerate an already completed chapter, use `produce chapter` with its
`--resume-run` path and `--remix`. This checks casting first, regenerates affected
speech, and reuses compatible work. `produce run` ordinarily skips completed
chapters; it is not a request to regenerate them.
