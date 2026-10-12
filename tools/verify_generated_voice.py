"""Verify a generated reference and a fresh Chatterbox audition before import."""
import argparse
import gc
import hashlib
import json
from pathlib import Path
from analyze_voice_library import analyze

AUDITION_TEXT = (
    'The train arrived before sunrise, carrying a sealed letter and a small wooden box. '
    'I placed them on the kitchen table and opened the window. '
    'Please tell the captain that we will meet beside the old bridge tomorrow.'
)


def check_audio(analysis):
    return (5 <= analysis['durationSeconds'] <= 90 and analysis['peakVolumeDb'] < 0
            and analysis['meanVolumeDb'] > -45 and analysis['channels'] in (1, 2))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('candidate', type=Path)
    parser.add_argument('--model', default='small.en')
    args = parser.parse_args()
    root = args.candidate.resolve()
    manifest_path = root / 'voice.json'
    manifest = json.loads(manifest_path.read_text(encoding='utf-8-sig'))
    sample = (root / manifest['sample']).resolve()
    if not sample.is_relative_to(root):
        raise ValueError('Reference sample leaves the candidate folder.')
    analysis = analyze(manifest_path)
    report = {'schemaVersion': 1, 'voiceId': manifest['id'], 'analysis': analysis,
              'verifierSha256': hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              'sampleSha256': hashlib.sha256(sample.read_bytes()).hexdigest(),
              'manifestSha256': hashlib.sha256(manifest_path.read_bytes()).hexdigest(),
              'status': 'fail'}
    if check_audio(analysis):
        from faster_whisper import WhisperModel
        from verify_voice_library import transcribe, normalize_words, edit_distance, classify
        recognizer = WhisperModel(args.model, device='cpu', compute_type='int8')

        def verify(path, expected):
            heard, language, probability = transcribe(recognizer, path)
            words = normalize_words(expected)
            distance = edit_distance(words, normalize_words(heard))
            wer = distance / len(words) if words else 1
            return {'status': classify(wer), 'wordErrorRate': wer, 'transcription': heard,
                    'language': language, 'languageProbability': probability}

        report['reference'] = verify(sample, manifest['sampleText'])
        if report['reference']['status'] == 'pass':
            import torch
            import torchaudio
            from cache_tokenizer import configure_tokenizer_cache
            from chatterbox.mtl_tts import ChatterboxMultilingualTTS
            configure_tokenizer_cache()
            torch.manual_seed(1729)
            model = ChatterboxMultilingualTTS.from_pretrained(device='cuda', t3_model='v3')
            with torch.inference_mode():
                audio = model.generate(AUDITION_TEXT, language_id='en', audio_prompt_path=str(sample),
                                       exaggeration=0.4, cfg_weight=0.5, temperature=0.7)
            audition = root / 'audition.wav'
            torchaudio.save(str(audition), audio.detach().cpu().float(), model.sr)
            del model, audio
            gc.collect()
            torch.cuda.empty_cache()
            report['audition'] = verify(audition, AUDITION_TEXT)
            report['status'] = 'pass' if report['audition']['status'] == 'pass' else 'fail'
    target = root / 'generated-verification.json'
    temporary = target.with_suffix('.tmp')
    temporary.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    temporary.replace(target)
    print(f"Generated voice {manifest['id']}: {report['status']}", flush=True)
    return 0 if report['status'] == 'pass' else 2


if __name__ == '__main__':
    raise SystemExit(main())
