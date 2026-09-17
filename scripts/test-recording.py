"""Record only an owned test window; no network calls or desktop capture.

Uses the built Windows recorder and ffprobe. System audio includes a quiet test
tone. Microphone smoke capture is local, short, and stored under .artifacts.
"""
import argparse
import json
import pathlib
import queue
import re
import ctypes
import subprocess
import threading
import time
import uuid

ROOT = pathlib.Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser()
parser.add_argument('--configuration', default='Release')
parser.add_argument('--duration', type=int, default=30)
args = parser.parse_args()
RUN = ROOT / '.artifacts' / 'recording' / uuid.uuid4().hex
RUN.mkdir(parents=True)
BIN = ROOT / 'src/NPEduTools.Recorder/bin' / args.configuration / 'net10.0-windows'
EXE = BIN / 'NPEduTools.Recorder.exe'
FIXTURE = ROOT / 'tests/NPEduTools.Recording.TestFixture/bin' / args.configuration / 'net10.0-windows/NPEduTools.Recording.TestFixture.exe'
TITLE = 'NPEduTools recording test ' + RUN.name
FLAGS = subprocess.CREATE_NO_WINDOW
report = {'passed': False, 'checks': [], 'files': []}


def tool(name, arguments):
    return subprocess.run([str(BIN / 'Tools' / (name + '.exe')), *arguments], capture_output=True, text=True,
                          encoding='utf-8', creationflags=FLAGS, timeout=45, check=True)


class Worker:
    def __init__(self, label):
        self.events = queue.Queue()
        self.process = subprocess.Popen([str(EXE), '--fixture-window', TITLE], stdin=subprocess.PIPE,
                                        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True,
                                        encoding='utf-8', creationflags=FLAGS)
        self.log = (RUN / (label + '.jsonl')).open('w', encoding='utf-8')
        def read():
            for line in self.process.stdout:
                self.log.write(line); self.log.flush()
                self.events.put(json.loads(line))
            self.log.close()
        self.thread = threading.Thread(target=read, daemon=True)
        self.thread.start()

    def send(self, action, **extra):
        self.process.stdin.write(json.dumps({'action': action, **extra}) + '\n')
        self.process.stdin.flush()

    def wait(self, phase, timeout=35):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            state = self.events.get(timeout=max(.1, deadline - time.monotonic()))
            if state['phase'] == phase:
                return state
            if state['phase'] == 'Failed':
                raise RuntimeError(state.get('error') or state['message'])
        raise TimeoutError(phase)

    def close(self):
        if self.process.poll() is None:
            self.process.stdin.close()  # same path as owner loss
            try:
                self.process.wait(timeout=45)
            except subprocess.TimeoutExpired:
                self.process.kill(); self.process.wait(timeout=10)
        self.thread.join(timeout=5)


def verify(state, audio):
    path = state['outputFile']
    media = json.loads(tool('ffprobe', ['-v', 'error', '-show_format', '-show_streams', '-of', 'json', path]).stdout)
    video = next(s for s in media['streams'] if s['codec_type'] == 'video')
    assert video['codec_name'] == 'h264' and video['height'] <= 720 and video['width'] <= 1280
    assert video['width'] % 2 == video['height'] % 2 == 0
    sound = [s for s in media['streams'] if s['codec_type'] == 'audio']
    assert bool(sound) == audio
    if audio:
        assert sound[0]['codec_name'] == 'aac' and sound[0]['sample_rate'] == '48000'
        assert abs(float(video['duration']) - float(sound[0]['duration'])) < .5
    # Decode the whole file, including the pause/resume splice, not just its header.
    tool('ffmpeg', ['-v', 'error', '-xerror', '-i', path, '-f', 'null', '-'])
    (RUN / (pathlib.Path(path).stem + '.probe.json')).write_text(json.dumps(media, indent=2), encoding='utf-8')
    report['files'].append({'path': path, 'duration': float(media['format']['duration']),
                            'width': video['width'], 'height': video['height'], 'audio': audio,
                            'overruns': state['audioOverruns'], 'dropped': state['droppedFrames']})
    return media


