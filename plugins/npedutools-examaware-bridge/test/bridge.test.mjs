import test from 'node:test';
import assert from 'node:assert/strict';
import net from 'node:net';
import { EventEmitter, once } from 'node:events';
import { createRequire } from 'node:module';
import { randomUUID } from 'node:crypto';
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
  let file = JSON.stringify({ version: 1, host: '127.0.0.1', port: 43210, key: 'a'.repeat(64) });
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

test('distribution declares no write or quit permission', async () => {
  const manifest = JSON.parse(await readFile(new URL('../package.json', import.meta.url), 'utf8'));
  assert.ok(!manifest.examaware.permissions.includes('app.quit'));
  assert.ok(!manifest.examaware.permissions.includes('app.configure'));
  assert.ok(manifest.examaware.permissions.includes('ui.notify'));
});
