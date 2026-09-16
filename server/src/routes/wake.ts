import type { FastifyInstance } from 'fastify';
import { config } from '../config.js';
import { Sys } from '../lib/protocol.js';
import type { RouteContext } from './index.js';
import { requireDevice } from './index.js';
import { logger } from '../lib/logger.js';

/**
 * Включение компьютера. Сам сервер магический пакет отправить не может —
 * он вне домашней сети. Поэтому команда уходит мосту пробуждения,
 * который стоит дома и держит постоянное соединение.
 */
export async function registerWakeRoutes(app: FastifyInstance, { store, hub }: RouteContext): Promise<void> {
  app.post<{ Body: { pcId?: string } }>('/api/v1/wake', async (request, reply) => {
    const device = requireDevice(store, request, reply);
    if (!device) return;

    const pcId = request.body?.pcId ?? store.listPeers(device.id).find((p) => p.role === 'pc')?.id;
    if (!pcId) return reply.code(404).send({ error: 'not_found', message: 'Компьютер не найден.' });

    if (!store.arePaired(device.id, pcId)) {
      return reply.code(403).send({ error: 'denied', message: 'Этот компьютер не спарен с устройством.' });
    }

    if (hub.isOnline(pcId)) {
      return reply.send({ ok: true, via: 'already-on', message: 'Компьютер уже на связи.' });
    }

    const pc = store.getDevice(pcId);
    if (!pc?.mac) {
      return reply.code(400).send({
        ok: false,
        error: 'no_mac',
        message: 'У компьютера не сохранён MAC-адрес — Wake-on-LAN невозможен.'
      });
    }

    const bridges = store.findBridgesForPc(pcId).filter((id) => hub.isOnline(id));
    if (bridges.length === 0) {
      return reply.code(503).send({
        ok: false,
        via: 'none',
        error: 'no_bridge',
        message:
          'Нет доступного моста пробуждения. Включить компьютер из другой сети можно только через мост ' +
          '(ESP32 / Raspberry Pi), либо находясь в домашней сети Wi-Fi.'
      });
    }

    const requestId = crypto.randomUUID();
    const results = await Promise.all(
      bridges.map((bridgeId) =>
        hub.request(
          bridgeId,
          Sys.WolRequest,
          { requestId, mac: pc.mac, repeat: 3 },
          config.wolTimeoutMs
        )
      )
    );

    const sent = results.some((r) => {
      const data = (r?.data ?? {}) as Record<string, unknown>;
      return data.sent === true;
    });

    store.addEvent(
      pcId,
      'wake.request',
      sent ? 'Отправлен магический пакет через мост' : 'Мост не смог отправить магический пакет',
      device.role
    );

    logger.info({ pcId, bridges: bridges.length, sent }, 'Запрос на включение компьютера');

    return reply.code(sent ? 200 : 502).send({
      ok: sent,
      via: 'bridge',
      requestId,
      bridges: bridges.length,
      message: sent
        ? 'Магический пакет отправлен. Компьютер обычно выходит на связь за 30–60 секунд.'
        : 'Мост не подтвердил отправку пакета.'
    });
  });

  /** Проверка, виден ли ПК в домашней сети — второй источник статуса. */
  app.post<{ Body: { pcId?: string } }>('/api/v1/wake/probe', async (request, reply) => {
    const device = requireDevice(store, request, reply);
    if (!device) return;

    const pcId = request.body?.pcId ?? store.listPeers(device.id).find((p) => p.role === 'pc')?.id;
    if (!pcId || !store.arePaired(device.id, pcId)) {
      return reply.code(404).send({ error: 'not_found', message: 'Компьютер не найден или не спарен.' });
    }

    const pc = store.getDevice(pcId);
    const ip = pc?.lan_ips ? (JSON.parse(pc.lan_ips) as string[])[0] : undefined;
    if (!ip) return reply.send({ ok: false, alive: false, message: 'Локальный адрес компьютера неизвестен.' });

    const bridges = store.findBridgesForPc(pcId).filter((id) => hub.isOnline(id));
    if (bridges.length === 0) return reply.send({ ok: false, alive: false, message: 'Мост недоступен.' });

    const requestId = crypto.randomUUID();
    const result = await hub.request(bridges[0]!, Sys.BridgePing, { requestId, ip }, config.wolTimeoutMs);
    const data = (result?.data ?? {}) as Record<string, unknown>;

    return reply.send({ ok: true, alive: data.alive === true, rttMs: data.rttMs ?? null });
  });
}
