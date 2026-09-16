#!/usr/bin/env python3
"""
PC MATE — мост пробуждения для Linux / Raspberry Pi.

Зачем он нужен: выключенный компьютер не принимает команды из интернета.
Жива только сетевая карта, которая слушает магический пакет Wake-on-LAN —
а он не выходит за пределы локальной сети. Мост — это маленькое устройство,
которое всегда включено дома, держит соединение с сервером PC MATE и по
команде отправляет магический пакет соседнему компьютеру.

Запуск:
    python3 pcmate_bridge.py --config /etc/pcmate/bridge.json

Первый запуск создаст файл конфигурации-заготовку.
"""
from __future__ import annotations

import argparse
import asyncio
import json
import logging
import os
import re
import secrets
import socket
import struct
import sys
import time
import uuid
from dataclasses import dataclass, asdict
from pathlib import Path
from typing import Any

try:
    import websockets
except ImportError:  # pragma: no cover - подсказка пользователю, а не логика
    print("Не установлен пакет websockets. Установите: pip3 install websockets", file=sys.stderr)
    raise

try:
    from urllib import request as urlrequest
    from urllib import error as urlerror
except ImportError:  # pragma: no cover
    raise

LOG = logging.getLogger("pcmate.bridge")

PROTOCOL_VERSION = 1
WOL_PORTS = (9, 7)
WOL_REPEAT = 3
WOL_INTERVAL_SEC = 1.0
PING_INTERVAL_SEC = 30
RECONNECT_MAX_SEC = 60


# --------------------------------------------------------------------------- конфигурация


