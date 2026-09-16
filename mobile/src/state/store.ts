import { create } from 'zustand';
import {
  deriveSessionKeys,
  fromBase64Url,
  pairFingerprint,
  toBase64Url,
  type SessionKeys
} from '../lib/crypto';
import { Commands, Events, type EvtPayload, type PairingOffer, isOfferExpired } from '../lib/protocol';
import { PcConnection, type ConnectionInfo } from '../lib/transport';
import * as api from '../lib/api';
import * as storage from '../lib/storage';
import { sendMagicPacket, isOnWifi, getPhoneIp, sameSubnet24 } from '../lib/wol';
import { setLanguage } from '../i18n';
import type {
  AppEntry,
  CheckResult,
  EventRecord,
  PairedPc,
  Scenario,
  ScenarioRunReport,
  Schedule,
  StatusSnapshot
} from '../lib/types';

/** Соединение живёт вне реактивного состояния: это объект с сокетом, а не данные. */
let connection: PcConnection | null = null;
let unsubscribers: (() => void)[] = [];

export interface Toast {
  text: string;
  level: 'info' | 'warn' | 'error' | 'success';
}

export interface CountdownState {
  action: string;
  secondsLeft: number;
  cancellable: boolean;
}

interface AppState {
  ready: boolean;
  deviceId: string;
  publicKey: Uint8Array | null;
  privateKey: Uint8Array | null;

  pcs: PairedPc[];
  activePcId: string | null;
  connection: ConnectionInfo;

  status: StatusSnapshot | null;
  scenarios: Scenario[];
  schedules: Schedule[];
  checks: CheckResult[];
  events: EventRecord[];
  apps: AppEntry[];
  lastReport: ScenarioRunReport | null;
  countdown: CountdownState | null;
  arriveAt: string | null;

  settings: storage.AppSettings;
  busy: string | null;
  toast: Toast | null;

  init: () => Promise<void>;
  setToast: (toast: Toast | null) => void;

  selectPc: (pcId: string) => Promise<void>;
  connect: () => Promise<void>;
  disconnect: () => void;

  command: <T = Record<string, unknown>>(cmd: string, args?: Record<string, unknown>) => Promise<T>;

  refreshAll: () => Promise<void>;
  refreshStatus: () => Promise<void>;
  refreshScenarios: () => Promise<void>;
  refreshSchedules: () => Promise<void>;
  refreshEvents: () => Promise<void>;
  loadApps: (refresh?: boolean) => Promise<AppEntry[]>;

  power: (action: 'shutdown' | 'reboot' | 'sleep' | 'hibernate' | 'lock', force?: boolean) => Promise<void>;
  cancelPower: () => Promise<void>;
  wake: () => Promise<{ ok: boolean; message: string }>;

  runWizard: () => Promise<void>;
  fixCheck: (id: string) => Promise<void>;
  startWakeTest: (mode: 'wol' | 'timer') => Promise<void>;

  saveScenario: (scenario: Scenario) => Promise<void>;
  deleteScenario: (id: string) => Promise<void>;
  runScenario: (id: string) => Promise<void>;

  saveSchedule: (schedule: Schedule) => Promise<void>;
  deleteSchedule: (id: string) => Promise<void>;

  setArrive: (minutes: number, scenarioId?: string | null) => Promise<void>;
  cancelArrive: () => Promise<void>;

  pair: (offer: PairingOffer, phoneName: string) => Promise<PairedPc>;
  forgetPc: (pcId: string) => Promise<void>;

  updateSettings: (patch: Partial<storage.AppSettings>) => Promise<void>;
}

