import { WebSocketServer, WebSocket, type RawData } from 'ws';
import type { IncomingMessage, Server } from 'node:http';
import { config } from '../config.js';
import { extractBearer } from '../lib/auth.js';
import { Store, toPeerState, type DeviceRow } from '../db/database.js';
import {
  parseEnvelope,
  sysEnvelope,
  Sys,
  PowerState,
  type Envelope,
  type DeviceRole
} from '../lib/protocol.js';
import { logger } from '../lib/logger.js';

interface Connection {
  socket: WebSocket;
  device: DeviceRow;
  role: DeviceRole;
  state: string;
  lastSeen: number;
  alive: boolean;
}

type PendingResolver = (envelope: Envelope) => void;

/**
 * Центральный узел маршрутизации. Сервер пересылает зашифрованные конверты
 * между спаренными устройствами и обслуживает служебные sys-сообщения.
 * Содержимое команд он расшифровать не может — и не должен.
 */
export class Hub {
  private readonly connections = new Map<string, Connection>();
  private readonly pending = new Map<string, PendingResolver>();
  private wss?: WebSocketServer;
  private pingTimer?: NodeJS.Timeout;

  constructor(private readonly store: Store) {}

  attach(server: Server): void {
    this.wss = new WebSocketServer({
      server,
      path: '/ws',
      maxPayload: config.maxFrameBytes
    });

    this.wss.on('connection', (socket, request) => this.onConnection(socket, request));

    this.pingTimer = setInterval(() => this.sweep(), config.pingIntervalMs);
    this.pingTimer.unref?.();
  }

  async close(): Promise<void> {
    clearInterval(this.pingTimer);
    for (const connection of this.connections.values()) {
      try {
        connection.socket.close(1001, 'server shutting down');
      } catch {
        // соединение уже закрыто
      }
    }
    this.connections.clear();
    await new Promise<void>((resolve) => {
      if (!this.wss) return resolve();
      this.wss.close(() => resolve());
    });
  }

  // ---------------- Подключение ----------------

  private onConnection(socket: WebSocket, request: IncomingMessage): void {
    const url = new URL(request.url ?? '/ws', 'http://localhost');
    const token = extractBearer(request.headers.authorization) ?? url.searchParams.get('token');

    if (!token) {
      socket.close(4401, 'token required');
      return;
    }

    const device = this.store.authenticateByToken(token);
    if (!device) {
      logger.warn({ ip: request.socket.remoteAddress }, 'Отклонено подключение с неизвестным токеном');
      socket.close(4403, 'unknown token');
      return;
    }

    const existing = this.connections.get(device.id);
    if (existing && existing.socket !== socket) {
      // Одно устройство — одно соединение. Старое закрываем.
      try {
        existing.socket.close(4409, 'replaced by a new connection');
      } catch {
        // уже закрыто
      }
    }

    const connection: Connection = {
      socket,
      device,
      role: device.role,
      state: device.role === 'pc' ? PowerState.Running : PowerState.Unknown,
      lastSeen: Date.now(),
      alive: true
    };
    this.connections.set(device.id, connection);
    this.store.touchDevice(device.id, connection.state);

    logger.info({ deviceId: device.id, role: device.role, name: device.name }, 'Устройство подключилось');

    socket.on('message', (raw) => this.onMessage(connection, raw));
    socket.on('pong', () => {
      connection.alive = true;
      connection.lastSeen = Date.now();
    });
    socket.on('close', () => this.onClose(connection));
    socket.on('error', (error) => logger.debug({ deviceId: device.id, error }, 'Ошибка сокета'));

    this.send(
      device.id,
      sysEnvelope(
        Sys.HelloOk,
        {
          serverTime: Math.floor(Date.now() / 1000),
          version: config.version,
          peers: this.peersOf(device.id)
        },
        device.id
      )
    );

    this.broadcastPeerState(device.id, true, connection.state);
  }

