export interface Pairing { version: number; host: string; port: number; key: string }
export function pairing(value: unknown): Pairing | undefined {
  if (!value || typeof value !== 'object') return;
  const p = value as Partial<Pairing>;
  if (p.version !== 2 || p.host !== '127.0.0.1' || !Number.isInteger(p.port) ||
      p.port! < 1024 || p.port! > 65535 || typeof p.key !== 'string' || !/^[a-f0-9]{64}$/.test(p.key)) return;
  return { version: 2, host: p.host, port: p.port!, key: p.key };
}
