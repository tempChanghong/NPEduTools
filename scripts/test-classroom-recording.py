"""Private school clock and owned visible fixture only; no desktop or audio capture."""
import ctypes
import hashlib
import json
import os
from pathlib import Path
import queue
import struct
import subprocess
import threading
import time
import uuid
from datetime import datetime, timedelta

ROOT = Path(__file__).resolve().parent.parent
RUN = ROOT / '.artifacts' / 'classroom-recording' / uuid.uuid4().hex
RUN.mkdir(parents=True)
PIPE = 'NPEduTools.Test.classroom.' + uuid.uuid4().hex
UPSTREAM = PIPE + '.school'
CLIENT = str(uuid.uuid4())
FLAGS = subprocess.CREATE_NO_WINDOW
HOST = ROOT / 'src/NPEduTools.Host/bin/Release/net10.0/NPEduTools.Host.exe'
REC = HOST.parent / 'Recorder/NPEduTools.Recorder.exe'
FIXTURE = ROOT / 'tests/NPEduTools.Recording.TestFixture/bin/Release/net10.0-windows/NPEduTools.Recording.TestFixture.exe'
PEER = ROOT / 'tests/NPEduTools.ClassIsland.TestPeer/bin/Release/net10.0/NPEduTools.ClassIsland.TestPeer.exe'
TITLE = 'Automatic recording owned fixture ' + uuid.uuid4().hex
BOOK = Path(os.environ['LOCALAPPDATA']) / 'NPEduTools/ui' / (hashlib.sha256(PIPE.encode()).hexdigest().upper()[:24] + '.recording-plans.json')
CLOCK = RUN / 'school-control.json'
LEDGER = RUN / 'host-data/recording-executions.json'
OUTPUT = RUN / 'videos'
OUTPUT.mkdir()
checks = []
host = peer = fixture = direct = None
logs = []

def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix(path.suffix + '.tmp')
    temp.write_text(json.dumps(value, ensure_ascii=False), encoding='utf-8')
    os.replace(temp, path)

def rpc(capability, **extra):
    request = dict(version=1, requestId=str(uuid.uuid4()), capability=capability, timeoutMs=15000, **extra)
    data = json.dumps(request).encode()
    end = time.monotonic() + 5
    while True:
        try:
            stream = open('\\\\.\\pipe\\' + PIPE, 'r+b', buffering=0)
            break
        except OSError:
            if time.monotonic() >= end: raise
            time.sleep(.05)
    with stream:
        stream.write(struct.pack('<I', len(data)) + data)
        def read(size):
            buf = bytearray()
            while len(buf) < size:
                part = stream.read(size - len(buf))
                if not part: raise EOFError('Host closed response')
                buf.extend(part)
            return bytes(buf)
        length = struct.unpack('<I', read(4))[0]
        assert 0 < length <= 65536
        reply = json.loads(read(length))
        assert reply['requestId'] == request['requestId']
        return reply

def auto(action, **extra):
    return rpc('recording.automatic', automatic=dict(action=action, clientId=CLIENT, **extra))

def status():
    auto('lease')
    return rpc('recording.status')

def wait(predicate, seconds=30):
    end = time.monotonic() + seconds
    last = None
    while time.monotonic() < end:
        last = status()
        if predicate(last): return last
        time.sleep(.2)
    raise AssertionError('Timed out: ' + json.dumps(last, ensure_ascii=False))

def idle(seconds):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        status(); time.sleep(.25)

def start_host():
    env = dict(os.environ, NPEEDUTOOLS_RECORDING_FIXTURE=TITLE)
    log = (RUN / ('host-' + uuid.uuid4().hex[:8] + '.log')).open('w', encoding='utf-8'); logs.append(log)
    process = subprocess.Popen([str(HOST), '--pipe', PIPE, '--classisland-pipe', UPSTREAM, '--data-dir', str(RUN / 'host-data')],
                               env=env, stdout=subprocess.PIPE, stderr=log, text=True, encoding='utf-8', creationflags=FLAGS)
    assert process.stdout.readline().startswith('Host ready:')
    return process

def school_now():
    return datetime.fromisoformat(rpc('classisland.school-clock')['schoolClock']['schoolNow']).replace(tzinfo=None)

def fixed_book(duration=20, delay=1):
    now = school_now()
    start = now + timedelta(seconds=delay)
    end = start + timedelta(seconds=duration)
    book = dict(Version=1, Recurring=[], Dated=[dict(Id=str(uuid.uuid4()), Name='无人值守短时段', Date=start.date().isoformat(),
        Start=start.time().isoformat(), End=end.time().isoformat(), Enabled=True)], Overrides=[], Days=[], ExcludedNames=[], Subjects=[])
    write(BOOK, book)
    return book

def command(action, control=None, options=None):
    return rpc('recording.command', recording=dict(action=action, options=options, control=control, clientId=CLIENT))