export const useStore = create<AppState>((set, get) => ({
  ready: false,
  deviceId: '',
  publicKey: null,
  privateKey: null,

  pcs: [],
  activePcId: null,
  connection: { state: 'idle', kind: null },

  status: null,
  scenarios: [],
  schedules: [],
  checks: [],
  events: [],
  apps: [],
  lastReport: null,
  countdown: null,
  arriveAt: null,

  settings: storage.defaultSettings,
  busy: null,
  toast: null,

  // ---------------- Инициализация ----------------

  init: async () => {
    const [identity, pcs, settings, activePcId] = await Promise.all([
      storage.loadIdentity(),
      storage.loadPairedPcs(),
      storage.loadSettings(),
      storage.getActivePcId()
    ]);

    setLanguage(settings.language);

    const active = activePcId && pcs.some((p) => p.pcId === activePcId) ? activePcId : (pcs[0]?.pcId ?? null);

    set({
      ready: true,
      deviceId: identity.deviceId,
      privateKey: identity.privateKey,
      publicKey: identity.publicKey,
      pcs,
      settings,
      activePcId: active
    });

    if (active) {
      const cached = await storage.loadCachedStatus<StatusSnapshot>(active);
      if (cached) set({ status: cached });
      await get().connect();
    }
  },

  setToast: (toast) => set({ toast }),

  // ---------------- Соединение ----------------

  selectPc: async (pcId) => {
    if (get().activePcId === pcId) return;

    get().disconnect();
    await storage.setActivePcId(pcId);
    const cached = await storage.loadCachedStatus<StatusSnapshot>(pcId);

    set({
      activePcId: pcId,
      status: cached,
      scenarios: [],
      schedules: [],
      checks: [],
      events: [],
      apps: []
    });

    await get().connect();
  },

  connect: async () => {
    const { activePcId, pcs, privateKey, deviceId, settings } = get();
    if (!activePcId || !privateKey) return;

    const pc = pcs.find((p) => p.pcId === activePcId);
    if (!pc) return;

    get().disconnect();

    let keys: SessionKeys;
    try {
      keys = deriveSessionKeys(privateKey, fromBase64Url(pc.pcPub), pc.pairId);
    } catch (error) {
      set({ connection: { state: 'offline', kind: null, error: (error as Error).message } });
      return;
    }

    const relayToken = pc.relayUrl ? await storage.getRelayToken(pc.relayUrl) : null;
    const link = new PcConnection(deviceId, pc, keys, relayToken, settings.preferLan);
    connection = link;

    unsubscribers.push(
      link.onStateChange((info) => {
        set({ connection: info });
        if (info.state === 'connected') void get().refreshAll();
      })
    );
    unsubscribers.push(link.onEvent((evt) => handleEvent(evt, set, get)));

    await link.connect();
  },

  disconnect: () => {
    for (const off of unsubscribers) off();
    unsubscribers = [];
    connection?.close();
    connection = null;
    set({ connection: { state: 'idle', kind: null } });
  },

  command: async <T = Record<string, unknown>>(cmd: string, args?: Record<string, unknown>): Promise<T> => {
    if (!connection || !connection.isConnected) throw new Error('Нет связи с компьютером.');
    return connection.send<T>(cmd, args);
  },

  // ---------------- Данные ----------------

  refreshAll: async () => {
    await Promise.allSettled([
      get().refreshStatus(),
      get().refreshScenarios(),
      get().refreshSchedules(),
      get().refreshEvents()
    ]);
  },

  refreshStatus: async () => {
    const status = await get().command<StatusSnapshot>(Commands.StatusGet);
    set({ status, countdown: mapCountdown(status) });
    const pcId = get().activePcId;
    if (pcId) await storage.cacheStatus(pcId, status);
  },

  refreshScenarios: async () => {
    const result = await get().command<{ scenarios: Scenario[] }>(Commands.ScenarioList);
    set({ scenarios: result.scenarios ?? [] });
  },

  refreshSchedules: async () => {
    const result = await get().command<{ schedules: Schedule[] }>(Commands.ScheduleList);
    set({ schedules: result.schedules ?? [] });
  },

  refreshEvents: async () => {
    const result = await get().command<{ events: EventRecord[] }>(Commands.HistoryList, { limit: 100 });
    set({ events: (result.events ?? []).slice().reverse() });
  },

  loadApps: async (refresh = false) => {
    set({ busy: 'apps' });
    try {
      const result = await get().command<{ apps: AppEntry[] }>(Commands.AppsList, { refresh });
      const apps = result.apps ?? [];
      set({ apps });
      return apps;
    } finally {
      set({ busy: null });
    }
  },

  // ---------------- Питание ----------------

  power: async (action, force = false) => {
    const command =
      action === 'shutdown'
        ? Commands.PowerShutdown
        : action === 'reboot'
          ? Commands.PowerReboot
          : action === 'sleep'
            ? Commands.PowerSleep
            : action === 'hibernate'
              ? Commands.PowerHibernate
              : Commands.PowerLock;

    set({ busy: action });
    try {
      await get().command(command, force ? { force: true } : undefined);
      set({ toast: { text: 'Команда отправлена', level: 'success' } });
    } catch (error) {
      set({ toast: { text: (error as Error).message, level: 'error' } });
      throw error;
    } finally {
      set({ busy: null });
    }
  },

  cancelPower: async () => {
    await get().command(Commands.PowerCancel);
    set({ countdown: null, toast: { text: 'Действие отменено', level: 'info' } });
  },

  /**
   * Включение: если телефон в домашней сети — шлём магический пакет сами,
   * иначе просим сервер разбудить ПК через мост.
   */
  wake: async () => {
    const { activePcId, pcs } = get();
    const pc = pcs.find((p) => p.pcId === activePcId);
    if (!pc) return { ok: false, message: 'Компьютер не подключён.' };
    if (!pc.mac) return { ok: false, message: 'У компьютера не сохранён MAC-адрес.' };

    set({ busy: 'wake' });
    try {
      const onWifi = await isOnWifi();
      const phoneIp = await getPhoneIp();
      const nearby = pc.lanIps.some((ip) => sameSubnet24(ip.split(':')[0] ?? ip, phoneIp));

      if (onWifi && nearby) {
        const result = await sendMagicPacket(pc.mac, pc.lanIps[0]?.split(':')[0] ?? null);
        if (result.sent) {
          void waitForWake(get, set);
          return { ok: true, message: 'Магический пакет отправлен. Ждём компьютер…' };
        }

        // Не получилось напрямую — пробуем через мост.
        const viaBridge = await wakeViaBridge(pc);
        return viaBridge ?? { ok: false, message: result.reason ?? 'Не удалось отправить пакет.' };
      }

      const viaBridge = await wakeViaBridge(pc);
      if (viaBridge) {
        if (viaBridge.ok) void waitForWake(get, set);
        return viaBridge;
      }

      return {
        ok: false,
        message:
          'Телефон не в домашней сети, а мост пробуждения недоступен. Включение возможно только из дома или через мост.'
      };
    } finally {
      set({ busy: null });
    }
  },

  // ---------------- Мастер готовности ----------------

  runWizard: async () => {
    set({ busy: 'wizard' });
    try {
      const result = await connection!.send<{ checks: CheckResult[] }>(Commands.WizardRun, undefined, 180000);
      set({ checks: result.checks ?? [] });
    } finally {
      set({ busy: null });
    }
  },

  fixCheck: async (id) => {
    set({ busy: `fix:${id}` });
    try {
      const result = await connection!.send<{ check: CheckResult }>(Commands.WizardFix, { id }, 120000);
      if (result.check) {
        set({ checks: get().checks.map((c) => (c.id === id ? result.check : c)) });
      }
    } finally {
      set({ busy: null });
    }
  },

  startWakeTest: async (mode) => {
    await get().command(Commands.WakeTest, { mode, sleepSeconds: 120 });
    set({ toast: { text: 'Тест запущен — компьютер скоро уснёт.', level: 'info' } });
  },

  // ---------------- Сценарии ----------------

  saveScenario: async (scenario) => {
    await get().command(Commands.ScenarioSave, { scenario });
    await get().refreshScenarios();
  },

  deleteScenario: async (id) => {
    await get().command(Commands.ScenarioDelete, { id });
    set({ scenarios: get().scenarios.filter((s) => s.id !== id) });
  },

  runScenario: async (id) => {
    set({ busy: `scenario:${id}`, lastReport: null });
    try {
      await get().command(Commands.ScenarioRun, { id });
      set({ toast: { text: 'Сценарий запущен', level: 'success' } });
    } catch (error) {
      set({ toast: { text: (error as Error).message, level: 'error' } });
    } finally {
      set({ busy: null });
    }
  },

  // ---------------- Расписания ----------------

  saveSchedule: async (schedule) => {
    const result = await get().command<{ id: string; synced: boolean }>(Commands.ScheduleSave, { schedule });
    await get().refreshSchedules();

    if (!result.synced) {
      set({ toast: { text: 'Расписание сохранено, таймер будет создан при следующем подключении.', level: 'warn' } });
    }

    // Дублируем на сервер: расписание должно пережить переустановку агента.
    const pc = get().pcs.find((p) => p.pcId === get().activePcId);
    if (pc?.relayUrl) {
      const token = await storage.getRelayToken(pc.relayUrl);
      if (token) {
        await api.pushScheduleToRelay(pc.relayUrl, token, pc.pcId, schedule).catch(() => undefined);
      }
    }
  },

  deleteSchedule: async (id) => {
    await get().command(Commands.ScheduleDelete, { id });
    set({ schedules: get().schedules.filter((s) => s.id !== id) });

    const pc = get().pcs.find((p) => p.pcId === get().activePcId);
    if (pc?.relayUrl) {
      const token = await storage.getRelayToken(pc.relayUrl);
      if (token) await api.deleteScheduleFromRelay(pc.relayUrl, token, pc.pcId, id).catch(() => undefined);
    }
  },

  // ---------------- «Буду через N минут» ----------------

  setArrive: async (minutes, scenarioId) => {
    const result = await get().command<{ wakeAt: string }>(Commands.ArriveSet, {
      minutes,
      scenarioId: scenarioId ?? undefined
    });
    set({ arriveAt: result.wakeAt, toast: { text: 'Таймер поставлен', level: 'success' } });
  },

  cancelArrive: async () => {
    await get().command(Commands.ArriveCancel);
    set({ arriveAt: null });
  },

  // ---------------- Сопряжение ----------------

  pair: async (offer, phoneName) => {
    if (isOfferExpired(offer)) throw new Error('Код истёк. Откройте новый QR-код на компьютере.');

    const { deviceId, publicKey, privateKey } = get();
    if (!publicKey || !privateKey) throw new Error('Ключи телефона не готовы.');

    const phonePub = toBase64Url(publicKey);
    const body = { token: offer.token, phoneId: deviceId, phonePub, phoneName };

    let result: api.PairClaimResult | null = null;
    let lastError: string | null = null;

    // 1. Пробуем напрямую: это работает даже без интернета и без сервера.
    for (const address of offer.lan) {
      try {
        const candidate = await api.claimPairingViaLan(address, body);
        if (candidate.ok) {
          result = candidate;
          break;
        }
        lastError = candidate.error ?? null;
      } catch (error) {
        lastError = (error as Error).message;
      }
    }

    // 2. Иначе — через сервер-ретранслятор.
    if (!result && offer.relay) {
      const existing = await storage.getRelayToken(offer.relay);
      let token = existing;

      if (!token) {
        const registration = await api.registerDevice(offer.relay, {
          deviceId,
          name: phoneName,
          pub: phonePub
        });
        token = registration.deviceToken;
        await storage.setRelayToken(offer.relay, token);
      }

      const candidate = await api.claimPairingViaRelay(offer.relay, body);
      if (candidate.ok) result = candidate;
      else lastError = candidate.error ?? lastError;
    }

    if (!result) {
      throw new Error(lastError ?? 'Не удалось подключиться ни напрямую, ни через сервер.');
    }

    const pcPub = result.pcPub ?? offer.pub;
    const fingerprint = pairFingerprint(publicKey, fromBase64Url(pcPub));

    const lanPort = Number.parseInt(offer.lan[0]?.split(':')[1] ?? '8760', 10);
    const pc: PairedPc = {
      pcId: result.pcId ?? offer.pcId,
      pcName: result.pcName ?? offer.pcName,
      pairId: result.pairId,
      pcPub,
      mac: result.mac ?? offer.mac ?? null,
      lanIps: (result.lanIps ?? offer.lan.map((a) => a.split(':')[0] ?? a)).filter(Boolean),
      lanPort: Number.isFinite(lanPort) ? lanPort : 8760,
      relayUrl: offer.relay ?? null,
      fingerprint,
      pairedAt: new Date().toISOString()
    };

    const pcs = await storage.upsertPairedPc(pc);
    await storage.setActivePcId(pc.pcId);
    set({ pcs, activePcId: pc.pcId });
    await get().connect();

    return pc;
  },

  forgetPc: async (pcId) => {
    if (get().activePcId === pcId) get().disconnect();

    try {
      if (connection?.isConnected) await get().command(Commands.PairRevoke, { phoneId: get().deviceId });
    } catch {
      // ПК может быть недоступен — локальную запись всё равно удаляем.
    }

    const pcs = await storage.removePairedPc(pcId);
    const nextActive = pcs[0]?.pcId ?? null;
    await storage.setActivePcId(nextActive);

    set({ pcs, activePcId: nextActive, status: null, scenarios: [], schedules: [], checks: [], events: [] });
    if (nextActive) await get().connect();
  },

  // ---------------- Настройки ----------------

  updateSettings: async (patch) => {
    const settings = { ...get().settings, ...patch };
    await storage.saveSettings(settings);
    setLanguage(settings.language);
    set({ settings });

    if (patch.preferLan !== undefined && get().activePcId) {
      await get().connect();
    }
  }
}));