fixture = subprocess.Popen([str(FIXTURE), 'title=' + TITLE, '--tone'])
worker = None
print('Recording artifacts:', RUN, flush=True)
try:
    time.sleep(1)
    environment = json.loads(subprocess.check_output([str(EXE), '--probe'], creationflags=FLAGS))
    assert environment['ready']
    base = {'display': environment['displays'][0]['id'], 'outputDirectory': str(RUN),
            'framesPerSecond': 8, 'maximumHeight': 720, 'systemAudio': False, 'microphone': False}
    worker = Worker('pause-resume')
    worker.send('start', options=base)
    worker.wait('Recording'); time.sleep(4)
    worker.send('pause'); worker.wait('Paused'); time.sleep(3)
    worker.send('resume'); worker.wait('Recording'); time.sleep(4)
    worker.send('stop'); saved = worker.wait('Saved')
    media = verify(saved, False)
    assert 8 <= float(media['format']['duration']) <= 13, 'Pause time leaked into output'
    tool('ffmpeg', ['-v', 'error', '-i', saved['outputFile'], '-frames:v', '1', str(RUN / 'frame.png')])
    worker.close(); worker = None
    report['checks'].append('GDI window capture, 8 fps, pause/resume, H.264 MP4 and complete decode')
    print('PASS: video + pause/resume', flush=True)

    if environment['speakers']:
        worker = Worker('system-audio')
        worker.send('start', options={**base, 'systemAudio': True})
        worker.wait('Recording')
        until = time.monotonic() + args.duration
        while time.monotonic() < until:
            state = worker.events.get(timeout=10)
            if state['phase'] == 'Failed': raise RuntimeError(state['error'])
        worker.send('pause'); worker.wait('Paused'); time.sleep(1)
        worker.send('resume'); worker.wait('Recording'); time.sleep(3)
        worker.send('stop'); saved = worker.wait('Saved')
        verify(saved, True)
        volume = tool('ffmpeg', ['-hide_banner', '-i', saved['outputFile'], '-vn', '-af', 'volumedetect', '-f', 'null', '-']).stderr
        (RUN / 'system-audio-volume.txt').write_text(volume, encoding='utf-8')
        mean = re.search(r'mean_volume: (-?[\d.]+) dB', volume)
        assert mean and float(mean[1]) > -90, 'Expected audible test tone in loopback capture'
        worker.close(); worker = None
        report['checks'].append(f'WASAPI loopback + AAC + video, {args.duration}s sustained capture plus pause/resume, complete decode')
        print('PASS: system audio', flush=True)

    if environment['microphones']:
        worker = Worker('microphone-and-owner-exit')
        worker.send('start', options={**base, 'systemAudio': bool(environment['speakers']), 'microphone': True, 'framesPerSecond': 15})
        worker.wait('Recording'); time.sleep(3)
        worker.process.stdin.close()
        saved = worker.wait('Saved', timeout=45)
        verify(saved, True)
        worker.process.wait(timeout=10); worker.close(); worker = None
        report['checks'].append('Microphone (+ system audio), 15 fps, owner EOF finalizes MP4')
        print('PASS: microphone + owner EOF', flush=True)

    worker = Worker('invalid-display')
    worker.send('start', options={**base, 'display': 'missing-test-display'})
    state = worker.wait('Failed')
    assert state['outputFile'] is None and state['recoveryDirectory'] is None
    worker.close(); worker = None
    report['checks'].append('Missing display rejects before recording and never claims a saved file')

    # Kill only this test's worker. Its Windows Job must reap its encoder; the
    # flushed MKV clusters must remain recoverable without changing the originals.
    worker = Worker('worker-crash')
    worker.send('start', options=base)
    state = worker.wait('Recording'); time.sleep(5)
    query = f"Get-CimInstance Win32_Process -Filter 'ParentProcessId={worker.process.pid}' | Select-Object -ExpandProperty ProcessId | ConvertTo-Json -Compress"
    pids = json.loads(subprocess.check_output(['pwsh', '-NoProfile', '-Command', query], creationflags=FLAGS))
    if isinstance(pids, int): pids = [pids]
    assert pids, 'Expected encoder child before simulated crash'
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.OpenProcess.restype = ctypes.c_void_p
    kernel.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    handles = [kernel.OpenProcess(0x00100000, False, pid) for pid in pids]
    assert all(handles)
    try:
        worker.process.kill(); worker.process.wait(timeout=10); worker.close(); worker = None
        assert all(kernel.WaitForSingleObject(handle, 10000) == 0 for handle in handles), 'Orphan encoder after worker crash'
    finally:
        for handle in handles:
            if handle: kernel.CloseHandle(handle)
    recovery = pathlib.Path(state['recoveryDirectory'])
    originals = {p.name: p.stat().st_size for p in recovery.glob('part-*.mkv')}
    result = subprocess.run([str(EXE), '--recover', str(recovery)], capture_output=True, text=True,
                            encoding='utf-8', creationflags=FLAGS, timeout=60, check=True)
    recovered = json.loads(result.stdout)['outputFile']
    verify({**state, 'outputFile': recovered}, False)
    assert originals == {p.name: p.stat().st_size for p in recovery.glob('part-*.mkv')}
    report['checks'].append('Worker crash reaps encoder; retained MKV recovers to fully decoded MP4 without deleting originals')
    print('PASS: crash cleanup + recovery', flush=True)
    report['passed'] = True
    print('PASS: all recording checks', flush=True)
finally:
    if worker is not None: worker.close()
    fixture.terminate(); fixture.wait(timeout=10)
    (RUN / 'summary.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