  private onClose(connection: Connection): void {
    const current = this.connections.get(connection.device.id);
    if (current?.socket === connection.socket) {
      this.connections.delete(connection.device.id);
      this.store.touchDevice(connection.device.id, connection.role === 'pc' ? PowerState.Unknown : PowerState.Unknown);
      logger.info({ deviceId: connection.device.id }, 'Устройство отключилось');
      this.broadcastPeerState(connection.device.id, false, PowerState.Unknown);
    }
  }

  private sweep(): void {
    const cutoff = Date.now() - config.connectionTimeoutMs;

    for (const connection of [...this.connections.values()]) {
      if (connection.lastSeen < cutoff) {
        logger.warn({ deviceId: connection.device.id }, 'Соединение молчит — закрываем');
        try {
          connection.socket.terminate();
        } catch {
          // уже закрыто
        }
        this.onClose(connection);
        continue;
      }

      connection.alive = false;
      try {
        connection.socket.ping();
        this.send(connection.device.id, sysEnvelope(Sys.Ping, { t: Date.now() }, connection.device.id));
      } catch {
        // отвалится на следующем проходе
      }
    }
  }

  // ---------------- Сообщения ----------------

  private onMessage(connection: Connection, raw: RawData): void {
    connection.lastSeen = Date.now();

    const text = typeof raw === 'string' ? raw : raw.toString('utf8');
    const envelope = parseEnvelope(text);

    if (!envelope) {
      logger.debug({ deviceId: connection.device.id }, 'Отброшен некорректный кадр');
      return;
    }

    if (envelope.v !== config.protocolVersion) {
      this.send(
        connection.device.id,
        sysEnvelope(Sys.Error, { code: 'E_VERSION', message: 'Неподдерживаемая версия протокола.' }, connection.device.id)
      );
      return;
    }

    // Подменить отправителя нельзя: from всегда берём из аутентифицированного соединения.
    if (envelope.from !== connection.device.id) {
      logger.warn(
        { claimed: envelope.from, actual: connection.device.id },
        'Попытка отправить сообщение от чужого имени'
      );
      envelope.from = connection.device.id;
    }

    if (envelope.type === 'sys') {
      this.handleSys(connection, envelope);
      return;
    }

    this.route(connection, envelope);
  }

  private handleSys(connection: Connection, envelope: Envelope): void {
    const data = (envelope.data ?? {}) as Record<string, unknown>;

    switch (envelope.sys) {
      case Sys.Hello: {
        const name = typeof data.name === 'string' ? data.name : undefined;
        const mac = typeof data.mac === 'string' ? data.mac : undefined;
        const lanIps = Array.isArray(data.lanIps) ? (data.lanIps as string[]) : undefined;
        this.store.updateDeviceMeta(connection.device.id, { name, mac, lanIps });

        const refreshed = this.store.getDevice(connection.device.id);
        if (refreshed) connection.device = refreshed;

        if (connection.role === 'pc') {
          connection.state = PowerState.Running;
          this.store.touchDevice(connection.device.id, PowerState.Running);
          this.store.addEvent(connection.device.id, 'relay.connect', 'Компьютер на связи', 'server');
          this.broadcastPeerState(connection.device.id, true, PowerState.Running);
        }
        break;
      }

      case Sys.Pong:
        connection.alive = true;
        break;

      case Sys.Ping:
        this.send(connection.device.id, sysEnvelope(Sys.Pong, { t: Date.now() }, connection.device.id));
        break;

      case Sys.Heartbeat: {
        const state = typeof data.state === 'string' ? data.state : connection.state;
        const changed = state !== connection.state;
        connection.state = state;
        this.store.touchDevice(connection.device.id, state);
        if (changed) this.broadcastPeerState(connection.device.id, true, state);
        break;
      }

      case Sys.PairOffer: {
        if (connection.role !== 'pc') return;
        const token = typeof data.token === 'string' ? data.token : null;
        const exp = typeof data.exp === 'number' ? data.exp : 0;
        if (!token || !exp) return;

        this.store.saveOffer(token, connection.device.id, exp);
        logger.info({ pcId: connection.device.id }, 'Получен код сопряжения');
        break;
      }

      case Sys.PairResult:
      case Sys.WolResult:
      case Sys.BridgePong: {
        // Ответы на запросы сервера — отдаём ожидающему обработчику.
        const key = typeof envelope.re === 'string' ? envelope.re : `${envelope.sys}:${connection.device.id}`;
        const resolver = this.pending.get(key);
        if (resolver) {
          this.pending.delete(key);
          resolver(envelope);
        }
        break;
      }

      default:
        logger.debug({ sys: envelope.sys, deviceId: connection.device.id }, 'Неизвестное служебное сообщение');
    }
  }

