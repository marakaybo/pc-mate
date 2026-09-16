import type { FastifyInstance, FastifyReply, FastifyRequest } from 'fastify';
import type { Store, DeviceRow } from '../db/database.js';
import type { Hub } from '../ws/hub.js';
import { extractBearer } from '../lib/auth.js';
import { registerDeviceRoutes } from './devices.js';
import { registerPairingRoutes } from './pairing.js';
import { registerScheduleRoutes } from './schedules.js';
import { registerEventRoutes } from './events.js';
import { registerWakeRoutes } from './wake.js';
import { config } from '../config.js';

export interface RouteContext {
  store: Store;
  hub: Hub;
}

/** Достаёт устройство по Bearer-токену; при неудаче сам отвечает 401. */
export function requireDevice(
  store: Store,
  request: FastifyRequest,
  reply: FastifyReply
): DeviceRow | null {
  const token = extractBearer(request.headers.authorization);
  if (!token) {
    reply.code(401).send({ error: 'unauthorized', message: 'Нужен заголовок Authorization: Bearer <токен>.' });
    return null;
  }

  const device = store.authenticateByToken(token);
  if (!device) {
    reply.code(401).send({ error: 'unauthorized', message: 'Неизвестный токен устройства.' });
    return null;
  }

  store.touchDevice(device.id);
  return device;
}

export async function registerRoutes(app: FastifyInstance, context: RouteContext): Promise<void> {
  const startedAt = Date.now();

  app.get('/api/v1/health', async () => ({
    ok: true,
    product: 'PC MATE',
    version: config.version,
    protocol: config.protocolVersion,
    uptimeSec: Math.floor((Date.now() - startedAt) / 1000)
  }));

  // Удобно проверять прокси и в браузере.
  app.get('/', async () => ({ ok: true, product: 'PC MATE relay', version: config.version }));

  await registerDeviceRoutes(app, context);
  await registerPairingRoutes(app, context);
  await registerScheduleRoutes(app, context);
  await registerEventRoutes(app, context);
  await registerWakeRoutes(app, context);
}
