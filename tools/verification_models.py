"""Chapter-scoped speech verifier reuse across selective retry passes."""
import gc


class VerificationModels:
    def __init__(self):
        self.primaries = {}
        self.secondaries = {}

    def primary(self, name, options):
        key = (name, tuple(sorted(options.items())))
        if key not in self.primaries:
            from faster_whisper import WhisperModel
            print(f"Loading {name} on {options['device']}...", flush=True)
            self.primaries[key] = WhisperModel(name, **options)
        else:
            print(f"Reusing {name} verifier on {options['device']}...", flush=True)
        return self.primaries[key]

    def secondary(self, name):
        if name == 'none':
            return None
        if name not in self.secondaries:
            from secondary_transcription import SecondaryTranscriber
            self.secondaries[name] = SecondaryTranscriber(name)
        return self.secondaries[name]

    def close(self):
        self.primaries.clear()
        self.secondaries.clear()
        gc.collect()
