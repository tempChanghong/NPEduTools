import { defineMainPlugin, type MainPluginContext, type TcpSocketHandle } from '@dsz-examaware/plugin-sdk';
import { createHmac, timingSafeEqual } from 'node:crypto';
import { pairing, type Pairing } from './pairing.js';

const sign = (key: string, text: string) => createHmac('sha256', Buffer.from(key, 'hex')).update(text).digest('hex');
const MAX = 65536;
export function activateBridge(ctx: MainPluginContext) {
  let stopped = false, generation = 0, socket: TcpSocketHandle | undefined;
  let retry: ReturnType<typeof setTimeout> | undefined;
  let cleanup: (() => void) | undefined;
  let failures = 0;

  function reset() {
    generation++;
    clearTimeout(retry);
    cleanup?.(); cleanup = undefined;
    socket?.dispose(); socket = undefined;
  }
  function schedule() {
    if (stopped) return;
    reset();
    retry = setTimeout(() => void connect(), Math.min(15000, 1000 * 2 ** Math.min(failures++, 4)));
  }
  async function connect() {
    if (stopped) return;
    const config = pairing(ctx.api.settings.get());
    if (!config) return;
    const current = generation;
    try {
      const candidate = await ctx.api.network.connectTcp({ host: config.host, port: config.port,
        allowLocalNetwork: true, timeoutMs: 3000 });
      if (stopped || current !== generation) { candidate.dispose(); return; }
      socket = candidate;
      startSession(candidate, config, current);
    } catch { if (!stopped && current === generation) schedule(); }
  }
  function startSession(connection: TcpSocketHandle, config: Pairing, current: number) {
    let buffer = Buffer.alloc(0), nonce: string | undefined, sequence = 0, sending = false;
    const active = () => !stopped && current === generation;
    const fail = () => { if (active()) schedule(); };
    const deadline = setTimeout(fail, 5000);
    const interval = setInterval(() => { if (nonce) void send(); }, 2000);
    async function send() {
      if (!nonce || sending || !active()) return;
      sending = true;
      try {
        const info = await ctx.api.app.info();
        let registered: boolean | null = null;
        try { registered = await ctx.api.app.getAutoStart(); } catch { /* Unknown, never false. */ }
        if (!active()) return;
        const payload = JSON.stringify({ name: info.name, version: info.version, platform: info.platform,
          packaged: info.packaged, autoStartRegistered: registered });
        const n = ++sequence;
        const body = Buffer.from(JSON.stringify({ version: 1, type: 'status', sequence: n, payload,
          proof: sign(config.key, `peer\n${nonce}\n${n}\n${payload}`) }));
        if (body.length > MAX) throw new Error('frame');
        const frame = Buffer.alloc(body.length + 4); frame.writeInt32LE(body.length); body.copy(frame, 4);
        await connection.write(frame);
        failures = 0;
      } catch { fail(); }
      finally { sending = false; }
    }
    const data = connection.onData(chunk => {
      if (!active()) return;
      try {
        // Host sends exactly one hello. Any subsequent command is outside this read-only protocol.
        if (nonce || buffer.length + chunk.length > MAX + 4) throw new Error('frame');
        buffer = Buffer.concat([buffer, chunk]);
        if (buffer.length < 4) return;
        const length = buffer.readInt32LE();
        if (length <= 0 || length > MAX) throw new Error('length');
        if (buffer.length < length + 4) return;
        if (buffer.length !== length + 4) throw new Error('extra');
        const hello = JSON.parse(buffer.subarray(4).toString('utf8'));
        if (hello.version !== 1 || hello.type !== 'hello' || typeof hello.nonce !== 'string' ||
          !/^[a-f0-9]{64}$/.test(hello.nonce) || typeof hello.proof !== 'string' || !/^[a-f0-9]{64}$/.test(hello.proof) ||
          !timingSafeEqual(Buffer.from(hello.proof, 'hex'), Buffer.from(sign(config.key, `host\n${hello.nonce}`), 'hex'))) throw new Error('auth');
        nonce = hello.nonce; buffer = Buffer.alloc(0); clearTimeout(deadline);
        void send();
      } catch { fail(); }
    });
    const state = connection.onStateChanged(value => { if (value === 'closed' || value === 'error') fail(); });
    cleanup = () => { clearTimeout(deadline); clearInterval(interval); data.dispose(); state.dispose(); };
    if (connection.state === 'closed' || connection.state === 'error') fail();
  }
  const changed = ctx.api.settings.onChanged(() => { reset(); failures = 0; void connect(); });
  void connect();
  return () => { stopped = true; changed.dispose(); reset(); };
}

export default defineMainPlugin({ activate(ctx) { ctx.scope.defer(activateBridge(ctx)); } });