def passed(message):
    checks.append(message); print('PASS:', message, flush=True)

def verify(path):
    result = subprocess.run([str(REC.parent / 'Tools/ffprobe.exe'), '-v', 'error', '-show_streams', '-show_format', '-of', 'json', str(path)],
        capture_output=True, text=True, encoding='utf-8', creationflags=FLAGS, check=True, timeout=30)
    probe = json.loads(result.stdout)
    assert [s['codec_type'] for s in probe['streams']] == ['video']
    assert probe['streams'][0]['codec_name'] == 'h264'
    subprocess.run([str(REC.parent / 'Tools/ffmpeg.exe'), '-v', 'error', '-xerror', '-i', str(path), '-f', 'null', '-'],
        capture_output=True, creationflags=FLAGS, check=True, timeout=30)
    return float(probe['format']['duration'])

print('Classroom recording artifacts:', RUN, flush=True)
MODE = RUN / 'host-data/classroom-mode.json'
def mode_file(name):
    # Only while the private Host is stopped; simulates a completed mode journal from a prior run.
    assert host is None or host.poll() is not None
    write(MODE, dict(revision=1, mode=name, phase='Idle', automaticPaused=name == 'Exam',
                     message='isolated mode persistence fixture', recentRequests=[]))
try:
    write(CLOCK, dict(noPlan=True))
    fixture = subprocess.Popen([str(FIXTURE), 'title=' + TITLE])
    peer = subprocess.Popen([str(PEER), UPSTREAM, 'bridge'], env=dict(os.environ, NPEEDUTOOLS_TEST_BRIDGE_CONTROL=str(CLOCK)),
        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, encoding='utf-8', creationflags=FLAGS)
    assert peer.stdout.readline().strip() == 'READY'
    mode_file('Exam')
    host = start_host()
    env = json.loads(subprocess.run([str(REC), '--probe'], capture_output=True, text=True, encoding='utf-8', creationflags=FLAGS, check=True).stdout)
    options = dict(display=env['displays'][0]['id'], outputDirectory=str(OUTPUT), framesPerSecond=8, maximumHeight=720, systemAudio=False, microphone=False)
    idle(2)
    fixed_book(duration=90, delay=0)
    book_hash = hashlib.sha256(BOOK.read_bytes()).hexdigest()
    assert auto('enable', options=options)['outcome'] == 'Succeeded'
    idle(3)
    state = status()
    assert state['automatic']['enabled'] and state['automatic']['suspendedByMode']
    assert not state['recording']['active'] and not state['automatic']['recent']
    passed('Persisted Exam mode suppresses due fixed recording even when automatic recording is enabled')
    assert command('start', options=options)['outcome'] == 'Succeeded'
    manual = wait(lambda s: s['recording']['phase'] == 'Recording')['recording']['control']
    idle(2)
    assert status()['recording']['phase'] == 'Recording' and manual['owner'] == 'Manual'
    assert command('stop', manual)['outcome'] == 'Succeeded'
    done = wait(lambda s: s['recording']['phase'] == 'Saved')
    verify(done['recording']['outputFile'])
    passed('Manual recording remains usable during Exam mode and produces a fully decodable MP4')
    rpc('host.stop'); host.wait(timeout=15); host = start_host(); idle(2)
    assert status()['automatic']['suspendedByMode'] and not status()['automatic']['enabled']
    assert auto('enable', options=options)['outcome'] == 'Succeeded'
    idle(2); assert not status()['recording']['active']
    passed('Host restart preserves Exam pause; enabling the scheduler cannot bypass it')
    rpc('host.stop'); host.wait(timeout=15); host = None
    mode_file('Daily'); host = start_host(); idle(2)
    assert not status()['automatic']['suspendedByMode'] and not status()['automatic']['enabled']
    assert hashlib.sha256(BOOK.read_bytes()).hexdigest() == book_hash
    assert auto('enable', options=options)['outcome'] == 'Succeeded'
    automatic = wait(lambda s: s['recording']['phase'] == 'Recording')['recording']['control']
    assert automatic['owner'] == 'Automatic'
    idle(2); auto('disable')
    done = wait(lambda s: not s['recording']['active'])
    verify(done['recording']['outputFile'])
    assert hashlib.sha256(BOOK.read_bytes()).hexdigest() == book_hash
    passed('Daily mode preserves the exact plan file, does not force-enable recording, and resumes the original due plan once enabled')
    rpc('host.stop'); host.wait(timeout=15); host = None
    write(RUN / 'summary.json', dict(passed=True, checks=checks, videos=[str(p) for p in OUTPUT.glob('*.mp4')]))
finally:
    for process in [host, peer, fixture]:
        if process is not None and process.poll() is None:
            process.kill(); process.wait(timeout=10)
    for log in logs: log.close()