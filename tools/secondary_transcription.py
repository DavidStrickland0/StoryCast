"""Independent, unprompted ASR for clips the primary verifier rejects."""


class SecondaryTranscriber:
    def __init__(self, model_name="medium.en"):
        self.model_name = model_name
        self.model = None
        self.load_error = None

    def transcribe(self, audio_path):
        if self.load_error is not None:
            raise RuntimeError(self.load_error)
        if self.model is None:
            from faster_whisper import WhisperModel
            print(f"Loading independent {self.model_name} verifier on CPU...", flush=True)
            try:
                self.model = WhisperModel(self.model_name, device="cpu", compute_type="int8", cpu_threads=4)
            except Exception as error:
                self.load_error = str(error)
                raise
        segments, _ = self.model.transcribe(str(audio_path), language="en", beam_size=5,
            vad_filter=False, condition_on_previous_text=False, temperature=0.0,
            word_timestamps=True, hallucination_silence_threshold=1.0)
        return " ".join(segment.text.strip() for segment in segments).strip()