// ---------------- Вспомогательное ----------------

function mapCountdown(status: StatusSnapshot): CountdownState | null {
  if (!status.pendingPower) return null;
  return {
    action: status.pendingPower.action,
    secondsLeft: status.pendingPower.secondsLeft,
    cancellable: status.pendingPower.cancellable
  };
}

type SetState = (partial: Partial<AppState>) => void;
type GetState = () => AppState;

function handleEvent(evt: EvtPayload, set: SetState, get: GetState): void {
  const data = (evt.data ?? {}) as Record<string, unknown>;

  switch (evt.evt) {
    case Events.Status:
      set({ status: data as unknown as StatusSnapshot });
      break;

    case Events.PowerState: {
      const status = get().status;
      if (status) set({ status: { ...status, state: String(data.state ?? status.state) as StatusSnapshot['state'] } });
      if (data.state === 'running') set({ countdown: null });
      break;
    }

    case Events.PowerCountdown: {
      if (data.cancelled === true || Number(data.secondsLeft ?? 0) <= 0) {
        set({ countdown: null });
      } else {
        set({
          countdown: {
            action: String(data.action ?? ''),
            secondsLeft: Number(data.secondsLeft ?? 0),
            cancellable: data.cancellable !== false
          }
        });
      }
      break;
    }

    case Events.PowerWake:
      set({ toast: { text: 'Компьютер проснулся', level: 'success' } });
      void get().refreshStatus().catch(() => undefined);
      break;

    case Events.ScenarioDone: {
      const report = data as unknown as ScenarioRunReport;
      set({
        lastReport: report,
        toast: {
          text: report.ok
            ? `Сценарий «${report.name}» выполнен`
            : `Сценарий «${report.name}» завершился с ошибкой`,
          level: report.ok ? 'success' : 'warn'
        }
      });
      break;
    }

    case Events.WizardResult:
      set({ checks: (data.checks as CheckResult[]) ?? [] });
      break;

    case Events.WakeTestResult:
      set({
        toast: {
          text: data.ok === true ? 'Тест пробуждения пройден' : 'Тест пробуждения не пройден',
          level: data.ok === true ? 'success' : 'error'
        }
      });
      break;

    case Events.ScheduleFired:
      void get().refreshSchedules().catch(() => undefined);
      break;

    case Events.Notify:
      set({
        toast: {
          text: String(data.text ?? ''),
          level: (data.level as Toast['level']) ?? 'info'
        }
      });
      break;

    default:
      break;
  }
}

