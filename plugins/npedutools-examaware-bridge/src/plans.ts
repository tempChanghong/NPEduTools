import type { MainPluginContext, PreparedPlayerSource } from '@dsz-examaware/plugin-sdk';
import { createHash, randomUUID } from 'node:crypto';

export const MAX_PLAN_BYTES = 24 * 1024;
const activeStates = new Set(['preparing', 'opening', 'ready', 'closing']);
export type PlanCommand = { requestId: string; action: 'plan.prepare' | 'plan.start'; issuedAt: number;
  expiresAt: number; dataBase64?: string; preparationId?: string };
export type PlanSummary = { preparationId: string; sha256: string; examName: string; message: string;
  exams: { name: string; start: string; end: string; alertTime: number }[] };

// Scoped to an authenticated TCP session. A reconnect requires preparing the file again.
export function createPlanController(api: MainPluginContext['api'], alive: () => boolean) {
  let prepared: { source: PreparedPlayerSource; summary: PlanSummary } | undefined;
  let lastSessionId: string | undefined;
  const supported = () => typeof api.player?.prepare === 'function' && typeof api.player?.startFromConfig === 'function'
    && typeof api.player?.listSessions === 'function';
  async function observe() {
    try {
      const sessions = await api.player.listSessions();
      if (sessions.length > 512) throw new Error('too-many-sessions');
      const current = sessions.filter(x => activeStates.has(x.state));
      if (current.length > 8) throw new Error('too-many-active-sessions');
      const brief = (x: (typeof sessions)[number]) => ({ id: x.id, state: x.state, examName: (x.examName ?? '').slice(0, 160) });
      const last = sessions.find(x => x.id === lastSessionId);
      return { known: true, sessions: current.map(brief), ...(last ? { lastSession: brief(last) } : {}) };
    } catch { return { known: false, sessions: [] }; }
  }
  async function execute(command: PlanCommand) {
    const fail = (state: string) => ({ requestId: command.requestId, state });
    if (!supported()) return fail('Unsupported');
    if (!alive()) return fail('Expired');
    try {
      if (command.action === 'plan.prepare') {
        prepared = undefined;
        const data = Buffer.from(command.dataBase64 ?? '', 'base64');
        if (!data.length || data.length > MAX_PLAN_BYTES || data.toString('base64') !== command.dataBase64) return fail('Invalid');
        const json = new TextDecoder('utf-8', { fatal: true }).decode(data).replace(/^\uFEFF/, '');
        const source = await api.player.prepare({ kind: 'json', data: json }, { maxBytes: MAX_PLAN_BYTES });
        const c = source.config;
        if (!source.validation.valid || !c || !c.examName || c.examName.length > 160 || c.message.length > 2000 ||
            c.examInfos.length === 0 || c.examInfos.length > 32 || c.examInfos.some(x =>
              !x.name || x.name.length > 160 || x.start.length > 64 || x.end.length > 64 || !Number.isFinite(x.alertTime)) ||
            c.examName.length + c.message.length + c.examInfos.reduce((n, x) => n + x.name.length + x.start.length + x.end.length, 0) > 6000) return fail('Invalid');
        if (!alive()) return fail('Expired');
        const summary: PlanSummary = { preparationId: randomUUID(), sha256: createHash('sha256').update(data).digest('hex'),
          examName: c.examName, message: c.message, exams: c.examInfos.map(x => ({ name: x.name, start: x.start, end: x.end, alertTime: x.alertTime })) };
        prepared = { source, summary };
        return { requestId: command.requestId, state: 'Prepared', summary };
      }
      const plan = prepared;
      if (!plan || plan.summary.preparationId !== command.preparationId) return fail('Expired');
      // Consume before awaiting: neither double clicks nor a lost reply may launch twice.
      prepared = undefined;
      const players = await observe();
      if (!players.known) return fail('Unconfirmed');
      if (players.sessions.length) return fail('Busy');
      if (!alive() || Date.now() > command.expiresAt) return fail('Expired');
      const session = await api.player.startFromConfig(plan.source.config, { replaceExisting: false, waitForReady: false, maxBytes: MAX_PLAN_BYTES });
      lastSessionId = session.id;
      return { requestId: command.requestId, state: 'Started', sessionId: session.id };
    } catch (error) {
      const code = (error as { code?: string }).code;
      return fail(code === 'permission-denied' ? 'Denied' : code === 'conflict' ? 'Busy' :
        command.action === 'plan.prepare' ? 'Invalid' : 'Unconfirmed');
    }
  }
  return { supported, observe, execute };
}
