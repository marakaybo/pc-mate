# PC MATE — протокол v1

Единый документ-контракт для всех компонентов: агента ПК, сервера-ретранслятора,
мобильного приложения и моста пробуждения. Любое изменение здесь требует правок
во всех четырёх реализациях.

---

## 1. Термины и идентификаторы

| Термин | Значение |
|---|---|
| `deviceId` | UUIDv4 устройства. Генерируется один раз при первом запуске и хранится локально. |
| `pcId` / `phoneId` / `bridgeId` | `deviceId` конкретной роли. |
| `pairId` | UUIDv4 пары «телефон ↔ ПК». Создаётся при сопряжении, используется как salt HKDF. |
| `deviceToken` | 32 случайных байта (base64url). Bearer-токен устройства **для сервера**. Не участвует в сквозном шифровании. |
| `role` | `"pc" \| "phone" \| "bridge"` |

Все бинарные значения в JSON кодируются **base64url без паддинга** (`A-Z a-z 0-9 - _`).

---

## 2. Криптография

### 2.1 Ключи

* Каждое устройство при первом запуске генерирует статическую пару **X25519**
  (`identityPub` / `identityPriv`).
* `identityPriv` на ПК шифруется DPAPI (`LocalMachine`), на телефоне лежит в
  Keystore/Keychain (`expo-secure-store`), на мосту — в NVS/файле с правами `600`.

### 2.2 Согласование ключей при сопряжении

```
shared = X25519(myPriv, peerPub)              // 32 байта
prk    = HKDF-Extract(SHA-256, salt = utf8(pairId), ikm = shared)
kPhoneToPc = HKDF-Expand(prk, info = "pcmate/v1/phone->pc", L = 32)
kPcToPhone = HKDF-Expand(prk, info = "pcmate/v1/pc->phone", L = 32)
```

Направленные ключи исключают отражение сообщения обратно отправителю.

### 2.3 Шифрование полезной нагрузки

AEAD: **AES-256-GCM**, nonce 12 случайных байт, тег 16 байт.

```
plaintext  = utf8(JSON(payload))
aad        = utf8(v + "|" + id + "|" + from + "|" + to + "|" + ts)
(ct, tag)  = AES-GCM-Seal(key, nonce, plaintext, aad)
enc.c      = base64url(ct || tag)
```

AAD связывает шифротекст с маршрутными полями: сервер не может подменить
`from`/`to`/`ts`, не сломав проверку тега.

### 2.4 Защита от повтора

Получатель отклоняет сообщение, если:

1. `|now - payload.ts| > 120` секунд, **или**
2. `payload.nonce` уже встречался за последние 600 секунд (скользящее окно,
   хранится в памяти; после перезагрузки окно пустое, но защиту даёт п. 1), **или**
3. AEAD-тег не сходится.

