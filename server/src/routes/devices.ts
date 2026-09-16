import type { FastifyInstance } from 'fastify';
import { config } from '../config.js';
import { registrationSecretMatches, extractBearer } from '../lib/auth.js';
import { toPeerState } from '../db/database.js';
import type { DeviceRole } from '../lib/protocol.js';
import type { RouteContext } from './index.js';
import { requireDevice } from './index.js';
import { logger } from '../lib/logger.js';

interface RegisterBody {
  deviceId?: string;
  role?: string;
  name?: string;
  pub?: string;
  mac?: string | null;
  lanIps?: string[] | null;
  secret?: string;
}

const ROLES = new Set(['pc', 'phone', 'bridge']);

export async function registerDeviceRoutes(app: FastifyInstance, { store, hub }: RouteContext): Promise<void> {
  app.post<{ Body: RegisterBody }>('/api/v1/devices/register', async (request, reply) => {
    if (!config.allowRegistration) {
      return reply.code(403).send({ error: 'registration_closed', message: 'Регистрация новых устройств закрыта.' });
    }

    const body = request.body ?? {};
    if (!registrationSecretMatches(body.secret, config.registrationSecret)) {
      return reply.code(403).send({ error: 'bad_secret', message: 'Неверный секрет регистрации.' });
    }

    if (!body.pub || typeof body.pub !== 'string' || body.pub.length < 16 || body.pub.length > 128) {
      return reply.code(400).send({ error: 'bad_pub', message: 'Нужен публичный ключ устройства.' });
    }

    const role = (body.role ?? '').toLowerCase();
    if (!ROLES.has(role)) {
      return reply.code(400).send({ error: 'bad_role', message: 'Роль должна быть pc, phone или bridge.' });
    }

    const result = store.registerDevice({
      deviceId: body.deviceId,
      role: role as DeviceRole,
      name: typeof body.name === 'string' ? body.name.slice(0, 120) : undefined,
      pub: body.pub,
      mac: typeof body.mac === 'string' ? body.mac : null,
      lanIps: Array.isArray(body.lanIps) ? body.lanIps.slice(0, 8) : null,
      existingToken: extractBearer(request.headers.authorization)
    });

    if ('error' in result) {
      return reply.code(409).send({ error: 'device_exists', message: result.error });
    }

    logger.info({ deviceId: result.deviceId, role }, 'Устройство зарегистрировано');
    return reply.send(result);
  });

  app.post<{ Body: { name?: string; mac?: string | null; lanIps?: string[] | null } }>(
    '/api/v1/devices/refresh',
    async (request, reply) => {
      const device = requireDevice(store, request, reply);
      if (!device) return;

      store.updateDeviceMeta(device.id, {
        name: request.body?.name,
        mac: request.body?.mac ?? null,
        lanIps: request.body?.lanIps ?? null
      });
      return reply.send({ ok: true });
    }
  );

  app.get('/api/v1/devices', async (request, reply) => {
    const device = requireDevice(store, request, reply);
    if (!device) return;

    const peers = store.listPeers(device.id).map((row) => {
      const online = hub.isOnline(row.id);
      const peer = toPeerState(row, online);
      if (online) peer.state = hub.stateOf(row.id);
      return peer;
    });

    return reply.send({
      self: { deviceId: device.id, role: device.role, name: device.name },
      devices: peers
    });
  });

  app.delete<{ Params: { id: string } }>('/api/v1/devices/:id', async (request, reply) => {
    const device = requireDevice(store, request, reply);
    if (!device) return;

    const targetId = request.params.id;
    if (!store.arePaired(device.id, targetId)) {
      return reply.code(404).send({ error: 'not_found', message: 'Такой пары нет.' });
    }

    const pcId = device.role === 'pc' ? device.id : targetId;
    const phoneId = device.role === 'pc' ? targetId : device.id;
    const revoked = store.revokePair(pcId, phoneId);

    if (revoked) {
      store.addEvent(pcId, 'pair.revoke', 'Доступ телефона отозван', device.role);
      logger.info({ pcId, phoneId, by: device.id }, 'Сопряжение отозвано');
    }

    return reply.send({ ok: revoked });
  });

  app.post<{ Body: { platform?: string; token?: string } }>('/api/v1/push/register', async (request, reply) => {
    const device = requireDevice(store, request, reply);
    if (!device) return;

    const platform = (request.body?.platform ?? '').toLowerCase();
    const token = request.body?.token;

    if (!['fcm', 'rustore', 'apns'].includes(platform) || !token) {
      return reply.code(400).send({ error: 'bad_request', message: 'Нужны platform и token.' });
    }

    store.savePushToken(device.id, platform, token);
    return reply.send({ ok: true });
  });
}
