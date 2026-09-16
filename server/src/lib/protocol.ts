/**
 * Типы протокола v1 — зеркало docs/protocol.md и PcMate.Core/Protocol.
 * Сервер не расшифровывает enc: он видит только маршрутные поля.
 */

export const PROTOCOL_VERSION = 1;
export const SERVER_ADDRESS = 'server';
export const BROADCAST_ADDRESS = '*';

export type MessageType = 'cmd' | 'res' | 'evt' | 'sys';
export type DeviceRole = 'pc' | 'phone' | 'bridge';

export interface EncBlock {
  alg: string;
  n: string;
  c: string;
}

export interface Envelope {
  v: number;
  id: string;
  re?: string;
  type: MessageType;
  from: string;
  to: string;
  ts: number;
  enc?: EncBlock;
  sys?: string;
  data?: Record<string, unknown> | null;
}

export const Sys = {
  Hello: 'hello',
  HelloOk: 'hello.ok',
  Ping: 'ping',
  Pong: 'pong',
  Heartbeat: 'heartbeat',
  PeerState: 'peer.state',
  RouteFail: 'route.fail',
  PairOffer: 'pair.offer',
  PairClaim: 'pair.claim',
  PairResult: 'pair.result',
  WolRequest: 'wol.request',
  WolResult: 'wol.result',
  BridgePing: 'bridge.ping',
  BridgePong: 'bridge.pong',
  SchedulePush: 'schedule.push',
  Error: 'error'
} as const;

export const PowerState = {
  Running: 'running',
  Sleeping: 'sleeping',
  Hibernating: 'hibernating',
  ShuttingDown: 'shutting-down',
  Locked: 'locked',
  Off: 'off',
  Unknown: 'unknown'
} as const;

export type PowerStateValue = (typeof PowerState)[keyof typeof PowerState];

export interface PeerState {
  deviceId: string;
  role: DeviceRole;
  name: string | null;
  online: boolean;
  state: string;
  lastSeen: string | null;
  mac?: string | null;
  lanIps?: string[] | null;
}

export function sysEnvelope(sys: string, data: Record<string, unknown> | null, to: string): Envelope {
  return {
    v: PROTOCOL_VERSION,
    id: crypto.randomUUID(),
    type: 'sys',
    from: SERVER_ADDRESS,
    to,
    ts: Math.floor(Date.now() / 1000),
    sys,
    data
  };
}

/** Строгая проверка входящего кадра: всё, что не подходит, отбрасываем до маршрутизации. */
export function parseEnvelope(raw: string): Envelope | null {
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }

  if (typeof parsed !== 'object' || parsed === null) return null;
  const e = parsed as Record<string, unknown>;

  if (typeof e.id !== 'string' || e.id.length === 0 || e.id.length > 64) return null;
  if (typeof e.from !== 'string' || typeof e.to !== 'string') return null;
  if (typeof e.ts !== 'number' || !Number.isFinite(e.ts)) return null;
  if (typeof e.type !== 'string' || !['cmd', 'res', 'evt', 'sys'].includes(e.type)) return null;
  if (typeof e.v !== 'number') return null;

  return parsed as Envelope;
}

export function isEncrypted(envelope: Envelope): boolean {
  return envelope.type !== 'sys' && !!envelope.enc;
}