  private route(connection: Connection, envelope: Envelope): void {
    if (!envelope.enc) {
      logger.warn({ from: envelope.from, type: envelope.type }, 'Незашифрованный конверт отброшен');
      this.send(
        connection.device.id,
        sysEnvelope(
          Sys.Error,
          { code: 'E_CRYPTO', message: 'Команды и события должны быть зашифрованы.' },
          connection.device.id
        )
      );
      return;
    }

    const targets =
      envelope.to === '*'
        ? this.store.listPeers(connection.device.id).map((d) => d.id)
        : [envelope.to];

    for (const target of targets) {
      if (!this.store.arePaired(connection.device.id, target)) {
        logger.warn({ from: connection.device.id, to: target }, 'Маршрут между неспаренными устройствами');
        this.send(
          connection.device.id,
          sysEnvelope(Sys.RouteFail, { re: envelope.id, reason: 'denied' }, connection.device.id)
        );
        continue;
      }

      const delivered = this.send(target, { ...envelope, to: target });
      if (!delivered && envelope.to !== '*') {
        this.send(
          connection.device.id,
          sysEnvelope(Sys.RouteFail, { re: envelope.id, reason: 'offline' }, connection.device.id)
        );
      }
    }
  }

  // ---------------- Публичный API ----------------

  isOnline(deviceId: string): boolean {
    const connection = this.connections.get(deviceId);
    return connection?.socket.readyState === WebSocket.OPEN;
  }

  stateOf(deviceId: string): string {
    return this.connections.get(deviceId)?.state ?? PowerState.Unknown;
  }

  send(deviceId: string, envelope: Envelope): boolean {
    const connection = this.connections.get(deviceId);
    if (!connection || connection.socket.readyState !== WebSocket.OPEN) return false;

    try {
      connection.socket.send(JSON.stringify(envelope));
      return true;
    } catch (error) {
      logger.debug({ deviceId, error }, 'Не удалось отправить сообщение');
      return false;
    }
  }

  /** Отправляет служебный запрос и ждёт ответ устройства (сопряжение, WOL). */
  request(deviceId: string, sys: string, data: Record<string, unknown>, timeoutMs: number): Promise<Envelope | null> {
    const envelope = sysEnvelope(sys, data, deviceId);
    if (!this.send(deviceId, envelope)) return Promise.resolve(null);

    return new Promise((resolve) => {
      const timer = setTimeout(() => {
        this.pending.delete(envelope.id);
        resolve(null);
      }, timeoutMs);

      this.pending.set(envelope.id, (reply) => {
        clearTimeout(timer);
        resolve(reply);
      });
    });
  }

  peersOf(deviceId: string) {
    return this.store.listPeers(deviceId).map((row) => {
      const online = this.isOnline(row.id);
      const peer = toPeerState(row, online);
      if (online) peer.state = this.stateOf(row.id);
      return peer;
    });
  }

  private broadcastPeerState(deviceId: string, online: boolean, state: string): void {
    const device = this.store.getDevice(deviceId);
    if (!device) return;

    const payload = {
      deviceId,
      role: device.role,
      name: device.name,
      online,
      state,
      lastSeen: device.last_seen
    };

    for (const peer of this.store.listPeers(deviceId)) {
      this.send(peer.id, sysEnvelope(Sys.PeerState, payload, peer.id));
    }
  }
}
