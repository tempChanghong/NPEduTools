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
RUN = ROOT / '.artifacts' / 'automatic-recording' / uuid.uuid4().hex
RUN.mkdir(parents=True)
PIPE = 'NPEduTools.Test.automatic.' + uuid.uuid4().hex
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

print('Automatic recording artifacts:', RUN, flush=True)
try:
    write(CLOCK, dict(lessonLengthSeconds=16))
    fixture = subprocess.Popen([str(FIXTURE), 'title=' + TITLE])
    peer = subprocess.Popen([str(PEER), UPSTREAM, 'bridge'], env=dict(os.environ, NPEEDUTOOLS_TEST_BRIDGE_CONTROL=str(CLOCK)),
        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, encoding='utf-8', creationflags=FLAGS)
    assert peer.stdout.readline().strip() == 'READY'
    host = start_host()
    env = json.loads(subprocess.run([str(REC), '--probe'], capture_output=True, text=True, encoding='utf-8', creationflags=FLAGS, check=True).stdout)
    options = dict(display=env['displays'][0]['id'], outputDirectory=str(OUTPUT), framesPerSecond=8, maximumHeight=720, systemAudio=False, microphone=False)
    write(BOOK, dict(Version=1, Recurring=[dict(Id=str(uuid.uuid4()), Name='短课程', Enabled=True, Filter=dict(BeforeMinutes=0, AfterMinutes=0))], Dated=[], Overrides=[], Days=[], ExcludedNames=[], Subjects=[]))
    idle(2)
    assert auto('enable', options=options)['outcome'] == 'Succeeded'
    recording = wait(lambda s: s['recording']['phase'] == 'Recording')
    identity = recording['recording']['control']
    assert identity['owner'] == 'Automatic'
    assert json.loads(LEDGER.read_text())['Entries'][0]['Phase'] in ['Starting', 'Recording']
    idle(2)
    assert command('pause', identity)['outcome'] == 'Succeeded'
    wait(lambda s: s['recording']['phase'] == 'Paused')
    write(CLOCK, dict(lessonLengthSeconds=16, offsetSeconds=-120))
    done = wait(lambda s: s['automatic']['recent'] and s['automatic']['recent'][0]['phase'] == 'Recorded', 25)
    course = done['automatic']['recent'][0]
    assert course['date'] == '2031-04-07'
    assert 1 < verify(course['outputFile']) < 15
    passed('Real short course uses school time; pause and 120-second rollback do not extend hard deadline; MP4 fully decodes')
    write(CLOCK, dict(lessonLengthSeconds=16))
    idle(2)
    assert len(status()['automatic']['recent']) == 1
    rpc('host.stop'); host.wait(timeout=15); host = start_host(); idle(2)
    assert not status()['automatic']['enabled']
    assert auto('enable', options=options)['outcome'] == 'Succeeded'
    idle(2); assert len(status()['automatic']['recent']) == 1
    passed('Recorded occurrence survives Host restart; auto remains off until enabled; no duplicate start')
    auto('disable')
    assert command('start', options=options)['outcome'] == 'Succeeded'
    manual = wait(lambda s: s['recording']['phase'] == 'Recording')['recording']['control']
    fixed_book(duration=20, delay=0)
    assert auto('enable', options=options)['outcome'] == 'Succeeded'
    skipped = wait(lambda s: s['automatic']['recent'][0]['phase'] == 'Skipped')
    assert skipped['recording']['control']['sessionId'] == manual['sessionId']
    assert command('stop', identity)['outcome'] == 'Rejected'
    assert status()['recording']['phase'] == 'Recording'
    assert command('stop', manual)['outcome'] == 'Succeeded'
    wait(lambda s: s['recording']['phase'] == 'Saved'); idle(1)
    assert status()['automatic']['recent'][0]['phase'] == 'Skipped'
    passed('Manual recording owns the device; due automatic task is skipped; late old auto Stop cannot stop manual session')
    auto('disable'); write(CLOCK, dict(noPlan=True)); idle(1)
    fixed_book(duration=24, delay=2)
    assert auto('enable', options=options)['outcome'] == 'Succeeded'
    active = wait(lambda s: s['recording']['phase'] == 'Recording' and s['recording']['control']['owner'] == 'Automatic')
    session_id = active['recording']['control']['sessionId']
    idle(2)
    host.kill(); host.wait(timeout=5); host = None
    manifest = OUTPUT / '.npeedutools-sessions' / ('自动微课-' + session_id.replace('-', '')) / 'session.json'
    end = time.monotonic() + 20
    while time.monotonic() < end:
        try:
            result = json.loads(manifest.read_text(encoding='utf-8'))
            if result['state']['phase'] == 'Saved': break
        except (OSError, json.JSONDecodeError): pass
        time.sleep(.2)
    else: raise AssertionError('Recorder failed to finalize after Host loss')
    verify(result['state']['outputFile'])
    host = start_host(); idle(2)
    recovered = status()['automatic']
    assert not recovered['enabled'] and recovered['recent'][0]['phase'] == 'Interrupted'
    assert auto('enable', options=options)['outcome'] == 'Succeeded'
    idle(2)
    assert not status()['recording']['active']
    passed('Fixed window works without timetable; killed Host triggers worker finalization; restart marks Interrupted and never replays')
    auto('disable'); fixed_book(duration=30, delay=1)
    assert auto('enable', options=options)['outcome'] == 'Succeeded'
    wait(lambda s: s['recording']['phase'] == 'Recording' and s['recording']['control']['owner'] == 'Automatic')
    time.sleep(15)  # Host stays alive; simulate a dead/frozen App by withholding its lease.
    lost_app = rpc('recording.status')
    assert not lost_app['automatic']['enabled']
    ended = wait(lambda s: not s['recording']['active'])
    verify(ended['recording']['outputFile'])
    passed('Missing App heartbeat disables future scheduling and ends the active session while Host remains alive')
    # The durable Starting intent must precede capture, including when the ledger cannot be replaced.
    fixed_book(duration=20, delay=1)
    before_files = len(list(OUTPUT.glob('*.mp4')))
    backup = LEDGER.with_suffix('.before-failure.json')
    LEDGER.rename(backup); LEDGER.mkdir()
    try:
        assert auto('enable', options=options)['outcome'] == 'Succeeded'
        rejected = wait(lambda s: not s['automatic']['enabled'] and s['automatic']['error'])
        assert not rejected['recording']['active'] and len(list(OUTPUT.glob('*.mp4'))) == before_files
    finally:
        LEDGER.rmdir(); backup.rename(LEDGER)
    passed('Ledger commit failure prevents capture; original journal is preserved and automatic mode is disabled')
    auto('disable'); rpc('host.stop'); host.wait(timeout=15); host = None
    # Independent recorder guard: controller keeps stdin open but ceases lease traffic.
    events = queue.Queue()
    direct = subprocess.Popen([str(REC), '--fixture-window', TITLE], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL, text=True, encoding='utf-8', creationflags=FLAGS)
    def read_direct():
        for line in direct.stdout: events.put(json.loads(line))
    threading.Thread(target=read_direct, daemon=True).start()
    assert events.get(timeout=5)['phase'] == 'Idle'
    counter = ctypes.c_longlong(); frequency = ctypes.c_longlong()
    ctypes.windll.kernel32.QueryPerformanceCounter(ctypes.byref(counter)); ctypes.windll.kernel32.QueryPerformanceFrequency(ctypes.byref(frequency))
    c = dict(owner='Automatic', sessionId=str(uuid.uuid4()), occurrenceId='fixed/silent-controller/2031-04-07',
        hardDeadline=counter.value + 30 * frequency.value, leaseDeadline=counter.value + 8 * frequency.value)
    direct.stdin.write(json.dumps(dict(action='start', options=options, control=c)) + '\n'); direct.stdin.flush()
    start = time.monotonic()
    while True:
        state = events.get(timeout=20)
        if state['phase'] == 'Failed': raise AssertionError(state)
        if state['phase'] == 'Saved': break
    assert time.monotonic() - start < 15
    assert verify(state['outputFile']) < 10
    passed('Open but silent control pipe expires after eight seconds; independent worker saves without Host Stop')
    direct.stdin.close(); direct.wait(timeout=10); direct = None
    for video in OUTPUT.glob('*.mp4'): verify(video)
    write(RUN / 'summary.json', dict(passed=True, checks=checks, videos=[str(p) for p in OUTPUT.glob('*.mp4')]))
finally:
    for process in [direct, host, peer, fixture]:
        if process is not None and process.poll() is None:
            process.kill(); process.wait(timeout=10)
    for log in logs: log.close()
