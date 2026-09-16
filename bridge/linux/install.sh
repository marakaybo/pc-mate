#!/usr/bin/env bash
# Установка моста пробуждения PC MATE на Raspberry Pi / Linux.
#   sudo ./install.sh wss://relay.example.com <pcId>
set -euo pipefail

RELAY_URL="${1:-}"
PC_ID="${2:-}"

if [[ -z "$RELAY_URL" ]]; then
  echo "Использование: sudo ./install.sh <адрес сервера> [идентификатор ПК]" >&2
  echo "Пример:        sudo ./install.sh wss://relay.example.com 7f3c…" >&2
  exit 1
fi

if [[ $EUID -ne 0 ]]; then
  echo "Запустите с sudo." >&2
  exit 1
fi

echo "==> Ставим зависимости"
apt-get update -qq
apt-get install -y python3 python3-pip >/dev/null
pip3 install --break-system-packages -q websockets || pip3 install -q websockets

echo "==> Создаём пользователя и каталоги"
id -u pcmate &>/dev/null || useradd --system --no-create-home --shell /usr/sbin/nologin pcmate
install -d -o pcmate -g pcmate /opt/pcmate /etc/pcmate

echo "==> Копируем файлы"
install -o pcmate -g pcmate -m 0755 pcmate_bridge.py /opt/pcmate/pcmate_bridge.py
install -m 0644 pcmate-bridge.service /etc/systemd/system/pcmate-bridge.service

echo "==> Пишем настройки"
python3 - "$RELAY_URL" "$PC_ID" <<'PY'
import json, secrets, sys, uuid, os
relay, pc_id = sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else ""
path = "/etc/pcmate/bridge.json"
config = json.load(open(path)) if os.path.exists(path) else {}
config.setdefault("device_id", str(uuid.uuid4()))
config.setdefault("public_key", secrets.token_urlsafe(32))
config.setdefault("device_token", "")
config.setdefault("name", "PC MATE Bridge")
config.setdefault("broadcast", "")
config.setdefault("registration_secret", "")
config["relay_url"] = relay
if pc_id:
    config["pc_id"] = pc_id
config.setdefault("pc_id", "")
json.dump(config, open(path, "w"), indent=2, ensure_ascii=False)
PY
chown pcmate:pcmate /etc/pcmate/bridge.json
chmod 600 /etc/pcmate/bridge.json

echo "==> Запускаем службу"
systemctl daemon-reload
systemctl enable --now pcmate-bridge
sleep 2
systemctl --no-pager --lines=10 status pcmate-bridge || true

echo
echo "Готово. Журнал: journalctl -u pcmate-bridge -f"
