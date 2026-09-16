import { buildAad, newNonce, openJson, sealJson, type SessionKeys } from './crypto';
import { fromBase64Url } from './base64';

/** Типы и кодек протокола v1 — зеркало docs/protocol.md. */

export const PROTOCOL_VERSION = 1;
export const SERVER_ADDRESS = 'server';

export type MessageType = 'cmd' | 'res' | 'evt' | 'sys';

export interface Envelope {
  v: number;
  id: string;
  re?: string;
  type: MessageType;
  from: string;
  to: string;
  ts: number;
  enc?: { alg: string; n: string; c: string };
  sys?: string;
  data?: Record<string, unknown> | null;
}

export interface CmdPayload {
  cmd: string;
  args?: Record<string, unknown> | null;
  nonce: string;
  ts: number;
}

export interface ResPayload {
  ok: boolean;
  result?: Record<string, unknown> | null;
  error?: { code: string; message: string } | null;
  nonce: string;
  ts: number;
}

export interface EvtPayload {
  evt: string;
  data?: Record<string, unknown> | null;
  nonce: string;
  ts: number;
}

export const Commands = {
  StatusGet: 'status.get',
  PowerShutdown: 'power.shutdown',
  PowerReboot: 'power.reboot',
  PowerSleep: 'power.sleep',
  PowerHibernate: 'power.hibernate',
  PowerLock: 'power.lock',
  PowerCancel: 'power.cancel',
  ScenarioList: 'scenario.list',
  ScenarioGet: 'scenario.get',
  ScenarioSave: 'scenario.save',
  ScenarioDelete: 'scenario.delete',
  ScenarioRun: 'scenario.run',
  ScenarioCancel: 'scenario.cancel',
  AppsList: 'apps.list',
  ScheduleList: 'schedule.list',
  ScheduleSave: 'schedule.save',
  ScheduleDelete: 'schedule.delete',
  ScheduleSync: 'schedule.sync',
  ArriveSet: 'arrive.set',
  ArriveCancel: 'arrive.cancel',
  WizardRun: 'wizard.run',
  WizardFix: 'wizard.fix',
  WakeTest: 'wake.test',
  HistoryList: 'history.list',
  PairRevoke: 'pair.revoke',
  AgentInfo: 'agent.info',
  SettingsGet: 'agent.settings.get',
  SettingsSet: 'agent.settings.set'
} as const;

export const Events = {
  PowerState: 'power.state',
  PowerWake: 'power.wake',
  PowerCountdown: 'power.countdown',
  Status: 'status',
  ScenarioProgress: 'scenario.progress',
  ScenarioDone: 'scenario.done',
  WizardResult: 'wizard.result',
  WakeTestResult: 'wake.test.result',
  ScheduleFired: 'schedule.fired',
  Notify: 'notify',
  AgentHello: 'agent.hello'
} as const;

export const Sys = {
  Hello: 'hello',
  HelloOk: 'hello.ok',
  Ping: 'ping',
  Pong: 'pong',
  Heartbeat: 'heartbeat',
  PeerState: 'peer.state',
  RouteFail: 'route.fail',
  Error: 'error'
} as const;

export function unixNow(): number {
  return Math.floor(Date.now() / 1000);
}

function uuid(): string {
  // crypto.randomUUID есть не во всех рантаймах RN — собираем из случайных байт.
  const g = globalThis as { crypto?: { randomUUID?: () => string } };
  if (typeof g.crypto?.randomUUID === 'function') return g.crypto.randomUUID();

  const bytes = new Uint8Array(16);
  (globalThis as unknown as { crypto: Crypto }).crypto.getRandomValues(bytes);
  bytes[6] = (bytes[6]! & 0x0f) | 0x40;
  bytes[8] = (bytes[8]! & 0x3f) | 0x80;

  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/** Окно уже виденных nonce: чужой перехваченный ответ второй раз не пройдёт. */
export class ReplayGuard {
  private readonly seen = new Map<string, number>();

  constructor(
    private readonly windowSec = 600,
    private readonly clockSkewSec = 120
  ) {}

  check(nonce: string | undefined, ts: number): 'ok' | 'stale' | 'duplicate' | 'missing' {
    if (!nonce) return 'missing';
    const now = unixNow();
    if (Math.abs(now - ts) > this.clockSkewSec) return 'stale';

    for (const [key, expires] of this.seen) {
      if (expires <= now) this.seen.delete(key);
    }

    if (this.seen.has(nonce)) return 'duplicate';
    this.seen.set(nonce, now + this.windowSec);
    return 'ok';
  }
}

export class MessageCodec {
  private readonly replay = new ReplayGuard();

  constructor(
    private readonly selfId: string,
    private readonly keys: SessionKeys
  ) {}

  sealCommand(to: string, cmd: string, args?: Record<string, unknown>): Envelope {
    const envelope: Envelope = {
      v: PROTOCOL_VERSION,
      id: uuid(),
      type: 'cmd',
      from: this.selfId,
      to,
      ts: unixNow()
    };

    const payload: CmdPayload = { cmd, args: args ?? null, nonce: newNonce(), ts: envelope.ts };
    const aad = buildAad(envelope.v, envelope.id, envelope.from, envelope.to, envelope.ts);
    const { nonce, cipher } = sealJson(this.keys.sendKey, payload, aad);

    envelope.enc = { alg: 'aes-256-gcm', n: nonce, c: cipher };
    return envelope;
  }

  open<T extends ResPayload | EvtPayload>(envelope: Envelope): T {
    if (envelope.v !== PROTOCOL_VERSION) throw new Error('Неподдерживаемая версия протокола.');
    if (!envelope.enc) throw new Error('Конверт без зашифрованной части.');

    const aad = buildAad(envelope.v, envelope.id, envelope.from, envelope.to, envelope.ts);
    const payload = openJson<T>(this.keys.receiveKey, envelope.enc.n, envelope.enc.c, aad);

    const verdict = this.replay.check(payload.nonce, payload.ts);
    if (verdict !== 'ok') {
      throw new Error(
        verdict === 'duplicate'
          ? 'Повтор ранее полученного сообщения.'
          : verdict === 'stale'
            ? 'Слишком большое расхождение времени между телефоном и компьютером.'
            : 'В сообщении нет nonce.'
      );
    }

    return payload;
  }
}

export interface PairingOffer {
  v: number;
  pcId: string;
  pcName: string;
  pub: string;
  token: string;
  exp: number;
  relay?: string | null;
  lan: string[];
  mac?: string | null;
  fp?: string | null;
}

export function parsePairingUri(uri: string): PairingOffer | null {
  const index = uri.indexOf('d=');
  if (index < 0) return null;

  try {
    const json = new TextDecoder().decode(fromBase64Url(uri.slice(index + 2)));
    const offer = JSON.parse(json) as PairingOffer;
    if (!offer.pcId || !offer.pub || !offer.token) return null;
    return offer;
  } catch {
    return null;
  }
}

export function isOfferExpired(offer: PairingOffer): boolean {
  return unixNow() > offer.exp;
}
