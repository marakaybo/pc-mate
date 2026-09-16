import type { FastifyInstance } from 'fastify';
import { config } from '../config.js';
import { Sys } from '../lib/protocol.js';
import type { RouteContext } from './index.js';
import { logger } from '../lib/logger.js';

interface ClaimBody {
  token?: string;
  phoneId?: string;
  phonePub?: string;
  phoneName?: string;
}

/**
 * Сопряжение через сервер (docs/protocol.md §8).
 * Сервер только сводит стороны: общий ключ они вычисляют сами,
 * их приватные ключи он никогда не видит.
 */
export async function registerPairingRoutes(app: FastifyInstance, { store, hub }: RouteContext): Promise<void> {
  app.post<{ Body: ClaimBody }>('/api/v1/pair/claim', async (request, reply) => {
    const body = request.body ?? {};

    if (!body.token || !body.phonePub || !body.phoneId) {
      return reply
        .code(400)
        .send({ ok: false, error: 'Нужны token, phoneId и phonePub.' });
    }

    const offer = store.takeOffer(body.token);
    if (!offer) {
      return reply
        .code(404)
        .send({ ok: false, error: 'Код сопряжения не найден или истёк. Откройте QR-код на компьютере заново.' });
    }

    const phone = store.getDevice(body.phoneId);
    if (!phone) {
      return reply
        .code(400)
        .send({ ok: false, error: 'Телефон не зарегистрирован на сервере. Сначала выполните /devices/register.' });
    }

    if (!hub.isOnline(offer.pcId)) {
      return reply
        .code(503)
        .send({ ok: false, error: 'Компьютер сейчас не на связи с сервером. Включите его и повторите.' });
    }

    const pairId = crypto.randomUUID();
    const reply$ = await hub.request(
      offer.pcId,
      Sys.PairClaim,
      {
        token: body.token,
        phoneId: body.phoneId,
        phonePub: body.phonePub,
        phoneName: body.phoneName ?? phone.name ?? 'Телефон',
        pairId
      },
      config.pairTimeoutMs
    );

    if (!reply$) {
      return reply.code(504).send({ ok: false, error: 'Компьютер не ответил на запрос сопряжения.' });
    }

    const data = (reply$.data ?? {}) as Record<string, unknown>;
    if (data.ok !== true) {
      return reply
        .code(403)
        .send({ ok: false, error: typeof data.error === 'string' ? data.error : 'Компьютер отклонил сопряжение.' });
    }

    const resolvedPairId = typeof data.pairId === 'string' ? data.pairId : pairId;
    store.createPair(resolvedPairId, offer.pcId, body.phoneId);
    store.consumeOffer(body.token);
    store.addEvent(offer.pcId, 'pair.ok', `Телефон «${body.phoneName ?? 'без имени'}» подключён`, 'server');

    logger.info({ pcId: offer.pcId, phoneId: body.phoneId }, 'Сопряжение выполнено');

    return reply.send({
      ok: true,
      pairId: resolvedPairId,
      pcId: offer.pcId,
      pcName: data.pcName ?? null,
      pcPub: data.pcPub ?? null,
      mac: data.mac ?? null,
      lanIps: data.lanIps ?? [],
      fingerprint: data.fingerprint ?? null
    });
  });

  /** Привязка моста пробуждения к компьютеру — мост шлёт магический пакет по команде сервера. */
  app.post<{ Body: { bridgeId?: string; pcId?: string } }>('/api/v1/bridges/bind', async (request, reply) => {
    const { bridgeId, pcId } = request.body ?? {};
    if (!bridgeId || !pcId) {
      return reply.code(400).send({ error: 'bad_request', message: 'Нужны bridgeId и pcId.' });
    }

    const bridge = store.getDevice(bridgeId);
    const pc = store.getDevice(pcId);

    if (!bridge || bridge.role !== 'bridge') {
      return reply.code(404).send({ error: 'not_found', message: 'Мост не зарегистрирован.' });
    }
    if (!pc || pc.role !== 'pc') {
      return reply.code(404).send({ error: 'not_found', message: 'Компьютер не найден.' });
    }

    store.bindBridge(bridgeId, pcId);
    store.addEvent(pcId, 'bridge.bind', 'К компьютеру привязан мост пробуждения', 'server');
    return reply.send({ ok: true });
  });
}
