from faster_whisper import WhisperModel

sample_path = "/mnt/c/Repos/StoryCast/voices/Female-001/sample.wav"

print("Loading small.en on CUDA...", flush=True)

model = WhisperModel(
    "small.en",
    device="cuda",
    compute_type="float16",
)

print(f"Transcribing: {sample_path}", flush=True)

segments, info = model.transcribe(
    sample_path,
    language="en",
    beam_size=5,
    vad_filter=True,
    condition_on_previous_text=False,
)

text = " ".join(
    segment.text.strip()
    for segment in segments
).strip()

print()
print(f"Detected language: {info.language}")
print(f"Language probability: {info.language_probability:.4f}")
print()
print("TRANSCRIPTION:")
print(text)
