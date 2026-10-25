"""Keep generated control/out-of-vocabulary tokens out of the audio decoder."""


def filter_speech_tokens(tokens, vocabulary_size):
    valid = tokens[(tokens >= 0) & (tokens < vocabulary_size)]
    removed = tokens.numel() - valid.numel()
    if removed:
        print(f"Chatterbox: removed {removed} non-audio speech tokens before decoding.", flush=True)
    if valid.numel() == 0:
        raise ValueError("Chatterbox generated no valid speech tokens.")
    return valid


def configure_speech_token_guard():
    import chatterbox.mtl_tts as multilingual
    from chatterbox.models.s3tokenizer import SPEECH_VOCAB_SIZE

    original = multilingual.drop_invalid_tokens
    if getattr(original, "storycast_guard", False):
        return

    def guarded(tokens):
        return filter_speech_tokens(original(tokens), SPEECH_VOCAB_SIZE)

    guarded.storycast_guard = True
    multilingual.drop_invalid_tokens = guarded