@dataclass
class BridgeConfig:
    relay_url: str = "wss://relay.example.com"
    pc_id: str = ""
    device_id: str = ""
    device_token: str = ""
    public_key: str = ""
    registration_secret: str = ""
    name: str = "PC MATE Bridge"
    broadcast: str = ""

    @staticmethod
    def load(path: Path) -> "BridgeConfig":
        if not path.exists():
            config = BridgeConfig(device_id=str(uuid.uuid4()), public_key=secrets.token_urlsafe(32))
            config.save(path)
            LOG.warning("Создан файл настроек %s — впишите адрес сервера и идентификатор компьютера.", path)
            return config

        data = json.loads(path.read_text(encoding="utf-8"))
        config = BridgeConfig(**{k: v for k, v in data.items() if k in BridgeConfig.__annotations__})

        if not config.device_id:
            config.device_id = str(uuid.uuid4())
        if not config.public_key:
            config.public_key = secrets.token_urlsafe(32)

        return config

    def save(self, path: Path) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(asdict(self), indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        try:
            os.chmod(path, 0o600)  # в файле лежит токен устройства
        except OSError:
            pass


# --------------------------------------------------------------------------- Wake-on-LAN


def parse_mac(mac: str) -> bytes:
    clean = re.sub(r"[^0-9a-fA-F]", "", mac or "")
    if len(clean) != 12:
        raise ValueError(f"Некорректный MAC-адрес: {mac!r}")
    return bytes.fromhex(clean)


def build_magic_packet(mac: str) -> bytes:
    return b"\xff" * 6 + parse_mac(mac) * 16


def local_broadcast_addresses() -> list[str]:
    """Широковещательные адреса всех интерфейсов, плюс общий 255.255.255.255."""
    addresses: set[str] = {"255.255.255.255"}

    try:
        import fcntl  # только Linux

        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            for name in os.listdir("/sys/class/net"):
                if name == "lo":
                    continue
                try:
                    packed = fcntl.ioctl(
                        sock.fileno(), 0x8919, struct.pack("256s", name[:15].encode())  # SIOCGIFBRDADDR
                    )
                    addresses.add(socket.inet_ntoa(packed[20:24]))
                except OSError:
                    continue
        finally:
            sock.close()
    except (ImportError, FileNotFoundError, OSError):
        pass

    return sorted(addresses)


def send_magic_packet(mac: str, broadcast: str = "", repeat: int = WOL_REPEAT) -> int:
    """Возвращает число успешно отправленных датаграмм."""
    packet = build_magic_packet(mac)
    targets = [broadcast] if broadcast else local_broadcast_addresses()
    sent = 0

    for attempt in range(max(1, repeat)):
        for address in targets:
            for port in WOL_PORTS:
                try:
                    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
                        sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
                        sock.sendto(packet, (address, port))
                        sent += 1
                except OSError as error:
                    LOG.debug("Не удалось отправить на %s:%s — %s", address, port, error)

        if attempt < repeat - 1:
            time.sleep(WOL_INTERVAL_SEC)

    LOG.info("Магический пакет для %s отправлен %d раз(а) на %s", mac, sent, ", ".join(targets))
    return sent


def ping_host(ip: str, timeout: float = 1.0) -> tuple[bool, float | None]:
    """Проверка, отвечает ли компьютер в локальной сети (TCP-стук по частым портам)."""
    started = time.perf_counter()

    for port in (445, 135, 3389, 22, 8760):
        try:
            with socket.create_connection((ip, port), timeout=timeout):
                return True, (time.perf_counter() - started) * 1000
        except OSError:
            continue

    return False, None


# --------------------------------------------------------------------------- протокол


def envelope(sys_name: str, data: dict[str, Any], device_id: str, reply_to: str | None = None) -> str:
    message: dict[str, Any] = {
        "v": PROTOCOL_VERSION,
        "id": str(uuid.uuid4()),
        "type": "sys",
        "from": device_id,
        "to": "server",
        "ts": int(time.time()),
        "sys": sys_name,
        "data": data,
    }
    if reply_to:
        message["re"] = reply_to
    return json.dumps(message, ensure_ascii=False)


def http_base(relay_url: str) -> str:
    url = relay_url.strip().rstrip("/")
    if url.endswith("/ws"):
        url = url[:-3]
    if url.startswith("wss://"):
        return "https://" + url[6:]
    if url.startswith("ws://"):
        return "http://" + url[5:]
    if url.startswith("http"):
        return url
    return "https://" + url


def ws_url(relay_url: str, token: str) -> str:
    url = relay_url.strip().rstrip("/")
    if url.startswith("http://"):
        url = "ws://" + url[7:]
    elif url.startswith("https://"):
        url = "wss://" + url[8:]
    elif not url.startswith("ws"):
        url = "wss://" + url
    if not url.endswith("/ws"):
        url += "/ws"
    return f"{url}?token={token}"


def post_json(url: str, payload: dict[str, Any], token: str = "") -> dict[str, Any]:
    body = json.dumps(payload).encode("utf-8")
    request = urlrequest.Request(url, data=body, method="POST")
    request.add_header("Content-Type", "application/json")
    if token:
        request.add_header("Authorization", f"Bearer {token}")

    with urlrequest.urlopen(request, timeout=20) as response:
        return json.loads(response.read().decode("utf-8") or "{}")


def register(config: BridgeConfig, path: Path) -> None:
    """Регистрирует мост на сервере и привязывает его к компьютеру."""
    base = http_base(config.relay_url)

    if not config.device_token:
        LOG.info("Регистрируем мост на сервере %s", base)
        result = post_json(
            f"{base}/api/v1/devices/register",
            {
                "deviceId": config.device_id,
                "role": "bridge",
                "name": config.name,
                "pub": config.public_key,
                "secret": config.registration_secret or None,
            },
        )
        config.device_token = result["deviceToken"]
        config.device_id = result.get("deviceId", config.device_id)
        config.save(path)
        LOG.info("Мост зарегистрирован: %s", config.device_id)

    if config.pc_id:
        try:
            post_json(
                f"{base}/api/v1/bridges/bind",
                {"bridgeId": config.device_id, "pcId": config.pc_id},
                config.device_token,
            )
            LOG.info("Мост привязан к компьютеру %s", config.pc_id)
        except urlerror.HTTPError as error:
            LOG.error("Не удалось привязать мост к компьютеру: %s", error)


# --------------------------------------------------------------------------- основной цикл


async def handle_message(websocket: Any, config: BridgeConfig, raw: str) -> None:
    try:
        message = json.loads(raw)
    except json.JSONDecodeError:
        return

    if message.get("type") != "sys":
        return

    kind = message.get("sys")
    data = message.get("data") or {}
    request_id = message.get("id")

    if kind == "ping":
        await websocket.send(envelope("pong", {"t": int(time.time())}, config.device_id))

    elif kind == "hello.ok":
        LOG.info("Сервер подтвердил подключение моста")

    elif kind == "wol.request":
        mac = data.get("mac", "")
        LOG.info("Команда включить компьютер: %s", mac)
        try:
            sent = await asyncio.to_thread(
                send_magic_packet, mac, data.get("broadcast") or config.broadcast, int(data.get("repeat", WOL_REPEAT))
            )
            payload = {"requestId": data.get("requestId"), "sent": sent > 0, "packets": sent}
        except ValueError as error:
            payload = {"requestId": data.get("requestId"), "sent": False, "error": str(error)}

        await websocket.send(envelope("wol.result", payload, config.device_id, reply_to=request_id))

    elif kind == "bridge.ping":
        ip = data.get("ip", "")
        alive, rtt = await asyncio.to_thread(ping_host, ip)
        await websocket.send(
            envelope(
                "bridge.pong",
                {"requestId": data.get("requestId"), "alive": alive, "rttMs": rtt},
                config.device_id,
                reply_to=request_id,
            )
        )

    elif kind == "error":
        LOG.error("Сервер сообщил об ошибке: %s", data)


async def heartbeat(websocket: Any, config: BridgeConfig) -> None:
    while True:
        await asyncio.sleep(PING_INTERVAL_SEC)
        await websocket.send(envelope("heartbeat", {"state": "running"}, config.device_id))


async def run(config: BridgeConfig) -> None:
    delay = 2.0

    while True:
        url = ws_url(config.relay_url, config.device_token)
        try:
            LOG.info("Подключаемся к серверу %s", http_base(config.relay_url))
            async with websockets.connect(
                url,
                additional_headers={"Authorization": f"Bearer {config.device_token}"},
                ping_interval=20,
                ping_timeout=20,
                max_size=512 * 1024,
            ) as websocket:
                await websocket.send(
                    envelope(
                        "hello",
                        {
                            "role": "bridge",
                            "token": config.device_token,
                            "version": "1.0.0",
                            "name": config.name,
                        },
                        config.device_id,
                    )
                )

                LOG.info("Мост на связи и ждёт команд")
                delay = 2.0

                beat = asyncio.create_task(heartbeat(websocket, config))
                try:
                    async for raw in websocket:
                        await handle_message(websocket, config, raw)
                finally:
                    beat.cancel()

        except asyncio.CancelledError:
            raise
        except Exception as error:  # соединение рвётся по множеству причин — все равнозначны
            LOG.warning("Нет связи с сервером (%s). Повтор через %.0f с.", error, delay)

        await asyncio.sleep(delay)
        delay = min(RECONNECT_MAX_SEC, delay * 2)


def main() -> int:
    parser = argparse.ArgumentParser(description="PC MATE — мост пробуждения")
    parser.add_argument(
        "--config",
        default=os.environ.get("PCMATE_BRIDGE_CONFIG", "/etc/pcmate/bridge.json"),
        help="путь к файлу настроек",
    )
    parser.add_argument("--verbose", action="store_true", help="подробный журнал")
    parser.add_argument("--test-wol", metavar="MAC", help="отправить магический пакет и выйти")
    args = parser.parse_args()

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s %(levelname)-7s %(message)s",
        datefmt="%H:%M:%S",
    )

    if args.test_wol:
        sent = send_magic_packet(args.test_wol)
        print(f"Отправлено датаграмм: {sent}")
        return 0 if sent else 1

    path = Path(args.config)
    config = BridgeConfig.load(path)

    if not config.relay_url or "example.com" in config.relay_url:
        LOG.error("Укажите адрес сервера в %s (поле relay_url).", path)
        return 2

    try:
        register(config, path)
    except Exception as error:
        LOG.error("Не удалось зарегистрироваться на сервере: %s", error)
        return 3

    try:
        asyncio.run(run(config))
    except KeyboardInterrupt:
        LOG.info("Остановлено пользователем")

    return 0


if __name__ == "__main__":
    sys.exit(main())
