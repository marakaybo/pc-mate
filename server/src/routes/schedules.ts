import type { FastifyInstance } from 'fastify';
import { Sys, sysEnvelope } from '../lib/protocol.js';
import type { RouteContext } from './index.js';
import { requireDevice } from './index.js';

interface ScheduleLike {
  id?: string;
  name?: string;
  action?: string;
  time?: string;
  days?: number[];
  enabled?: boolean;
  [key: string]: unknown;
}

const ACTIONS = new Set(['wake', 'shutdown', 'sleep', 'hibernate', 'reboot', 'scenario']);

/**
 * Расписания хранятся и на сервере, и на ПК. Сервер — точка синхронизации
 * между телефоном и агентом; исполняет их всегда сам агент (таймеры Планировщика),
 * поэтому расписание работает даже когда сервер недоступен.
 */
export async function registerScheduleRoutes(app: FastifyInstance, { store, hub }: RouteContext): Promise<void> {
  function resolvePcId(deviceId: string, role: string, requested?: string): string | null {
    if (role === 'pc') return deviceId;

    const peers = store.listPeers(deviceId).filter((p) => p.role === 'pc');
    if (requested) return peers.some((p) => p.id === requested) ? requested : null;
    return peers[0]?.id ?? null;
  }

  app.get<{ Querystring: { pcId?: string } }>('/api/v1/schedules', async (request, reply) => {
    const device = requireDevice(store, request, reply);
    if (!device) return;

    const pcId = resolvePcId(device.id, device.role, request.query.pcId);
    if (!pcId) return reply.code(404).send({ error: 'not_found', message: 'Компьютер не найден или не спарен.' });

    return reply.send({ pcId, schedules: store.listSchedules(pcId) });
  });

  app.put<{ Params: { id: string }; Body: { pcId?: string; schedule?: ScheduleLike } }>(
    '/api/v1/schedules/:id',
    async (request, reply) => {
      const device = requireDevice(store, request, reply);
      if (!device) return;

      const schedule = request.body?.schedule;
      if (!schedule || typeof schedule !== 'object') {
        return reply.code(400).send({ error: 'bad_request', message: 'Нужен объект schedule.' });
      }

      if (schedule.action && !ACTIONS.has(schedule.action)) {
        return reply.code(400).send({ error: 'bad_action', message: `Неизвестное действие: ${schedule.action}` });
      }

      if (schedule.time && !/^\d{2}:\d{2}$/.test(schedule.time)) {
        return reply.code(400).send({ error: 'bad_time', message: 'Время должно быть в формате ЧЧ:ММ.' });
      }

      const pcId = resolvePcId(device.id, device.role, request.body?.pcId);
      if (!pcId) return reply.code(404).send({ error: 'not_found', message: 'Компьютер не найден или не спарен.' });

      const id = request.params.id;
      schedule.id = id;
      store.saveSchedule(pcId, id, schedule);

      // Агент заводит задачу в Планировщике — до этого расписание считается несинхронизированным.
      const pushed = hub.send(pcId, sysEnvelope(Sys.SchedulePush, { schedules: [schedule] }, pcId));

      return reply.send({ ok: true, synced: pushed, pcOnline: hub.isOnline(pcId) });
    }
  );

  app.delete<{ Params: { id: string }; Querystring: { pcId?: string } }>(
    '/api/v1/schedules/:id',
    async (request, reply) => {
      const device = requireDevice(store, request, reply);
      if (!device) return;

      const pcId = resolvePcId(device.id, device.role, request.query.pcId);
      if (!pcId) return reply.code(404).send({ error: 'not_found', message: 'Компьютер не найден или не спарен.' });

      const deleted = store.deleteSchedule(pcId, request.params.id);
      if (deleted) {
        hub.send(pcId, sysEnvelope(Sys.SchedulePush, { schedules: store.listSchedules(pcId) }, pcId));
      }

      return reply.send({ ok: deleted });
    }
  );

  /** Принудительно раздать агенту всё, что есть на сервере. */
  app.post<{ Body: { pcId?: string } }>('/api/v1/schedules/sync', async (request, reply) => {
    const device = requireDevice(store, request, reply);
    if (!device) return;

    const pcId = resolvePcId(device.id, device.role, request.body?.pcId);
    if (!pcId) return reply.code(404).send({ error: 'not_found', message: 'Компьютер не найден или не спарен.' });

    const schedules = store.listSchedules(pcId);
    const pushed = hub.send(pcId, sysEnvelope(Sys.SchedulePush, { schedules }, pcId));
    return reply.send({ ok: pushed, count: schedules.length, pcOnline: hub.isOnline(pcId) });
  });
}
