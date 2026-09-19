import { defineMainPlugin, type MainPluginContext, type TcpSocketHandle } from '@dsz-examaware/plugin-sdk';
import { createHmac, timingSafeEqual, randomBytes } from 'node:crypto';
import { pairing, type Pairing } from './pairing.js';
const sign = (key: string, text: string) => createHmac('sha256', Buffer.from(key, 'hex')).update(text).digest('hex');
const verify = (key: string, text: string, proof: unknown) => typeof proof === 'string' && /^[a-f0-9]{64}$/.test(proof) && timingSafeEqual(Buffer.from(proof, 'hex'), Buffer.from(sign(key, text), 'hex'));
const MAX = 65536;
export function activateBridge(ctx: MainPluginContext) {
  // Keep one owner across re-activation/CJS cache reloads; old disposal cannot stop a newer owner.
  const ownerKey = Symbol.for('npedutools.examaware.bridge.owner.v2');
  const owners = globalThis as unknown as Record<symbol, { dispose(): void } | undefined>;
  owners[ownerKey]?.dispose();
  let stopped = false, generation = 0, socket: TcpSocketHandle | undefined;
  let retry: ReturnType<typeof setTimeout> | undefined, cleanup: (() => void) | undefined;
  let failures = 0, connectingGeneration = -1, commandOwner: string | undefined, mutationEpoch = 0;
  const consumed = new Set<string>();
  function reset() { generation++; clearTimeout(retry); cleanup?.(); cleanup = undefined; socket?.dispose(); socket = undefined; }
  function schedule() {
    if (stopped) return;
    reset(); retry = setTimeout(() => void connect(), Math.min(15000, 1000 * 2 ** Math.min(failures++, 4)));
  }
  async function connect() {
    if (stopped) return;
    const config = pairing(ctx.api.settings.get()); if (!config) return;
    const current = generation;
    // The real settings API immediately emits its current value on subscription.
    // That callback and initial connect() must share a single connection attempt.
    if (connectingGeneration === current) return;
    connectingGeneration = current;
    try {
      const candidate = await ctx.api.network.connectTcp({ host: config.host, port: config.port, allowLocalNetwork: true, timeoutMs: 3000 });
      if (stopped || current !== generation) { candidate.dispose(); return; }
      socket = candidate; startSession(candidate, config, current);
    } catch { if (!stopped && current === generation) schedule(); }
  }
  function startSession(connection: TcpSocketHandle, config: Pairing, current: number) {
    let buffer = Buffer.alloc(0), serverNonce = '', clientNonce = '', ready = false;
    let sequence = 0, incoming = 0, sampling = false, queue = Promise.resolve();
    const active = () => !stopped && current === generation;
    const fail = () => { if (active()) schedule(); };
    const deadline = setTimeout(fail, 5000);
    const interval = setInterval(() => { if (ready) void sample(); }, 2000);
    async function write(value: unknown) {
      if (!active()) throw new Error('closed');
      const body = Buffer.from(JSON.stringify(value)); if (body.length > MAX) throw new Error('frame');
      const frame = Buffer.alloc(body.length + 4); frame.writeInt32LE(body.length); body.copy(frame, 4);
      await connection.write(frame);
    }
    function send(type: string, value: unknown) {
      const next = queue.then(async () => {
        const payload = JSON.stringify(value), n = ++sequence;
        await write({ version: 2, type, sequence: n, payload,
          proof: sign(config.key, `peer\n${serverNonce}\n${clientNonce}\n${n}\n${type}\n${payload}`) });
      });
      queue = next.catch(fail); return next;
    }
    async function sample() {
      if (!ready || sampling || !active() || commandOwner) return;
      sampling = true;
      const observedEpoch = mutationEpoch;
      try {
        const info = await ctx.api.app.info(); let registered: boolean | null = null;
        try { registered = await ctx.api.app.getAutoStart(); } catch { /* Unknown, never false. */ }
        if (!active() || commandOwner || observedEpoch !== mutationEpoch) return;
        await send('status', { name: info.name, version: info.version, platform: info.platform,
          packaged: info.packaged, autoStartRegistered: registered, processId: process.pid, canSetAutoStart: true });
        failures = 0;
      } catch { fail(); } finally { sampling = false; }
    }
    async function quit(command: { requestId: string; issuedAt: number; expiresAt: number }) {
      const reply = (state: string) => send('reply', { requestId: command.requestId, state });
      try {
        if (commandOwner || consumed.has(command.requestId) || consumed.size >= 64) { await reply('Failed'); return; }
        consumed.add(command.requestId);
        if (Date.now() > command.expiresAt || Date.now() < command.issuedAt - 1000) { await reply('Expired'); return; }
        commandOwner = command.requestId;
        await reply('Accepted'); // The official quit call may synchronously dispose this socket.
        if (!active()) return;
        if (Date.now() > command.expiresAt || Date.now() < command.issuedAt - 1000) { await reply('Expired'); return; }
        try { ctx.api.app.quit(); }
        catch (error) { await reply((error as { code?: string }).code === 'permission-denied' ? 'Denied' : 'Failed'); }
      } catch { fail(); } finally { if (commandOwner === command.requestId) commandOwner = undefined; }
    }
    async function setAutoStart(command: { requestId: string; issuedAt: number; expiresAt: number; enabled: boolean }) {
      const reply = (state: string, registered: boolean | null = null) => send('autostart.reply', { requestId: command.requestId, state, registered });
      let owns = false;
      try {
        if (commandOwner || consumed.has(command.requestId) || consumed.size >= 64) { await reply('Failed'); return; }
        consumed.add(command.requestId);
        if (!active()) return;
        if (Date.now() > command.expiresAt || Date.now() < command.issuedAt - 1000) { await reply('Expired'); return; }
        commandOwner = command.requestId; owns = true; mutationEpoch++;
        try { await ctx.api.app.setAutoStart(command.enabled); }
        catch (error) { await reply((error as { code?: string }).code === 'permission-denied' ? 'Denied' : 'Failed'); return; }
        if (!active()) return;
        let registered: boolean;
        try {
          registered = await ctx.api.app.getAutoStart();
          if (typeof registered !== 'boolean') throw new Error('unknown');
        } catch { await reply('Unconfirmed'); return; }
        if (!active()) return;
        await reply(registered === command.enabled ? 'Succeeded' : 'Mismatch', registered);
      } catch { fail(); }
      finally {
        if (owns) { commandOwner = undefined; mutationEpoch++; void sample(); }
      }
    }
    function receive(message: any) {
      if (!serverNonce) {
        if (message.version !== 2 || message.type !== 'hello' || typeof message.nonce !== 'string' ||
            !/^[a-f0-9]{64}$/.test(message.nonce) || !verify(config.key, `host\n${message.nonce}`, message.proof)) throw new Error('auth');
        serverNonce = message.nonce; clientNonce = randomBytes(32).toString('hex');
        void write({ version: 2, type: 'challenge', nonce: clientNonce,
          proof: sign(config.key, `peer-auth\n${serverNonce}\n${clientNonce}`) }).catch(fail);
      } else if (!ready) {
        if (message.version !== 2 || message.type !== 'ready' || message.nonce !== clientNonce ||
            !verify(config.key, `host-auth\n${serverNonce}\n${clientNonce}`, message.proof)) throw new Error('auth');
        ready = true; clearTimeout(deadline); void sample();
      } else {
        if (message.version !== 2 || message.type !== 'command' || message.sequence !== incoming + 1 || typeof message.payload !== 'string' ||
            !verify(config.key, `host\n${serverNonce}\n${clientNonce}\n${message.sequence}\ncommand\n${message.payload}`, message.proof)) throw new Error('auth');
        incoming++;
        const command = JSON.parse(message.payload);
        if (!['quit', 'autostart.set'].includes(command.action) || typeof command.requestId !== 'string' || !/^[0-9a-f-]{36}$/.test(command.requestId) ||
            !Number.isSafeInteger(command.issuedAt) || !Number.isSafeInteger(command.expiresAt) ||
            command.expiresAt - command.issuedAt <= 0 || command.expiresAt - command.issuedAt > 3000 ||
            (command.action === 'autostart.set' ? typeof command.enabled !== 'boolean' : command.enabled !== undefined)) throw new Error('command');
        if (command.action === 'quit') void quit(command); else void setAutoStart(command);
      }
    }
    const data = connection.onData(chunk => {
      if (!active()) return;
      try {
        if (buffer.length + chunk.length > MAX * 2 + 8) throw new Error('frame');
        buffer = Buffer.concat([buffer, chunk]);
        while (buffer.length >= 4) {
          const length = buffer.readInt32LE(); if (length <= 0 || length > MAX) throw new Error('length');
          if (buffer.length < length + 4) break;
          const body = buffer.subarray(4, length + 4); buffer = buffer.subarray(length + 4);
          receive(JSON.parse(body.toString('utf8')));
        }
      } catch (error) { console.warn('[NPEduTools bridge] rejected frame:', error instanceof Error ? error.message : 'invalid'); fail(); }
    });
    const state = connection.onStateChanged(value => { if (value === 'closed' || value === 'error') fail(); });
    cleanup = () => { clearTimeout(deadline); clearInterval(interval); data.dispose(); state.dispose(); };
    if (connection.state === 'closed' || connection.state === 'error') fail();
  }
  const changed = ctx.api.settings.onChanged(() => { reset(); failures = 0; void connect(); });
  const owner = { dispose() { if (stopped) return; stopped = true; changed.dispose(); reset(); if (owners[ownerKey] === owner) delete owners[ownerKey]; } };
  owners[ownerKey] = owner;
  void connect(); return owner.dispose;
}
export default defineMainPlugin({ activate(ctx) { ctx.scope.defer(activateBridge(ctx)); } });
