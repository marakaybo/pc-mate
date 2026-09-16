import type { FastifyInstance } from 'fastify';
import type { RouteContext } from './index.js';
import { requireDevice } from './index.js';

/**
 * История событий. Сюда попадает только то, что устройство прислало открыто
 * служебным каналом: подключения, пробуждения, сработавшие расписания.
 * Содержимое команд остаётся зашифрованным и на сервер не попадает.
 */
export async function registerEventRoutes(app: FastifyInstance, { store }: RouteContext): Promise<void> {
  app.get<{ Querystring: { pcId?: string; limit?: string } }>('/api/v1/events', async (request, reply) => {
    const device = requireDevice(store, request, reply);
    if (!device) return;

    const limit = Number.parseInt(request.query.limit ?? '100', 10);
    const pcId =
      device.role === 'pc'
        ? device.id
        : request.query.pcId ?? store.listPeers(device.id).find((p) => p.role === 'pc')?.id;

    if (!pcId) return reply.code(404).send({ error: 'not_found', message: 'Компьютер не найден или не спарен.' });
    if (device.role !== 'pc' && !store.arePaired(device.id, pcId)) {
      return reply.code(403).send({ error: 'denied', message: 'Этот компьютер не спарен с устройством.' });
    }

    const events = store.listEvents(pcId, Number.isFinite(limit) ? limit : 100).map((row) => ({
      id: row.id,
      pcId: row.pc_id,
      kind: row.kind,
      summary: row.summary,
      at: row.at,
      source: row.source
    }));

    return reply.send({ pcId, events });
  });

  app.post<{ Body: { kind?: string; summary?: string } }>('/api/v1/events', async (request, reply) => {
    const device = requireDevice(store, request, reply);
    if (!device) return;

    const kind = request.body?.kind;
    const summary = request.body?.summary;
    if (!kind || !summary) {
      return reply.code(400).send({ error: 'bad_request', message: 'Нужны kind и summary.' });
    }

    const pcId = device.role === 'pc' ? device.id : store.listPeers(device.id).find((p) => p.role === 'pc')?.id;
    if (!pcId) return reply.code(404).send({ error: 'not_found', message: 'Компьютер не найден.' });

    const event = store.addEvent(pcId, kind.slice(0, 40), summary.slice(0, 300), device.role);
    return reply.send({ ok: true, id: event.id });
  });
}
