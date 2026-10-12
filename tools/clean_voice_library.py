"""Back up, gently denoise, and loudness-normalize reference WAV files."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import shutil
import subprocess
from datetime import datetime, timezone


def run(args):
    result = subprocess.run(args, capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError(result.stderr[-3000:])
    return result


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('library', type=Path)
    args = parser.parse_args()
    library = args.library.resolve()
    manifests = sorted(library.glob('*/voice.json'))
    if not manifests:
        raise ValueError('No voice manifests found')
    backup = library.parent / ('voices-before-cleaning-' + datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S'))
    shutil.copytree(library, backup)
    print('Original library backed up:', backup, flush=True)
    records = []
    for index, manifest_path in enumerate(manifests, 1):
        manifest = json.loads(manifest_path.read_text(encoding='utf-8-sig'))
        sample = (manifest_path.parent / manifest['sample']).resolve()
        if not sample.is_relative_to(library):
            raise ValueError('Sample path is outside library')
        probe = json.loads(run(['ffprobe', '-v', 'error', '-show_streams', '-of', 'json', str(sample)]).stdout)['streams'][0]
        rate, channels = int(probe['sample_rate']), int(probe['channels'])
        reduction = 'afftdn=nr=8:nf=-40'
        measurement = run(['ffmpeg', '-hide_banner', '-nostdin', '-i', str(sample), '-af', reduction + ',loudnorm=I=-23:TP=-2:LRA=11:print_format=json', '-f', 'null', '-']).stderr
        stats = json.loads(measurement[measurement.rfind('{'):measurement.rfind('}')+1])
        for key in ('input_i', 'input_tp', 'input_lra', 'input_thresh', 'target_offset'):
            if not math.isfinite(float(stats[key])):
                raise ValueError('Cannot normalize silent/non-finite sample: ' + str(sample))
        normalization = ('loudnorm=I=-23:TP=-2:LRA=11:linear=true:print_format=json'
                         f":measured_I={stats['input_i']}:measured_TP={stats['input_tp']}"
                         f":measured_LRA={stats['input_lra']}:measured_thresh={stats['input_thresh']}"
                         f":offset={stats['target_offset']}")
        temporary = sample.with_name(sample.stem + '.cleaning.wav')
        original_hash = digest(sample)
        rendered = run(['ffmpeg', '-hide_banner', '-nostdin', '-y', '-i', str(sample), '-af', reduction + ',' + normalization,
                        '-ar', str(rate), '-ac', str(channels), '-c:a', 'pcm_s24le', str(temporary)])
        output_stats = json.loads(rendered.stderr[rendered.stderr.rfind('{'):rendered.stderr.rfind('}')+1])
        after = json.loads(run(['ffprobe', '-v', 'error', '-show_streams', '-of', 'json', str(temporary)]).stdout)['streams'][0]
        if abs(float(after['duration']) - float(probe['duration'])) > 0.05:
            raise ValueError('Unexpected duration change: ' + str(sample))
        temporary.replace(sample)
        record = dict(voiceId=manifest['id'], originalSha256=original_hash, cleanedSha256=digest(sample),
                      sampleRate=rate, channels=channels, inputLufs=float(stats['input_i']),
                      outputLufs=float(output_stats['output_i']), outputTruePeak=float(output_stats['output_tp']),
                      normalizationType=output_stats['normalization_type'])
        records.append(record)
        (library/'sample-cleaning-report.json').write_text(json.dumps(dict(backup=str(backup), filter=reduction, targetLufs=-23, voices=records), indent=2), encoding='utf-8')
        print(f"[{index}/{len(manifests)}] {manifest['id']}: {record['inputLufs']} -> {record['outputLufs']} LUFS", flush=True)
    print('Complete:', len(records), 'voices', flush=True)


if __name__ == '__main__':
    main()
