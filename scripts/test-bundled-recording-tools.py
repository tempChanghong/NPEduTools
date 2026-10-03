"""Verify recording media processing with synthetic input, never screen or microphone capture."""
import argparse
import json
import math
import pathlib
import struct
import subprocess
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument('--runtime', default='.tools/recording-bundled')
parser.add_argument('--report', default='.artifacts/ffmpeg-bundling/media-result.json')
args = parser.parse_args()
runtime = pathlib.Path(args.runtime).resolve()
report_path = pathlib.Path(args.report).resolve()
report_path.parent.mkdir(parents=True, exist_ok=True)
flags = getattr(subprocess, 'CREATE_NO_WINDOW', 0)


def run(tool, arguments):
    result = subprocess.run([str(runtime / (tool + '.exe')), *arguments],
                            capture_output=True, text=True, encoding='utf-8',
                            creationflags=flags, timeout=90)
    if result.returncode:
        raise RuntimeError(result.stderr[-4000:])
    return result.stdout + (result.stderr if tool == 'ffmpeg' else '')


checks = []
version = run('ffmpeg', ['-version'])
assert '--enable-libx264' in version and '--enable-nonfree' not in version
assert '--disable-network' in version
checks.append('version and redistribution profile')
devices = run('ffmpeg', ['-hide_banner', '-devices'])
assert 'gdigrab' in devices and 'lavfi' in devices
checks.append('GDI input compiled; no capture started')

with tempfile.TemporaryDirectory(prefix='NPEduTools-synthetic-media-') as scratch:
    work = pathlib.Path(scratch)
    pcm = work / 'input.f32'
    with pcm.open('wb') as output:
        for index in range(48000 * 2):
            sample = .1 * math.sin(2 * math.pi * 440 * index / 48000)
            output.write(struct.pack('<ff', sample, sample))
    parts = []
    for index in range(2):
        part = work / f'part-{index + 1:04}.mkv'
        run('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-n',
                      '-f', 'lavfi', '-i', 'testsrc2=size=1600x900:rate=8',
                      '-f', 'f32le', '-ar', '48000', '-ac', '2', '-i', str(pcm),
                      '-map', '0:v:0', '-map', '1:a:0', '-t', '2',
                      '-vf', 'scale=1280:720:flags=fast_bilinear,setsar=1',
                      '-c:v', 'libx264', '-preset', 'ultrafast', '-tune', 'zerolatency',
                      '-crf', '23', '-pix_fmt', 'yuv420p', '-threads', '2', '-g', '40',
                      '-r', '8', '-fps_mode', 'cfr', '-c:a', 'aac', '-b:a', '128k',
                      '-ar', '48000', '-ac', '2', '-flush_packets', '1',
                      '-cluster_time_limit', '1000', '-f', 'matroska', str(part)])
        parts.append(part)
    checks.append('two scaled H.264/AAC segments from raw float PCM')
    listing = work / 'parts.txt'
    listing.write_text("\n".join(f"file '{part.name}'" for part in parts), encoding='utf-8')
    final = work / 'joined.mp4'
    run('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-n', '-f', 'concat',
                  '-safe', '1', '-i', str(listing), '-c', 'copy', '-movflags', '+faststart', str(final)])
    media = json.loads(run('ffprobe', ['-v', 'error', '-show_format', '-show_streams', '-of', 'json', str(final)]))
    video = next(stream for stream in media['streams'] if stream['codec_type'] == 'video')
    audio = next(stream for stream in media['streams'] if stream['codec_type'] == 'audio')
    assert (video['codec_name'], video['width'], video['height'], video['pix_fmt']) == ('h264', 1280, 720, 'yuv420p')
    assert (audio['codec_name'], audio['sample_rate'], audio['channels']) == ('aac', '48000', 2)
    assert 3.8 < float(media['format']['duration']) < 4.5
    assert abs(float(video['duration']) - float(audio['duration'])) < .5
    checks.append('concat, MP4 faststart, probe and AV duration alignment')
    run('ffmpeg', ['-v', 'error', '-xerror', '-i', str(final), '-f', 'null', '-'])
    checks.append('full final-file decode')
    silent = work / 'silent.mp4'
    run('ffmpeg', ['-v', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=640x360:rate=8',
                  '-t', '1', '-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p', str(silent)])
    silent_media = json.loads(run('ffprobe', ['-v', 'error', '-show_streams', '-of', 'json', str(silent)]))
    assert len(silent_media['streams']) == 1 and silent_media['streams'][0]['codec_name'] == 'h264'
    checks.append('video-only recording')

report = {'passed': True, 'checks': checks, 'runtime': str(runtime), 'version': version.splitlines()[0],
          'capture': 'synthetic only; no desktop, microphone, or system audio'}
report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(f'PASS: {len(checks)} synthetic recording checks; {report_path}')
