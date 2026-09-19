import test from 'node:test';
import assert from 'node:assert/strict';
import net from 'node:net';
import { EventEmitter, once } from 'node:events';
import { createRequire } from 'node:module';
import { randomUUID, createHmac, randomBytes } from 'node:crypto';
import { mkdtemp, rm, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { spawn } from 'node:child_process';
import { setTimeout as delay } from 'node:timers/promises';
import renderer from '../dist/renderer/index.mjs';
const require = createRequire(import.meta.url);
const entry = require('../dist/main/index.cjs').default;
const hostExe = resolve('../../src/NPEduTools.Host/bin/Release/net10.0/NPEduTools.Host.exe');
const subscribe = (events, event, handler) => { events.on(event, handler); return { dispose: () => events.off(event, handler) }; };

// Adapter follows the official SDK TCP handle contract. Actual bytes cross Windows TCP and the .NET Host pipe.
async function connectTcp(options) {
  assert.equal(options.host, '127.0.0.1'); assert.equal(options.allowLocalNetwork, true);
  const socket = net.connect(options.port, options.host), events = new EventEmitter();
  let state = 'connecting';
  socket.on('connect', () => { state = 'open'; events.emit('state', state); });
  socket.on('data', data => { // Fragment every frame header to exercise buffering.
    for (const chunk of [data.subarray(0, 2), data.subarray(2)]) events.emit('data', new Uint8Array(chunk));
  });
  socket.on('error', () => { state = 'error'; events.emit('state', state); });
  socket.on('close', () => { state = 'closed'; events.emit('state', state); });
  return { get state() { return state; },
    write: data => new Promise((res, rej) => socket.write(data, err => err ? rej(err) : res())),
    onData: callback => subscribe(events, 'data', callback), onStateChanged: callback => subscribe(events, 'state', callback),
    dispose: () => { events.removeAllListeners(); socket.destroy(); }, end: async () => socket.end() };
}
function context(config = {}) {
  const deferred = [], changes = new EventEmitter(), cards = new Map();
  let settings = config;
  const state = { version: '1.5.2', registered: true, failRead: false, infoReads: 0 };
  const api = {
    settings: {
      get: () => settings,
      replace: async value => { settings = value; changes.emit('change', value); },
      onChanged: handler => subscribe(changes, 'change', handler)
    },
    app: { info: async () => { state.infoReads++; return { name: 'ExamAware', version: state.version, platform: 'win32', packaged: true }; },
      getAutoStart: async () => { if (state.failRead) throw new Error('unavailable'); return state.registered; } },
    network: { connectTcp },
    ui: { home: { register: card => { cards.set(card.id, card); return { dispose: () => cards.delete(card.id) }; } } }
  };
  return { api, state, cards, scope: {
    defer: callback => deferred.push(callback), add: item => deferred.push(() => item.dispose()),
    dispose: async () => { for (const fn of deferred.splice(0).reverse()) await fn(); }
  } };
}
async function request(pipe, capability) {
  const body = Buffer.from(JSON.stringify({ version: 1, requestId: randomUUID(), capability }));
  const header = Buffer.alloc(4); header.writeInt32LE(body.length);
  return await new Promise((resolveRequest, reject) => {
    const socket = net.connect('\\\\.\\pipe\\' + pipe); let buffer = Buffer.alloc(0);
    socket.setTimeout(4000, () => socket.destroy(new Error('pipe timeout')));
    socket.on('connect', () => socket.write(Buffer.concat([header, body])));
    socket.on('error', reject);
    socket.on('data', data => {
      buffer = Buffer.concat([buffer, data]);
      if (buffer.length >= 4 && buffer.length >= 4 + buffer.readInt32LE()) {
        socket.destroy(); resolveRequest(JSON.parse(buffer.subarray(4, 4 + buffer.readInt32LE()).toString()));
      }
    });
  });
}
async function until(check, timeout = 10000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) { try { const value = await check(); if (value) return value; } catch {} await delay(50); }
  assert.fail('Timed out waiting for condition');
}
async function fixture(t) {
  const directory = await mkdtemp(join(tmpdir(), 'NPEduTools.ExamAware.Node.'));
  const pipe = 'NPEduTools.Test.examaware.' + randomUUID();
  let child, output = '';
  async function start() {
    child = spawn(hostExe, ['--pipe', pipe, '--data-dir', directory], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    child.stdout.on('data', data => output += data); child.stderr.on('data', data => output += data);
    await until(async () => (await request(pipe, 'host.ping')).outcome === 'Succeeded');
  }
  async function stop() { if (child && child.exitCode === null) { const exited = once(child, 'exit'); child.kill(); await exited; } }
  t.after(async () => { await stop(); await rm(directory, { recursive: true, force: true }); });
  await start();
  return { start, stop, request: capability => request(pipe, capability), get output() { return output; } };
}

test('real Host: official SDK entry, authenticated heartbeat, false/unknown, restart and unload', async t => {
  const host = await fixture(t);
  const config = (await host.request('examaware.pairing.get')).examAwarePairing;
  const ctx = context(config);
  assert.equal(entry.apiVersion, 2); assert.equal(entry.process, 'main');
  const unload = await entry(ctx); t.after(unload);
  const status = async () => (await host.request('examaware.status')).examAware;
  await until(async () => (await status()).bridgeState === 'Connected');
  assert.equal((await status()).autoStartRegistered, true);
  ctx.state.registered = false;
  await until(async () => (await status()).autoStartRegistered === false);
  ctx.state.failRead = true;
  await until(async () => (await status()).autoStartRegistered === undefined || (await status()).autoStartRegistered === null);
  ctx.state.failRead = false;
  await host.stop(); await host.start();
  assert.deepEqual((await host.request('examaware.pairing.get')).examAwarePairing, config);
  await until(async () => (await status()).bridgeState === 'Connected');
  await unload();
  await until(async () => (await status()).bridgeState === 'Disconnected');
  assert.ok(!host.output.includes(config.key));
});

test('wrong pairing key never reads app data or becomes connected', async t => {
  const host = await fixture(t);
  const config = (await host.request('examaware.pairing.get')).examAwarePairing;
  const ctx = context({ ...config, key: '0'.repeat(64) });
  const unload = await entry(ctx); t.after(unload);
  await delay(1600);
  assert.equal(ctx.state.infoReads, 0);
  assert.equal((await host.request('examaware.status')).examAware.bridgeState, 'Disconnected');
});

test('re-activation replaces the bridge; disposing old activation cannot stop the new owner', async t => {
  const host = await fixture(t), config = (await host.request('examaware.pairing.get')).examAwarePairing;
  const first = context(config), second = context(config); second.state.registered = false;
  const stopFirst = await entry(first); t.after(stopFirst);
  await until(async () => (await host.request('examaware.status')).examAware.autoStartRegistered === true);
  const stopSecond = await entry(second); t.after(stopSecond);
  await until(async () => (await host.request('examaware.status')).examAware.autoStartRegistered === false);
  await stopFirst(); await delay(2300);
  assert.equal((await host.request('examaware.status')).examAware.autoStartRegistered, false);
});

test('eager settings subscription and startup share one connection attempt', async t => {
  const host = await fixture(t), ctx = context((await host.request('examaware.pairing.get')).examAwarePairing);
  const subscribe = ctx.api.settings.onChanged;
  ctx.api.settings.onChanged = handler => { const subscription = subscribe(handler); handler(ctx.api.settings.get()); return subscription; };
  let attempts = 0;
  ctx.api.network.connectTcp = options => { attempts++; return connectTcp(options); };
  const stop = await entry(ctx); t.after(stop);
  await until(async () => (await host.request('examaware.status')).examAware.bridgeState === 'Connected');
  await delay(300); assert.equal(attempts, 1);
});

test('settings import reconnects main plugin, clearing pairing stops it', async t => {
  const host = await fixture(t), ctx = context();
  const unload = await entry(ctx); t.after(unload);
  await ctx.api.settings.replace((await host.request('examaware.pairing.get')).examAwarePairing);
  await until(async () => (await host.request('examaware.status')).examAware.bridgeState === 'Connected');
  await ctx.api.settings.replace({});
  await until(async () => (await host.request('examaware.status')).examAware.bridgeState === 'Disconnected');
});

test('renderer action validates exported file and removes its cards on unload', async () => {
  const ctx = context(); const messages = [];
  let file = JSON.stringify({ version: 2, host: '127.0.0.1', port: 43210, key: 'a'.repeat(64) });
  ctx.api.files = { open: async () => ['pairing.json'], stat: async () => ({ size: file.length }), readText: async () => file };
  ctx.api.dialogs = { message: async value => { messages.push(value); return { response: 0 }; } };
  assert.equal(renderer.apiVersion, 2);
  const unload = await renderer(ctx);
  await ctx.cards.get('npedutools-pair').action();
  assert.equal(ctx.api.settings.get().port, 43210);
  file = JSON.stringify({ ...ctx.api.settings.get(), host: '192.168.1.1' });
  await ctx.cards.get('npedutools-pair').action();
  assert.equal(messages.at(-1).type, 'error');
  assert.equal(ctx.api.settings.get().host, '127.0.0.1');
  await ctx.cards.get('npedutools-unpair').action();
  assert.deepEqual(ctx.api.settings.get(), {});
  await unload(); assert.equal(ctx.cards.size, 0);
});

test('distribution declares explicit quit and auto-start write permissions', async () => {
  const manifest = JSON.parse(await readFile(new URL('../package.json', import.meta.url), 'utf8'));
  assert.ok(manifest.examaware.permissions.includes('app.quit'));
  assert.ok(manifest.examaware.permissions.includes('app.configure'));
  assert.ok(manifest.examaware.permissions.includes('ui.notify'));
});

async function commandFixture(t, readyProof = true) {
  const key = randomBytes(32).toString('hex'), nonce = randomBytes(32).toString('hex');
  const hmac = value => createHmac('sha256', Buffer.from(key, 'hex')).update(value).digest('hex');
  const server = net.createServer(); server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const accepted = once(server, 'connection');
  const ctx = context({ version: 2, host: '127.0.0.1', port: server.address().port, key });
  let calls = 0, denied = false;
  ctx.api.app.quit = () => { calls++; if (denied) throw Object.assign(new Error('policy'), { code: 'permission-denied' }); };
  const unload = await entry(ctx), [socket] = await accepted;
  const frames = []; let buffer = Buffer.alloc(0);
  socket.on('error', () => {});
  socket.on('data', data => {
    buffer = Buffer.concat([buffer, data]);
    while (buffer.length >= 4 && buffer.length >= buffer.readInt32LE() + 4) {
      const n = buffer.readInt32LE(); frames.push(JSON.parse(buffer.subarray(4, n + 4))); buffer = buffer.subarray(n + 4);
    }
  });
  const write = value => { const b = Buffer.from(JSON.stringify(value)), h = Buffer.alloc(4); h.writeInt32LE(b.length); socket.write(Buffer.concat([h, b])); };
  t.after(async () => { await unload(); socket.destroy(); await new Promise(res => server.close(res)); });
  write({ version: 2, type: 'hello', nonce, proof: hmac('host\n' + nonce) });
  const challenge = await until(() => frames.find(f => f.type === 'challenge'));
  assert.equal(challenge.proof, hmac(`peer-auth\n${nonce}\n${challenge.nonce}`));
  write({ version: 2, type: 'ready', nonce: challenge.nonce, proof: readyProof ? hmac(`host-auth\n${nonce}\n${challenge.nonce}`) : '0'.repeat(64) });
  let sequence = 0;
  return { ctx, frames, get calls() { return calls; }, deny() { denied = true; },
    send({ id = randomUUID(), expired = false, badProof = false, repeatedSequence = false, action = 'quit', enabled } = {}) {
      const issuedAt = Date.now() - (expired ? 10000 : 0), payload = JSON.stringify({ requestId: id, action, issuedAt, expiresAt: issuedAt + 3000, enabled });
      const n = repeatedSequence ? sequence : ++sequence;
      write({ version: 2, type: 'command', sequence: n, payload, proof: badProof ? '0'.repeat(64) : hmac(`host\n${nonce}\n${challenge.nonce}\n${n}\ncommand\n${payload}`) });
      return id;
    }, reply: id => frames.filter(f => f.type === 'reply' || f.type === 'autostart.reply').map(f => JSON.parse(f.payload)).filter(f => f.requestId === id)
  };
}
test('quit acknowledges before invocation; duplicate IDs and expired commands never execute', async t => {
  const f = await commandFixture(t); await until(() => f.frames.some(x => x.type === 'status'));
  const id = f.send(); await until(() => f.calls === 1);
  await until(() => f.reply(id).some(x => x.state === 'Accepted'));
  f.send({ id }); await until(() => f.reply(id).some(x => x.state === 'Failed')); assert.equal(f.calls, 1);
  const old = f.send({ expired: true }); await until(() => f.reply(old).some(x => x.state === 'Expired')); assert.equal(f.calls, 1);
  f.deny(); const blocked = f.send(); await until(() => f.reply(blocked).some(x => x.state === 'Denied')); assert.equal(f.calls, 2);
});
test('recorded hello without proof of fresh client challenge cannot read or quit', async t => {
  const f = await commandFixture(t, false); await delay(300);
  assert.equal(f.ctx.state.infoReads, 0); assert.equal(f.calls, 0);
});
test('forged command HMAC cannot invoke quit', async t => {
  const f = await commandFixture(t); await until(() => f.frames.some(x => x.type === 'status'));
  f.send({ badProof: true }); await delay(300); assert.equal(f.calls, 0);
});
test('replayed sequence closes session without a second quit', async t => {
  const f = await commandFixture(t); await until(() => f.frames.some(x => x.type === 'status'));
  f.send(); await until(() => f.calls === 1);
  f.send({ repeatedSequence: true }); await delay(300); assert.equal(f.calls, 1);
});

test('auto-start enable and disable use readback, not setter truthiness; no duplicate or expired write', async t => {
  const f = await commandFixture(t); let writes = 0;
  f.ctx.api.app.setAutoStart = async enabled => { writes++; f.ctx.state.registered = enabled; return enabled; };
  await until(() => f.frames.some(x => x.type === 'status'));
  for (const enabled of [false, true]) {
    const id = f.send({ action: 'autostart.set', enabled });
    await until(() => f.reply(id).some(x => x.state === 'Succeeded' && x.registered === enabled));
    const before = writes; f.send({ id, action: 'autostart.set', enabled });
    await until(() => f.reply(id).some(x => x.state === 'Failed')); assert.equal(writes, before);
  }
  const id = f.send({ action: 'autostart.set', enabled: false, expired: true });
  await until(() => f.reply(id).some(x => x.state === 'Expired')); assert.equal(writes, 2);
});
test('auto-start mismatched readback, read failure and permission denial are distinct', async t => {
  const f = await commandFixture(t); let writes = 0;
  f.ctx.api.app.setAutoStart = async () => { writes++; return false; }; // SDK return must not be mistaken for the actual readback.
  await until(() => f.frames.some(x => x.type === 'status'));
  let id = f.send({ action: 'autostart.set', enabled: false });
  await until(() => f.reply(id).some(x => x.state === 'Mismatch' && x.registered === true));
  f.ctx.state.failRead = true;
  id = f.send({ action: 'autostart.set', enabled: false });
  await until(() => f.reply(id).some(x => x.state === 'Unconfirmed' && x.registered === null));
  f.ctx.api.app.setAutoStart = async () => { throw Object.assign(new Error('permission'), { code: 'permission-denied' }); };
  id = f.send({ action: 'autostart.set', enabled: true });
  await until(() => f.reply(id).some(x => x.state === 'Denied')); assert.equal(writes, 2);
});
test('auto-start cannot overlap another mutation or quit, and disconnect never replays a write', async t => {
  const f = await commandFixture(t); let finish, writes = 0;
  f.ctx.api.app.setAutoStart = () => { writes++; return new Promise(resolve => { finish = resolve; }); };
  await until(() => f.frames.some(x => x.type === 'status'));
  const first = f.send({ action: 'autostart.set', enabled: false }); await until(() => writes === 1);
  const second = f.send({ action: 'autostart.set', enabled: true });
  await until(() => f.reply(second).some(x => x.state === 'Failed'));
  const quit = f.send(); await until(() => f.reply(quit).some(x => x.state === 'Failed')); assert.equal(f.calls, 0);
  await f.ctx.api.settings.replace({}); finish(false); await delay(200);
  assert.equal(writes, 1); assert.equal(f.reply(first).length, 0);
});
for (const invalid of [{ badProof: true }, { enabled: 'false' }, { enabled: undefined }]) {
  test(`invalid auto-start command cannot write: ${JSON.stringify(invalid)}`, async t => {
    const f = await commandFixture(t); let writes = 0;
    f.ctx.api.app.setAutoStart = async () => { writes++; return false; };
    await until(() => f.frames.some(x => x.type === 'status'));
    f.send({ action: 'autostart.set', enabled: false, ...invalid }); await delay(200); assert.equal(writes, 0);
  });
}
test('a heartbeat started before a write cannot publish stale registration after readback', async t => {
  const f = await commandFixture(t);
  await until(() => f.frames.some(x => x.type === 'status'));
  let oldRead, reads = 0;
  f.ctx.api.app.getAutoStart = () => {
    if (++reads === 1) return new Promise(resolve => { oldRead = resolve; });
    return Promise.resolve(f.ctx.state.registered);
  };
  f.ctx.api.app.setAutoStart = async enabled => { f.ctx.state.registered = enabled; return enabled; };
  await until(() => oldRead);
  const id = f.send({ action: 'autostart.set', enabled: false });
  await until(() => f.reply(id).some(x => x.state === 'Succeeded'));
  const afterReply = f.frames.length;
  oldRead(true); await delay(150);
  assert.ok(f.frames.slice(afterReply).filter(x => x.type === 'status').every(x => JSON.parse(x.payload).autoStartRegistered !== true));
});
