import { MessageCodec, type Envelope, type EvtPayload, type ResPayload } from './protocol';
import type { SessionKeys } from './crypto';
import { probeAgent, wsUrl } from './api';
import type { PairedPc } from './types';
import { sameSubnet24, getPhoneIp } from './wol';

/**
 * Связь телефона с ПК. Два пути к одному и тому же агенту:
 *
 *   1. локальная сеть — WebSocket прямо на компьютер (работает без интернета);
 *   2. сервер-ретранслятор — из любой точки мира.
 *
 * Шифрование в обоих случаях одинаковое и сквозное: сервер лишь пересылает конверты.
 */

export type TransportKind = 'lan' | 'relay';
export type ConnectionState = 'idle' | 'connecting' | 'connected' | 'offline';

export interface ConnectionInfo {
  state: ConnectionState;
  kind: TransportKind | null;
  error?: string | null;
}

type EventListener = (evt: EvtPayload) => void;
type StateListener = (info: ConnectionInfo) => void;

interface PendingRequest {
  resolve: (payload: ResPayload) => void;
  reject: (error: Error) => void;
  timer: ReturnType<typeof setTimeout>;
}

export class PcConnection {
  private socket: WebSocket | null = null;
  private codec: MessageCodec;
  private readonly pending = new Map<string, PendingRequest>();
  private readonly eventListeners = new Set<EventListener>();
  private readonly stateListeners = new Set<StateListener>();

  private info: ConnectionInfo = { state: 'idle', kind: null };
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private reconnectDelayMs = 1000;
  private closedByUser = false;

  constructor(
    private readonly phoneId: string,
    private readonly pc: PairedPc,
    keys: SessionKeys,
    private readonly relayToken: string | null,
    private readonly preferLan: boolean
  ) {
    this.codec = new MessageCodec(phoneId, keys);
  }

  get connectionInfo(): ConnectionInfo {
    return this.info;
  }

  get isConnected(): boolean {
    return this.info.state === 'connected';
  }

  onEvent(listener: EventListener): () => void {
    this.eventListeners.add(listener);
    return () => this.eventListeners.delete(listener);
  }

  onStateChange(listener: StateListener): () => void {
    this.stateListeners.add(listener);
    listener(this.info);
    return () => this.stateListeners.delete(listener);
  }

  async connect(): Promise<void> {
    this.closedByUser = false;
    if (this.socket && this.socket.readyState <= WebSocket.OPEN) return;

    this.setState({ state: 'connecting', kind: null });

    const target = await this.pickTarget();
    if (!target) {
      this.setState({
        state: 'offline',
        kind: null,
        error: this.relayToken
          ? 'Компьютер не отвечает ни в локальной сети, ни через сервер.'
          : 'Компьютер не найден в локальной сети, а сервер-ретранслятор не настроен.'
      });
      this.scheduleReconnect();
      return;
    }

    try {
      const socket = new WebSocket(target.url);
      this.socket = socket;

      socket.onopen = () => {
        this.reconnectDelayMs = 1000;
        this.setState({ state: 'connected', kind: target.kind, error: null });
      };

      socket.onmessage = (event) => this.handleMessage(String(event.data));

      socket.onerror = () => {
        this.setState({ state: 'offline', kind: target.kind, error: 'Соединение прервано.' });
      };

      socket.onclose = () => {
        this.socket = null;
        this.failAllPending(new Error('Соединение с компьютером закрыто.'));
        if (!this.closedByUser) {
          this.setState({ state: 'offline', kind: null, error: this.info.error ?? null });
          this.scheduleReconnect();
        } else {
          this.setState({ state: 'idle', kind: null });
        }
      };
    } catch (error) {
      this.setState({ state: 'offline', kind: null, error: (error as Error).message });
      this.scheduleReconnect();
    }
  }

  close(): void {
    this.closedByUser = true;
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    this.failAllPending(new Error('Соединение закрыто.'));
    try {
      this.socket?.close();
    } catch {
      // уже закрыт
    }
    this.socket = null;
    this.setState({ state: 'idle', kind: null });
  }

  /** Отправляет команду и ждёт ответ ПК. */
  async send<T = Record<string, unknown>>(
    cmd: string,
    args?: Record<string, unknown>,
    timeoutMs = 20000
  ): Promise<T> {
    if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
      throw new Error('Нет связи с компьютером.');
    }

