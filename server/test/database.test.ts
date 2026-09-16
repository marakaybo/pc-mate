import { test, describe } from 'node:test';
import assert from 'node:assert/strict';
import { Store } from '../src/db/database.js';

function makeStore(): Store {
  return new Store(':memory:');
}

describe('Store: устройства', () => {
  test('регистрирует устройство и выдаёт рабочий токен', () => {
    const store = makeStore();
    const result = store.registerDevice({ role: 'pc', name: 'DESKTOP', pub: 'a'.repeat(43) });

    assert.ok('deviceToken' in result);
    if (!('deviceToken' in result)) return;

    const device = store.authenticate(result.deviceId, result.deviceToken);
    assert.ok(device);
    assert.equal(device?.role, 'pc');
    assert.equal(device?.name, 'DESKTOP');

    assert.equal(store.authenticate(result.deviceId, 'wrong-token'), null);
    store.close();
  });

  test('чужому не отдаёт занятый идентификатор', () => {
    const store = makeStore();
    const first = store.registerDevice({ deviceId: 'pc-1', role: 'pc', pub: 'a'.repeat(43) });
    assert.ok('deviceToken' in first);

    const attacker = store.registerDevice({ deviceId: 'pc-1', role: 'pc', pub: 'b'.repeat(43) });
    assert.ok('error' in attacker);
    store.close();
  });

  test('владелец токена может перерегистрироваться', () => {
    const store = makeStore();
    const first = store.registerDevice({ deviceId: 'pc-1', role: 'pc', pub: 'a'.repeat(43) });
    assert.ok('deviceToken' in first);
    if (!('deviceToken' in first)) return;

    const again = store.registerDevice({
      deviceId: 'pc-1',
      role: 'pc',
      pub: 'c'.repeat(43),
      existingToken: first.deviceToken
    });

    assert.ok('deviceToken' in again);
    if (!('deviceToken' in again)) return;
    assert.notEqual(again.deviceToken, first.deviceToken, 'выдаётся новый токен');
    assert.equal(store.authenticate('pc-1', first.deviceToken), null, 'старый токен больше не работает');
    store.close();
  });

  test('находит устройство по одному лишь токену', () => {
    const store = makeStore();
    const result = store.registerDevice({ role: 'phone', pub: 'p'.repeat(43) });
    assert.ok('deviceToken' in result);
    if (!('deviceToken' in result)) return;

    const found = store.authenticateByToken(result.deviceToken);
    assert.equal(found?.id, result.deviceId);
    assert.equal(store.authenticateByToken('нет такого'), null);
    store.close();
  });
});

describe('Store: пары', () => {
  test('связывает телефон и ПК и разрывает связь при отзыве', () => {
    const store = makeStore();
    store.registerDevice({ deviceId: 'pc-1', role: 'pc', pub: 'a'.repeat(43) });
    store.registerDevice({ deviceId: 'phone-1', role: 'phone', pub: 'b'.repeat(43) });

    assert.equal(store.arePaired('pc-1', 'phone-1'), false);

    store.createPair('pair-1', 'pc-1', 'phone-1');
    assert.equal(store.arePaired('pc-1', 'phone-1'), true);
    assert.equal(store.arePaired('phone-1', 'pc-1'), true, 'связь симметрична');

    assert.equal(store.listPeers('phone-1').length, 1);
    assert.equal(store.listPeers('phone-1')[0]?.id, 'pc-1');

    assert.equal(store.revokePair('pc-1', 'phone-1'), true);
    assert.equal(store.arePaired('pc-1', 'phone-1'), false);
    assert.equal(store.listPeers('phone-1').length, 0);
    store.close();
  });

  test('повторное сопряжение той же пары не создаёт дубликат', () => {
    const store = makeStore();
    store.registerDevice({ deviceId: 'pc-1', role: 'pc', pub: 'a'.repeat(43) });
    store.registerDevice({ deviceId: 'phone-1', role: 'phone', pub: 'b'.repeat(43) });

    store.createPair('pair-1', 'pc-1', 'phone-1');
    store.createPair('pair-2', 'pc-1', 'phone-1');

    assert.equal(store.listPeers('phone-1').length, 1);
    store.close();
  });

  test('отозванная пара восстанавливается при новом сопряжении', () => {
    const store = makeStore();
    store.registerDevice({ deviceId: 'pc-1', role: 'pc', pub: 'a'.repeat(43) });
    store.registerDevice({ deviceId: 'phone-1', role: 'phone', pub: 'b'.repeat(43) });

    store.createPair('pair-1', 'pc-1', 'phone-1');
    store.revokePair('pc-1', 'phone-1');
    store.createPair('pair-3', 'pc-1', 'phone-1');

    assert.equal(store.arePaired('pc-1', 'phone-1'), true);
    store.close();
  });
});

describe('Store: коды сопряжения', () => {
  test('код действует до истечения срока и гасится после использования', () => {
    const store = makeStore();
    store.registerDevice({ deviceId: 'pc-1', role: 'pc', pub: 'a'.repeat(43) });

    const exp = Math.floor(Date.now() / 1000) + 300;
    store.saveOffer('token-1', 'pc-1', exp);

    assert.deepEqual(store.takeOffer('token-1'), { pcId: 'pc-1' });

    store.consumeOffer('token-1');
    assert.equal(store.takeOffer('token-1'), null);
    store.close();
  });

  test('истёкший код не принимается', () => {
    const store = makeStore();
    store.registerDevice({ deviceId: 'pc-1', role: 'pc', pub: 'a'.repeat(43) });
    store.saveOffer('old', 'pc-1', Math.floor(Date.now() / 1000) - 10);

    assert.equal(store.takeOffer('old'), null);
    store.close();
  });
});

describe('Store: расписания и события', () => {
  test('расписания сохраняются и удаляются по ПК', () => {
    const store = makeStore();
    store.registerDevice({ deviceId: 'pc-1', role: 'pc', pub: 'a'.repeat(43) });

    store.saveSchedule('pc-1', 'wk', { id: 'wk', action: 'wake', time: '23:00', days: [1, 2, 3, 4, 5] });
    store.saveSchedule('pc-1', 'night', { id: 'night', action: 'hibernate', time: '03:00' });

    assert.equal(store.listSchedules('pc-1').length, 2);
    assert.equal(store.deleteSchedule('pc-1', 'wk'), true);
    assert.equal(store.listSchedules('pc-1').length, 1);
    assert.equal(store.deleteSchedule('pc-1', 'нет'), false);
    store.close();
  });

  test('история событий обрезается и отдаётся по возрастанию', () => {
    const store = makeStore();
    store.registerDevice({ deviceId: 'pc-1', role: 'pc', pub: 'a'.repeat(43) });

    for (let i = 0; i < 10; i++) store.addEvent('pc-1', 'test', `событие ${i}`);

    const events = store.listEvents('pc-1', 5);
    assert.equal(events.length, 5);
    assert.equal(events[0]?.summary, 'событие 5');
    assert.equal(events[4]?.summary, 'событие 9');
    store.close();
  });
});

describe('Store: мосты', () => {
  test('мост привязывается к компьютеру', () => {
    const store = makeStore();
    store.registerDevice({ deviceId: 'pc-1', role: 'pc', pub: 'a'.repeat(43) });
    store.registerDevice({ deviceId: 'bridge-1', role: 'bridge', pub: 'b'.repeat(43) });

    store.bindBridge('bridge-1', 'pc-1');
    assert.deepEqual(store.findBridgesForPc('pc-1'), ['bridge-1']);
    store.close();
  });
});
