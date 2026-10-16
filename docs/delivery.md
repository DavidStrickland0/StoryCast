# Dialogue delivery

Dialogue `delivery` is a free-form acting direction, not an enum. Examples include
`whispering`, `yelling joyfully`, `screaming in fear`, `quietly`, `thoughtfully`,
`sarcastically`, and `barely containing excitement`. Directions belong in metadata;
they are never inserted into the words spoken by the character.

Audio preparation maps each distinct direction to bounded Chatterbox parameter
offsets using the configured Ollama model. It caches the result in
`production/delivery/<chapter>.json`. Finite numeric offsets outside supported
bounds are clamped; the original proposals and clamped fields are retained for
audit. Malformed plans, nonfinite values, and unsupported fields are retried,
then rejected. Saved plans are strictly validated before synthesis.
Planning cannot change the cast, reference speaker, dialogue, pitch, or accent.
Each segment retains its assigned `voiceId` and its character's baseline settings.

Chatterbox's exaggeration, guidance, and temperature controls only approximate
acting directions. They do not guarantee genuine whispering, screaming, laughter,
singing, or arbitrary vocal effects. This limitation is recorded in the synthesis
metadata. New directions do not require a code change or a new profile ID.

For stronger performance conditioning, an approved sample of the **same voice**
can be added to that voice's `voice.json` (alongside its base `sample.wav`):

```json
{
  "id": "maren-unique-voice",
  "sample": "sample.wav",
  "deliveryVariants": [
    {
      "direction": "whispering",
      "aliases": ["whispered", "hushed whisper"],
      "voiceId": "maren-unique-voice",
      "sample": "variants/whispering.wav",
      "sha256": "SHA256_OF_THE_APPROVED_WAV",
      "approved": true
    }
  ]
}
```

The direction and aliases can contain any description. Before approval, audition
the sample for speaker identity, performance, intelligibility, and artifacts.
The sample must remain inside that voice's directory, explicitly name the same
voice ID, and match its approved checksum. Other characters' voices are never
selected to obtain a style. Missing or unapproved styles use the character's base
reference with the parameter approximation explicitly recorded.

Delivery, resolved settings, and reference checksums participate in synthesis
reuse. Changes invalidate affected audio, including all chunks of a segment during
selective retry. Verification still checks the original spoken words using the
existing transcript policies, independent verifier, and retry limits. It does not
relax thresholds for expressive delivery. A changed waveform requires fresh
verification; an old passing transcript is reusable only for identical audio.
Transcript verification does not certify that a requested performance was achieved
or that speaker identity has no audible drift; those still require listening.

The worker image must be rebuilt after these tool changes:

```powershell
docker build -f Dockerfile.worker -t storycast-worker:dev .
```

Run that command from the StoryCast repository with Docker Desktop running.
Existing attribution artifacts retain their saved directions. Newly attributed
chapters use the expanded free-form delivery guidance.
