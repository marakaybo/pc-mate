import { test, describe, before, after } from 'node:test';
import assert from 'node:assert/strict';
import type { FastifyInstance } from 'fastify';
import { createServer } from '../src/index.js';
import type { Store } from '../src/db/database.js';

let app: FastifyInstance;
let store: Store;

before(async () => {
  const created = await createServer(':memory:');
  app = created.app;
  store = created.store;
  await app.ready();
});

after(async () => {
  await app.close();
  store.close();
});

async function registerPc(name = 'DESKTOP-TEST') {
  const response = await app.inject({
    method: 'POST',
    url: '/api/v1/devices/register',
    payload: { role: 'pc', name, pub: 'a'.repeat(43), mac: 'AA:BB:CC:DD:EE:FF', lanIps: ['192.168.1.50'] }
  });
  return response.json() as { deviceId: string; deviceToken: string };
}

async function registerPhone(name = 'Pixel') {
  const response = await app.inject({
    method: 'POST',
    url: '/api/v1/devices/register',
    payload: { role: 'phone', name, pub: 'b'.repeat(43) }
  });
  return response.json() as { deviceId: string; deviceToken: string };
}

describe('HTTP API', () => {
  test('health отвечает', async () => {
    const response = await app.inject({ method: 'GET', url: '/api/v1/health' });
    assert.equal(response.statusCode, 200);

    const body = response.json();
    assert.equal(body.ok, true);
    assert.equal(body.protocol, 1);
  });

  test('регистрация требует валидных полей', async () => {
    const noPub = await app.inject({
      method: 'POST',
      url: '/api/v1/devices/register',
      payload: { role: 'pc', name: 'x' }
    });
    assert.equal(noPub.statusCode, 400);

    const badRole = await app.inject({
      method: 'POST',
      url: '/api/v1/devices/register',
      payload: { role: 'toaster', pub: 'a'.repeat(43) }
    });
    assert.equal(badRole.statusCode, 400);
  });

  test('регистрация выдаёт идентификатор и токен', async () => {
    const pc = await registerPc();
    assert.match(pc.deviceId, /^[0-9a-f-]{36}$/);
    assert.ok(pc.deviceToken.length > 30);
  });

  test('защищённые маршруты требуют токен', async () => {
    const response = await app.inject({ method: 'GET', url: '/api/v1/devices' });
    assert.equal(response.statusCode, 401);

    const wrong = await app.inject({
      method: 'GET',
      url: '/api/v1/devices',
      headers: { authorization: 'Bearer не-токен' }
    });
    assert.equal(wrong.statusCode, 401);
  });

  test('список устройств содержит только спаренные', async () => {
    const pc = await registerPc('PC-A');
    const phone = await registerPhone('Phone-A');
    const stranger = await registerPc('PC-Чужой');

    store.createPair('pair-api-1', pc.deviceId, phone.deviceId);

    const response = await app.inject({
      method: 'GET',
      url: '/api/v1/devices',
      headers: { authorization: `Bearer ${phone.deviceToken}` }
    });

    assert.equal(response.statusCode, 200);
    const body = response.json();
    assert.equal(body.devices.length, 1);
    assert.equal(body.devices[0].deviceId, pc.deviceId);
    assert.notEqual(body.devices[0].deviceId, stranger.deviceId);
    assert.equal(body.devices[0].online, false, 'ПК не подключён по WebSocket');
  });

  test('сопряжение без кода отклоняется', async () => {
    const phone = await registerPhone('Phone-B');
    const response = await app.inject({
      method: 'POST',
      url: '/api/v1/pair/claim',
      payload: { token: 'нет-такого', phoneId: phone.deviceId, phonePub: 'b'.repeat(43) }
    });

    assert.equal(response.statusCode, 404);
    assert.equal(response.json().ok, false);
  });

  test('сопряжение с кодом, но без связи с ПК, даёт понятную ошибку', async () => {
    const pc = await registerPc('PC-Offline');
    const phone = await registerPhone('Phone-C');
    store.saveOffer('offer-token-1', pc.deviceId, Math.floor(Date.now() / 1000) + 300);

    const response = await app.inject({
      method: 'POST',
      url: '/api/v1/pair/claim',
      payload: { token: 'offer-token-1', phoneId: phone.deviceId, phonePub: 'b'.repeat(43) }
    });

    assert.equal(response.statusCode, 503);
    assert.match(response.json().error, /не на связи/);
  });

  test('расписания сохраняются и читаются телефоном', async () => {
    const pc = await registerPc('PC-Sched');
    const phone = await registerPhone('Phone-Sched');
    store.createPair('pair-sched', pc.deviceId, phone.deviceId);

    const save = await app.inject({
      method: 'PUT',
      url: '/api/v1/schedules/evening',
      headers: { authorization: `Bearer ${phone.deviceToken}` },
      payload: {
        pcId: pc.deviceId,
        schedule: { id: 'evening', name: 'Вечер', action: 'wake', time: '23:00', days: [1, 2, 3, 4, 5] }
      }
    });
    assert.equal(save.statusCode, 200);
    assert.equal(save.json().synced, false, 'ПК офлайн — синхронизация отложена');

    const list = await app.inject({
      method: 'GET',
      url: `/api/v1/schedules?pcId=${pc.deviceId}`,
      headers: { authorization: `Bearer ${phone.deviceToken}` }
    });
    assert.equal(list.statusCode, 200);
    assert.equal(list.json().schedules.length, 1);
    assert.equal(list.json().schedules[0].name, 'Вечер');
  });

  test('расписание с неверным временем отклоняется', async () => {
    const pc = await registerPc('PC-BadTime');
    const phone = await registerPhone('Phone-BadTime');
    store.createPair('pair-badtime', pc.deviceId, phone.deviceId);

    const response = await app.inject({
      method: 'PUT',
      url: '/api/v1/schedules/x',
      headers: { authorization: `Bearer ${phone.deviceToken}` },
      payload: { pcId: pc.deviceId, schedule: { action: 'wake', time: '25 часов' } }
    });

    assert.equal(response.statusCode, 400);
  });

  test('чужие расписания недоступны', async () => {
    const pc = await registerPc('PC-Secret');
    const outsider = await registerPhone('Phone-Чужой');

    const response = await app.inject({
      method: 'GET',
      url: `/api/v1/schedules?pcId=${pc.deviceId}`,
      headers: { authorization: `Bearer ${outsider.deviceToken}` }
    });

    assert.equal(response.statusCode, 404);
  });

  test('включение без моста объясняет причину', async () => {
    const pc = await registerPc('PC-NoBridge');
    const phone = await registerPhone('Phone-NoBridge');
    store.createPair('pair-nobridge', pc.deviceId, phone.deviceId);

    const response = await app.inject({
      method: 'POST',
      url: '/api/v1/wake',
      headers: { authorization: `Bearer ${phone.deviceToken}` },
      payload: { pcId: pc.deviceId }
    });

    assert.equal(response.statusCode, 503);
    assert.equal(response.json().error, 'no_bridge');
    assert.match(response.json().message, /мост/i);
  });

  test('история событий доступна спаренному телефону', async () => {
    const pc = await registerPc('PC-Events');
    const phone = await registerPhone('Phone-Events');
    store.createPair('pair-events', pc.deviceId, phone.deviceId);
    store.addEvent(pc.deviceId, 'power.wake', 'Компьютер проснулся');

    const response = await app.inject({
      method: 'GET',
      url: `/api/v1/events?pcId=${pc.deviceId}`,
      headers: { authorization: `Bearer ${phone.deviceToken}` }
    });

    assert.equal(response.statusCode, 200);
    assert.equal(response.json().events.length, 1);
    assert.equal(response.json().events[0].kind, 'power.wake');
  });

  test('отзыв доступа удаляет пару', async () => {
    const pc = await registerPc('PC-Revoke');
    const phone = await registerPhone('Phone-Revoke');
    store.createPair('pair-revoke', pc.deviceId, phone.deviceId);

    const response = await app.inject({
      method: 'DELETE',
      url: `/api/v1/devices/${pc.deviceId}`,
      headers: { authorization: `Bearer ${phone.deviceToken}` }
    });

    assert.equal(response.statusCode, 200);
    assert.equal(store.arePaired(pc.deviceId, phone.deviceId), false);
  });
});