async function wakeViaBridge(pc: PairedPc): Promise<{ ok: boolean; message: string } | null> {
  if (!pc.relayUrl) return null;

  const token = await storage.getRelayToken(pc.relayUrl);
  if (!token) return null;

  try {
    const result = await api.requestWake(pc.relayUrl, token, pc.pcId);
    return { ok: result.ok, message: result.message ?? (result.ok ? 'Пакет отправлен.' : 'Не удалось включить.') };
  } catch (error) {
    return { ok: false, message: (error as Error).message };
  }
}

/** После отправки пакета ждём, пока агент выйдет на связь (до 90 секунд). */
async function waitForWake(get: GetState, set: SetState): Promise<void> {
  const deadline = Date.now() + 90000;

  while (Date.now() < deadline) {
    await new Promise((resolve) => setTimeout(resolve, 5000));
    if (get().connection.state === 'connected') {
      set({ toast: { text: 'Компьютер вышел на связь', level: 'success' } });
      return;
    }
    await get().connect().catch(() => undefined);
  }

  set({
    toast: {
      text: 'Компьютер не ответил за 90 секунд. Откройте мастер готовности и проверьте Wake-on-LAN.',
      level: 'warn'
    }
  });
}

export function activePc(state: AppState): PairedPc | null {
  return state.pcs.find((p) => p.pcId === state.activePcId) ?? null;
}