Реализация окна: `ReplayGuard` (C#), `ReplayGuard` (TS), `ReplayGuard` (Python).

### 2.5 Отпечаток пары

```
fp = SHA-256(utf8("pcmate-fp/v1") || min(pubA,pubB) || max(pubA,pubB))
```

Показывается пользователю как 6 групп по 4 символа из Crockford-Base32 первых
15 байт (например `K7M2-9QX4-11BC-7D0E-2FG3-HJ5K`). ПК и телефон обязаны
показать одинаковый отпечаток после сопряжения.

---

## 3. Конверт сообщения

Все сообщения — JSON-объекты в одном WebSocket-фрейме (text).

```jsonc
{
  "v": 1,                     // версия протокола
  "id": "9b2c…",              // UUIDv4 сообщения; ответ повторяет его в "re"
  "re": "9b2c…",              // (только в ответах) id исходного сообщения
  "type": "cmd|res|evt|sys",
  "from": "<deviceId>",
  "to": "<deviceId>",         // "*" — всем спаренным устройствам отправителя
  "ts": 1757894400,           // unix seconds, время отправителя
  "enc": {                    // отсутствует только у type="sys"
    "alg": "aes-256-gcm",
    "n": "<base64url nonce 12B>",
    "c": "<base64url ciphertext||tag>"
  }
}
```

* `type="sys"` — служебные сообщения **между устройством и сервером**
  (аутентификация, heartbeat, сопряжение, WOL). Не шифруются сквозным ключом,
  но идут только внутри TLS и требуют `deviceToken`.
* `type="cmd" | "res" | "evt"` — сквозь сервер, он их **не расшифровывает**.

### 3.1 Полезная нагрузка (внутри `enc`)

**cmd**
```jsonc
{ "cmd": "power.shutdown", "args": { "delaySec": 30 }, "nonce": "<base64url 16B>", "ts": 1757894400 }
```

**res**
```jsonc
{ "ok": true, "result": { }, "nonce": "…", "ts": 1757894400 }
{ "ok": false, "error": { "code": "E_NO_SESSION", "message": "Нет активного сеанса пользователя" }, "nonce": "…", "ts": 1757894400 }
```

**evt**
```jsonc
{ "evt": "power.state", "data": { }, "nonce": "…", "ts": 1757894400 }
```

---

## 4. Команды (телефон → ПК)

| `cmd` | `args` | `result` |
|---|---|---|
| `status.get` | — | `StatusSnapshot` (§7.1) |
| `power.shutdown` | `{ delaySec?: number = 30, force?: boolean = false }` | `{ scheduledAt: iso }` |
| `power.reboot` | то же | `{ scheduledAt: iso }` |
| `power.sleep` | `{ delaySec?: number = 30, force?: boolean }` | `{ scheduledAt: iso }` |
| `power.hibernate` | то же | `{ scheduledAt: iso }` |
| `power.lock` | — | `{}` |
| `power.cancel` | — | `{ cancelled: boolean }` |
| `scenario.list` | — | `{ scenarios: Scenario[] }` |
| `scenario.get` | `{ id }` | `{ scenario: Scenario }` |
| `scenario.save` | `{ scenario: Scenario }` | `{ id }` |
| `scenario.delete` | `{ id }` | `{ deleted: boolean }` |
| `scenario.run` | `{ id }` или `{ scenario: Scenario }` | `{ runId }` (отчёт придёт событиями `scenario.progress` / `scenario.done`) |
| `scenario.cancel` | `{ runId }` | `{ cancelled: boolean }` |
| `apps.list` | `{ refresh?: boolean }` | `{ apps: AppEntry[] }` (§7.3) |
| `schedule.list` | — | `{ schedules: Schedule[] }` |
| `schedule.save` | `{ schedule: Schedule }` | `{ id, synced: boolean }` |
| `schedule.delete` | `{ id }` | `{ deleted: boolean }` |
| `schedule.sync` | — | `{ synced: number, failed: number }` |
| `arrive.set` | `{ minutes: number, scenarioId?: string }` | `{ wakeAt: iso }` |
| `arrive.cancel` | — | `{ cancelled: boolean }` |
| `wizard.run` | `{ ids?: string[] }` | `{ checks: CheckResult[] }` (§7.2) |
| `wizard.fix` | `{ id: string }` | `{ check: CheckResult }` |
| `wake.test` | `{ mode: "wol" \| "timer", sleepSeconds?: number = 120 }` | `{ testId }` |
| `history.list` | `{ limit?: number = 100 }` | `{ events: EventRecord[] }` |
| `pair.revoke` | `{ phoneId }` | `{ revoked: boolean }` |
| `agent.info` | — | `{ version, buildDate, os, features: string[] }` |
| `agent.settings.get` | — | `{ settings: AgentSettings }` |
| `agent.settings.set` | `{ settings: Partial<AgentSettings> }` | `{ settings: AgentSettings }` |

Коды ошибок: `E_UNKNOWN_CMD`, `E_BAD_ARGS`, `E_NO_SESSION`, `E_DENIED`,
`E_REPLAY`, `E_CRYPTO`, `E_BUSY`, `E_NOT_FOUND`, `E_OS`, `E_TIMEOUT`,
`E_DISABLED` (функция выключена в настройках агента), `E_VERSION`.

### 4.1 Команды только с самого компьютера

Эти команды приходят от помощника в трее по локальному каналу. С телефона они
возвращают `E_DENIED` — иначе один сопряжённый телефон мог бы втихую пригласить
второй или включить выполнение произвольных команд.

| `cmd` | `args` | `result` |
|---|---|---|
| `pair.offer` | — | `{ offer: PairingOffer, uri: string }` — новый одноразовый код для QR |
| `pair.list` | — | `{ phones: PairedPhone[] }` |
| `agent.settings.set` с `allowShellSteps` / `allowAutoLogon` | `{ settings }` | меняется только локально |

---

## 5. События (ПК → телефон)

| `evt` | `data` |
|---|---|
| `power.state` | `{ state: "running"\|"sleeping"\|"hibernating"\|"shutting-down"\|"locked", reason?: string }` |
| `power.wake` | `{ reason: string, wakeSource?: string, at: iso }` — `reason` из `powercfg /lastwake` |
| `power.countdown` | `{ action, secondsLeft, cancellable: boolean }` |
| `status` | `StatusSnapshot` |
| `scenario.progress` | `{ runId, id, stepIndex, total, step: ScenarioStep, status: "ok"\|"error"\|"skipped", message?: string }` |
| `scenario.done` | `{ runId, id, ok: boolean, steps: StepResult[], startedAt, finishedAt }` |
| `wizard.result` | `{ checks: CheckResult[] }` |
| `wake.test.result` | `{ testId, mode, ok, elapsedMs, wakeReason? }` |
| `schedule.fired` | `{ id, action, at: iso }` |
| `notify` | `{ text: string, level: "info"\|"warn"\|"error" }` |
| `agent.hello` | `{ version, pcName, features }` — отправляется сразу после соединения |

---

## 6. Служебные сообщения `type="sys"` (устройство ↔ сервер)

Поле `enc` отсутствует, вместо него `sys` и `data`.

```jsonc
{ "v":1, "id":"…", "type":"sys", "from":"<deviceId>", "to":"server", "ts":1757894400,
  "sys":"hello", "data": { "role":"pc", "token":"<deviceToken>", "version":"1.0.0" } }
```

| `sys` | Направление | `data` |
|---|---|---|
| `hello` | устройство → сервер | `{ role, token, version, name?, mac?, lanIps?: string[] }` |
| `hello.ok` | сервер → устройство | `{ serverTime: number, peers: PeerState[] }` |
| `ping` / `pong` | обе | `{ t: number }` — каждые 30 с, таймаут 90 с |
| `heartbeat` | устройство → сервер | `{ state, uptimeSec, load? }` |
| `peer.state` | сервер → устройство | `{ deviceId, online: boolean, state, lastSeen: iso }` |
| `route.fail` | сервер → отправителю | `{ re: "<id>", reason: "offline"\|"unknown"\|"denied" }` |
| `pair.offer` | ПК → сервер | `{ token, exp }` — агент публикует одноразовый код, чтобы сервер принял `claim` |
| `pair.claim` | сервер → ПК | `{ token, phoneId, phonePub, phoneName, pairId }` |
| `pair.result` | ПК → сервер | `{ pairId, ok, pcPub?, pcName?, mac?, lanIps?, error? }` |
| `wol.request` | сервер → мост | `{ requestId, mac, broadcast?: string, repeat?: number = 3 }` |
| `wol.result` | мост → сервер | `{ requestId, sent: boolean, error?: string }` |
| `bridge.ping` | сервер → мост | `{ requestId, ip }` — проверка ПК в локальной сети |
| `bridge.pong` | мост → сервер | `{ requestId, alive: boolean, rttMs?: number }` |
| `schedule.push` | сервер → ПК | `{ schedules: Schedule[] }` — синхронизация расписаний |
| `error` | сервер → устройство | `{ code, message }` |

---

## 7. Структуры данных

### 7.1 StatusSnapshot
```jsonc
{
  "pcId": "…",
  "pcName": "DESKTOP-ABC",
  "state": "running",
  "uptimeSec": 41230,
  "cpuPercent": 12.4,
  "ramUsedMb": 9310,
  "ramTotalMb": 32690,
  "battery": { "percent": 87, "charging": true },
  "activeWindow": "obs64 — OBS Studio",
  "userSession": { "active": true, "userName": "Marakabo", "locked": false },
  "network": { "mac": "AA:BB:CC:DD:EE:FF", "ip": "192.168.1.50", "adapter": "Ethernet", "isWired": true },
  "agentVersion": "1.0.0",
  "readiness": { "ok": 9, "warn": 2, "fail": 1, "wakeReady": true },
  "at": "2026-09-15T20:00:00Z"
}
```

### 7.2 CheckResult
```jsonc
{
  "id": "hibernate-enabled",
  "title": "Гибернация включена",
  "status": "ok",
  "detail": "powercfg /a: гибернация доступна",
  "canFix": true,
  "fixHint": "Включить гибернацию (powercfg /h on)",
  "docUrl": "https://…",
  "requiresElevation": true,
  "checkedAt": "2026-09-15T20:00:00Z"
}
```

`status`: `ok` | `warn` | `fail` | `unknown`.

Идентификаторы проверок (порядок = порядок в мастере):
`hibernate-enabled`, `sleep-states`, `wake-timers`, `fast-startup`,
`nic-wake-armed`, `nic-magic-packet`, `wired-connection`, `network-identity`,
`static-ip`, `bios-wol`, `server-link`, `autologon`.

### 7.3 AppEntry
```jsonc
{ "name": "OBS Studio",
  "path": "C:\\Program Files\\obs-studio\\bin\\64bit\\obs64.exe",
  "args": "",
  "cwd": "C:\\Program Files\\obs-studio\\bin\\64bit",
  "iconB64": "<png base64 32x32>",
  "source": "start-menu",
  "uwpAppId": null }
```

`source`: `start-menu` | `registry` | `uwp` | `running`.

### 7.4 Scenario
```jsonc
{
  "id": "evening",
  "name": "Вечер: игры и запись",
  "icon": "🎮",
  "favorite": true,
  "onError": "continue",
  "steps": [],
  "updatedAt": "2026-09-15T20:00:00Z"
}
```

`onError`: `continue` | `stop`.

### 7.5 ScenarioStep

| `type` | Поля | Где исполняется |
|---|---|---|
| `launch` | `path`, `args?`, `cwd?`, `window?: "normal"\|"min"\|"max"`, `waitForExit?: boolean` | сеанс пользователя |
| `open` | `path` (файл или папка) | сеанс пользователя |
| `url` | `url`, `browser?: string` (путь к exe; по умолчанию системный) | сеанс пользователя |
| `close` | `process` (имя без `.exe`), `force?: boolean` | сеанс пользователя |
| `wait` | `seconds` | служба |
| `volume` | `level?: 0..100`, `mute?: boolean` | сеанс пользователя |
| `command` | `shell: "cmd"\|"powershell"`, `command`, `hidden?: boolean` | служба (по умолчанию **выключено**) |
| `power` | `action: "sleep"\|"hibernate"\|"shutdown"\|"lock"`, `delaySec?` | служба |
| `notify` | `text`, `notifyLevel?` (`info`/`warn`/`error`) | телефон (через сервер) |
| `uwp` | `appId` | сеанс пользователя |

Каждый шаг может иметь `id?`, `title?`, `enabled?: boolean = true`,
`timeoutSec?: number = 60`, `continueOnError?: boolean` (перекрывает `onError`).

> `volume` использует `level` (число 0–100), а `notify` — `notifyLevel`
> (строка важности). Имена разные, потому что шаг — один объект с общим
> набором полей, и два разных смысла у одного `level` не ужились бы.

### 7.6 Schedule
```jsonc
{
  "id": "wk-night",
  "name": "Будни, 23:00 — включить",
  "enabled": true,
  "action": "wake",
  "scenarioId": "evening",
  "time": "23:00",
  "days": [1,2,3,4,5],
  "date": null,
  "wakeBeforeSec": 0,
  "syncedAt": "2026-09-15T20:00:00Z",
  "lastFiredAt": null,
  "updatedAt": "2026-09-15T19:00:00Z"
}
```

* `action`: `wake` | `shutdown` | `sleep` | `hibernate` | `reboot` | `scenario`
* `time` — локальное время ПК, `days` — `0=Вс … 6=Сб`; пустой массив + `date` = разовое
* `wakeBeforeSec` — запас на прогрев (для `wake`)
* `synced = syncedAt >= updatedAt`. Телефон показывает ⚠️, если расписание ещё
  не подтверждено агентом.

### 7.7 AgentSettings
```jsonc
{
  "countdownSec": 30,
  "allowShellSteps": false,
  "allowAutoLogon": false,
  "relayUrl": "wss://relay.example.com",
  "lanPort": 8760,
  "lanEnabled": true,
  "heartbeatSec": 30,
  "language": "ru",
  "wakeTimersOnBattery": false,
  "logLevel": "info"
}
```

---

## 8. Сопряжение

### 8.1 QR-код

Агент показывает строку (она же в QR):

```
pcmate://pair?d=<base64url(JSON)>
```

JSON:
```jsonc
{
  "v": 1,
  "pcId": "…",
  "pcName": "DESKTOP-ABC",
  "pub": "<base64url X25519 pub>",
  "token": "<base64url 16B одноразовый>",
  "exp": 1757894700,
  "relay": "wss://relay.example.com",
  "lan": ["192.168.1.50:8760"],
  "mac": "AA:BB:CC:DD:EE:FF",
  "fp": "K7M2-9QX4-11BC"
}
```

### 8.2 Последовательность

```
ПК                        Сервер                      Телефон
 |  sys hello (role=pc)      |                            |
 |-------------------------->|                            |
 |  POST /pair/offer {token} |                            |
 |-------------------------->|                            |
 |                           |      [QR отсканирован]     |
 |                           |<-- POST /pair/claim -------|
 |<-- sys pair.claim --------|                            |
 |  (проверка token + exp)   |                            |
 |--- sys pair.result ------>|                            |
 |                           |-- 200 {pcPub, pcName, …} ->|
 |                           |                            |
 |  -- оба вычисляют shared, показывают отпечаток --      |
```

В локальном режиме (этап 1) телефон вместо сервера обращается напрямую:
`POST http://<pc-ip>:8760/pair/claim` с тем же телом.

Токен одноразовый: после успешного `pair.result` агент его удаляет.

---

## 9. Wake-on-LAN

Магический пакет: `FF×6 || MAC×16` (102 байта), UDP на порты **9** и **7**,
адрес — **широковещательный адрес подсети** (например `192.168.1.255`) и
дополнительно `255.255.255.255`. Повтор **3 раза с интервалом 1 с**.

Кто отправляет:
1. **Телефон**, если он в той же подсети (сравнение первых 24 бит IP) — мгновенно.
2. **Мост** по команде `sys wol.request` — всегда.
3. **Другой ПК** с установленным агентом в той же сети (опция).

После отправки инициатор ждёт до **90 с** события `agent.hello`/`power.wake`
от целевого ПК. Если его нет — считаем пробуждение неудачным и показываем
диагностику (ссылку на мастер, пункт `bios-wol`).

---

## 10. HTTP API сервера

Базовый префикс `/api/v1`. Аутентификация: `Authorization: Bearer <deviceToken>`.

| Метод | Путь | Тело / ответ |
|---|---|---|
| `GET` | `/health` | `{ ok: true, version, uptimeSec }` |
| `POST` | `/devices/register` | `{ role, name, pub, mac?, lanIps? }` → `{ deviceId, deviceToken }` |
| `POST` | `/devices/refresh` | `{ name?, lanIps?, mac? }` → `{ ok }` |
| `GET` | `/devices` | `{ devices: PeerState[] }` — только спаренные с вызывающим |
| `DELETE` | `/devices/:id` | отзыв сопряжения → `{ ok }` |
| `POST` | `/pair/offer` | (агент) `{ token, exp }` → `{ ok }` |
| `POST` | `/pair/claim` | (телефон) `{ token, phonePub, phoneName }` → `{ pairId, pcId, pcName, pcPub, mac, lanIps }` |
| `GET` | `/schedules?pcId=` | `{ schedules: Schedule[] }` |
| `PUT` | `/schedules/:id` | `{ schedule }` → `{ ok, synced }` |
| `DELETE` | `/schedules/:id` | `{ ok }` |
| `POST` | `/wake` | `{ pcId }` → `{ ok, via: "bridge"\|"none", requestId }` |
| `GET` | `/events?pcId=&limit=` | `{ events: EventRecord[] }` |
| `POST` | `/push/register` | `{ platform: "fcm"\|"rustore"\|"apns", token }` → `{ ok }` |

`EventRecord`:
```jsonc
{ "id": 1042, "pcId": "…", "kind": "power.wake", "summary": "ПК проснулся (Wake Timer)",
  "at": "2026-09-15T20:00:00Z", "source": "pc" }
```

`source`: `pc` | `phone` | `server` | `bridge`.

Сервер **никогда** не хранит расшифрованное содержимое `cmd`/`res`/`evt`.
`EventRecord` формируется из явных незашифрованных `sys`-сообщений.

---

## 11. Версионирование

* `v` в конверте = мажорная версия протокола. Устройство с меньшей версией
  получает `sys error { code: "E_VERSION" }`.
* Неизвестные поля JSON игнорируются (forward-compatible).
* Неизвестные `cmd` → `E_UNKNOWN_CMD`, неизвестные `evt` → игнорируются.