    const envelope = this.codec.sealCommand(this.pc.pcId, cmd, args);

    return new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(envelope.id);
        reject(new Error('Компьютер не ответил вовремя.'));
      }, timeoutMs);

      this.pending.set(envelope.id, {
        timer,
        resolve: (payload) => {
          if (payload.ok) resolve((payload.result ?? {}) as T);
          else reject(new Error(payload.error?.message ?? 'Команда не выполнена.'));
        },
        reject
      });

      try {
        this.socket!.send(JSON.stringify(envelope));
      } catch (error) {
        clearTimeout(timer);
        this.pending.delete(envelope.id);
        reject(error as Error);
      }
    });
  }

  private handleMessage(raw: string): void {
    let envelope: Envelope;
    try {
      envelope = JSON.parse(raw) as Envelope;
    } catch {
      return;
    }

    if (envelope.type === 'sys') {
      // Служебные сообщения сервера: состояние соседей, ошибки маршрутизации.
      if (envelope.sys === 'route.fail') {
        const reason = String((envelope.data as { reason?: string } | undefined)?.reason ?? '');
        this.setState({
          ...this.info,
          error:
            reason === 'offline'
              ? 'Компьютер сейчас не на связи с сервером.'
              : 'Сервер отказался доставить команду.'
        });
      }
      return;
    }

    if (envelope.type === 'res') {
      const pending = envelope.re ? this.pending.get(envelope.re) : undefined;
      try {
        const payload = this.codec.open<ResPayload>(envelope);
        if (pending && envelope.re) {
          clearTimeout(pending.timer);
          this.pending.delete(envelope.re);
          pending.resolve(payload);
        }
      } catch (error) {
        if (pending && envelope.re) {
          clearTimeout(pending.timer);
          this.pending.delete(envelope.re);
          pending.reject(error as Error);
        }
      }
      return;
    }

    if (envelope.type === 'evt') {
      try {
        const payload = this.codec.open<EvtPayload>(envelope);
        for (const listener of this.eventListeners) listener(payload);
      } catch {
        // Не расшифровалось — сообщение не от нашего ПК, игнорируем.
      }
    }
  }

  /** Локальная сеть быстрее и не зависит от интернета, поэтому пробуем её первой. */
  private async pickTarget(): Promise<{ url: string; kind: TransportKind } | null> {
    if (this.preferLan) {
      const lan = await this.findLanAddress();
      if (lan) return { url: `ws://${lan}/ws?phoneId=${encodeURIComponent(this.phoneId)}`, kind: 'lan' };
    }

    const relay = this.pc.relayUrl;
    if (relay && this.relayToken) {
      return { url: wsUrl(relay, this.relayToken), kind: 'relay' };
    }

    if (!this.preferLan) {
      const lan = await this.findLanAddress();
      if (lan) return { url: `ws://${lan}/ws?phoneId=${encodeURIComponent(this.phoneId)}`, kind: 'lan' };
    }

    return null;
  }

  private async findLanAddress(): Promise<string | null> {
    if (this.pc.lanIps.length === 0) return null;

    const phoneIp = await getPhoneIp();
    const port = this.pc.lanPort || 8760;

    // Сначала адреса из той же подсети — остальные всё равно недостижимы.
    const ordered = [...this.pc.lanIps].sort((a, b) => {
      const aNear = sameSubnet24(a, phoneIp) ? 0 : 1;
      const bNear = sameSubnet24(b, phoneIp) ? 0 : 1;
      return aNear - bNear;
    });

    for (const ip of ordered) {
      const address = ip.includes(':') ? ip : `${ip}:${port}`;
      try {
        const health = await probeAgent(address);
        if (health.ok && health.pcId === this.pc.pcId) return address;
      } catch {
        // Следующий адрес.
      }
    }

    return null;
  }

  private scheduleReconnect(): void {
    if (this.closedByUser || this.reconnectTimer) return;

    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      void this.connect();
    }, this.reconnectDelayMs);

    this.reconnectDelayMs = Math.min(this.reconnectDelayMs * 2, 30000);
  }

  private failAllPending(error: Error): void {
    for (const [, pending] of this.pending) {
      clearTimeout(pending.timer);
      pending.reject(error);
    }
    this.pending.clear();
  }

  private setState(info: ConnectionInfo): void {
    this.info = info;
    for (const listener of this.stateListeners) listener(info);
  }
}
