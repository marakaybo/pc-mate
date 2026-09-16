/**
 * PC MATE — мост пробуждения на ESP32.
 *
 * Зачем он нужен: выключенный компьютер не принимает команды из интернета —
 * жива только сетевая карта, которая слушает магический пакет Wake-on-LAN,
 * а тот не выходит за пределы локальной сети. Мост всегда включён дома,
 * держит соединение с сервером PC MATE и по команде будит компьютер.
 *
 * Что делает прошивка:
 *   1. При первом запуске поднимает точку доступа для настройки (WiFiManager).
 *   2. Регистрируется на сервере и получает токен устройства.
 *   3. Держит WebSocket и отвечает на sys-сообщения wol.request и bridge.ping.
 *
 * Питание: подойдёт любое зарядное на 5 В — мост потребляет доли ватта.
 */

#include <Arduino.h>
#include <WiFi.h>
#include <WiFiClientSecure.h>
#include <HTTPClient.h>
#include <WiFiUdp.h>
#include <WebSocketsClient.h>
#include <ArduinoJson.h>
#include <Preferences.h>
#include <WiFiManager.h>

#ifndef PCMATE_VERSION
#define PCMATE_VERSION "1.0.0"
#endif

namespace {

constexpr int kProtocolVersion = 1;
constexpr uint16_t kWolPorts[] = {9, 7};
constexpr int kWolRepeat = 3;
constexpr uint32_t kWolIntervalMs = 1000;
constexpr uint32_t kHeartbeatIntervalMs = 30000;
constexpr uint32_t kStatusLedIntervalMs = 1000;
constexpr int kLedPin = 2;  // встроенный светодиод на большинстве плат ESP32

Preferences prefs;
WebSocketsClient webSocket;
WiFiUDP udp;

String relayUrl;       // wss://relay.example.com
String pcId;           // идентификатор компьютера (из приложения на телефоне)
String deviceId;       // идентификатор моста
String deviceToken;    // токен для сервера
String publicKey;      // случайная строка: мост не участвует в сквозном шифровании
String registrationSecret;

bool connected = false;
uint32_t lastHeartbeat = 0;
uint32_t lastLedToggle = 0;

// --------------------------------------------------------------------- утилиты

String randomToken(size_t bytes) {
  static const char kAlphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
  String out;
  out.reserve(bytes);
  for (size_t i = 0; i < bytes; i++) out += kAlphabet[esp_random() % (sizeof(kAlphabet) - 1)];
  return out;
}

String makeUuid() {
  char buffer[37];
  snprintf(buffer, sizeof(buffer), "%08x-%04x-4%03x-%04x-%08x%04x",
           esp_random(), esp_random() & 0xFFFF, esp_random() & 0x0FFF,
           (esp_random() & 0x3FFF) | 0x8000, esp_random(), esp_random() & 0xFFFF);
  return String(buffer);
}

/** wss://host/path → https://host/path (для HTTP-запросов регистрации). */
String httpBase(const String& url) {
  String result = url;
  result.trim();
  while (result.endsWith("/")) result.remove(result.length() - 1);
  if (result.endsWith("/ws")) result.remove(result.length() - 3);

  if (result.startsWith("wss://")) return "https://" + result.substring(6);
  if (result.startsWith("ws://")) return "http://" + result.substring(5);
  if (result.startsWith("http")) return result;
  return "https://" + result;
}

bool parseMac(const String& mac, uint8_t out[6]) {
  int index = 0;
  uint8_t value = 0;
  int nibbles = 0;

  for (size_t i = 0; i < mac.length() && index < 6; i++) {
    const char c = mac[i];
    int digit = -1;
    if (c >= '0' && c <= '9') digit = c - '0';
    else if (c >= 'a' && c <= 'f') digit = c - 'a' + 10;
    else if (c >= 'A' && c <= 'F') digit = c - 'A' + 10;
    else continue;

    value = static_cast<uint8_t>((value << 4) | digit);
    if (++nibbles == 2) {
      out[index++] = value;
      value = 0;
      nibbles = 0;
    }
  }

  return index == 6;
}

/** Широковещательный адрес текущей подсети: именно туда уходит магический пакет. */
IPAddress broadcastAddress() {
  const IPAddress ip = WiFi.localIP();
  const IPAddress mask = WiFi.subnetMask();
  IPAddress result;

  for (int i = 0; i < 4; i++) result[i] = ip[i] | (~mask[i] & 0xFF);
  return result;
}

int sendMagicPacket(const String& mac, const String& broadcastOverride, int repeat) {
  uint8_t target[6];
  if (!parseMac(mac, target)) {
    Serial.printf("[WOL] Некорректный MAC: %s\n", mac.c_str());
    return -1;
  }

  uint8_t packet[102];
  memset(packet, 0xFF, 6);
  for (int i = 0; i < 16; i++) memcpy(packet + 6 + i * 6, target, 6);

  IPAddress destination = broadcastAddress();
  if (broadcastOverride.length() > 0) destination.fromString(broadcastOverride);

  int sent = 0;
  const int attempts = repeat > 0 ? repeat : kWolRepeat;

  for (int attempt = 0; attempt < attempts; attempt++) {
    for (uint16_t port : kWolPorts) {
      if (udp.beginPacket(destination, port) == 1) {
        udp.write(packet, sizeof(packet));
        if (udp.endPacket() == 1) sent++;
      }
      // Дублируем на 255.255.255.255: некоторые роутеры фильтруют адрес подсети.
      if (udp.beginPacket(IPAddress(255, 255, 255, 255), port) == 1) {
        udp.write(packet, sizeof(packet));
        if (udp.endPacket() == 1) sent++;
      }
    }
    if (attempt < attempts - 1) delay(kWolIntervalMs);
  }

  Serial.printf("[WOL] %s → %s, отправлено пакетов: %d\n",
                mac.c_str(), destination.toString().c_str(), sent);
  return sent;
}

/** Проверка, отвечает ли компьютер: стучимся в типичные открытые порты. */
bool probeHost(const String& ip, uint32_t& rttMs) {
  const uint16_t ports[] = {445, 135, 3389, 22, 8760};
  IPAddress address;
  if (!address.fromString(ip)) return false;

  for (uint16_t port : ports) {
    const uint32_t started = millis();
    WiFiClient client;
    client.setTimeout(1);
    if (client.connect(address, port, 1000)) {
      rttMs = millis() - started;
      client.stop();
      return true;
    }
  }

  return false;
}

// --------------------------------------------------------------------- протокол

void sendSys(const char* sys, const JsonDocument& data, const String& replyTo = "") {
  JsonDocument message;
  message["v"] = kProtocolVersion;
  message["id"] = makeUuid();
  message["type"] = "sys";
  message["from"] = deviceId;
  message["to"] = "server";
  message["ts"] = static_cast<uint32_t>(time(nullptr));
  message["sys"] = sys;
  message["data"] = data;
  if (replyTo.length() > 0) message["re"] = replyTo;

  String payload;
  serializeJson(message, payload);
  webSocket.sendTXT(payload);
}

void sendHello() {
  JsonDocument data;
  data["role"] = "bridge";
  data["token"] = deviceToken;
  data["version"] = PCMATE_VERSION;
  data["name"] = "PC MATE Bridge (ESP32)";

  JsonArray ips = data["lanIps"].to<JsonArray>();
  ips.add(WiFi.localIP().toString());

  sendSys("hello", data);
}

void handleMessage(const String& raw) {
  JsonDocument message;
  if (deserializeJson(message, raw) != DeserializationError::Ok) return;
  if (strcmp(message["type"] | "", "sys") != 0) return;

  const char* sys = message["sys"] | "";
  const String requestId = message["id"] | "";
  JsonObject data = message["data"].as<JsonObject>();

  if (strcmp(sys, "ping") == 0) {
    JsonDocument reply;
    reply["t"] = static_cast<uint32_t>(millis() / 1000);
    sendSys("pong", reply);

  } else if (strcmp(sys, "hello.ok") == 0) {
    Serial.println("[WS] Сервер подтвердил подключение");

  } else if (strcmp(sys, "wol.request") == 0) {
    const String mac = data["mac"] | "";
    const String broadcast = data["broadcast"] | "";
    const int repeat = data["repeat"] | kWolRepeat;

    const int sent = sendMagicPacket(mac, broadcast, repeat);

    JsonDocument reply;
    reply["requestId"] = data["requestId"] | "";
    reply["sent"] = sent > 0;
    reply["packets"] = sent > 0 ? sent : 0;
    if (sent < 0) reply["error"] = "Некорректный MAC-адрес";
    sendSys("wol.result", reply, requestId);

  } else if (strcmp(sys, "bridge.ping") == 0) {
    uint32_t rtt = 0;
    const bool alive = probeHost(data["ip"] | "", rtt);

    JsonDocument reply;
    reply["requestId"] = data["requestId"] | "";
    reply["alive"] = alive;
    if (alive) reply["rttMs"] = rtt;
    sendSys("bridge.pong", reply, requestId);

  } else if (strcmp(sys, "error") == 0) {
    Serial.printf("[WS] Сервер сообщил об ошибке: %s\n", (const char*)(data["message"] | ""));
  }
}

void onWebSocketEvent(WStype_t type, uint8_t* payload, size_t length) {
  switch (type) {
    case WStype_CONNECTED:
      connected = true;
      Serial.println("[WS] Соединение установлено");
      sendHello();
      break;

    case WStype_DISCONNECTED:
      connected = false;
      Serial.println("[WS] Соединение потеряно");
      break;

    case WStype_TEXT:
      handleMessage(String(reinterpret_cast<char*>(payload), length));
      break;

    case WStype_ERROR:
      Serial.println("[WS] Ошибка соединения");
      break;

    default:
      break;
  }
}

// --------------------------------------------------------------------- настройка

bool registerBridge() {
  if (deviceToken.length() > 0) return true;

  const String url = httpBase(relayUrl) + "/api/v1/devices/register";
  Serial.printf("[HTTP] Регистрируем мост: %s\n", url.c_str());

  JsonDocument body;
  body["deviceId"] = deviceId;
  body["role"] = "bridge";
  body["name"] = "PC MATE Bridge (ESP32)";
  body["pub"] = publicKey;
  if (registrationSecret.length() > 0) body["secret"] = registrationSecret;

  String payload;
  serializeJson(body, payload);

  HTTPClient http;
  WiFiClientSecure secure;
  secure.setInsecure();  // сертификат проверяет сервер; мост доверяет своей сети

  const bool https = url.startsWith("https");
  if (https) http.begin(secure, url);
  else http.begin(url);

  http.addHeader("Content-Type", "application/json");
  const int status = http.POST(payload);

  if (status != 200) {
    Serial.printf("[HTTP] Регистрация не удалась: %d %s\n", status, http.getString().c_str());
    http.end();
    return false;
  }

  JsonDocument response;
  const DeserializationError error = deserializeJson(response, http.getString());
  http.end();
  if (error) return false;

  deviceToken = String((const char*)(response["deviceToken"] | ""));
  deviceId = String((const char*)(response["deviceId"] | deviceId.c_str()));

  prefs.putString("token", deviceToken);
  prefs.putString("deviceId", deviceId);
  Serial.printf("[HTTP] Мост зарегистрирован: %s\n", deviceId.c_str());
  return deviceToken.length() > 0;
}

void bindToPc() {
  if (pcId.length() == 0 || deviceToken.length() == 0) return;

  const String url = httpBase(relayUrl) + "/api/v1/bridges/bind";
  JsonDocument body;
  body["bridgeId"] = deviceId;
  body["pcId"] = pcId;

  String payload;
  serializeJson(body, payload);

  HTTPClient http;
  WiFiClientSecure secure;
  secure.setInsecure();

  if (url.startsWith("https")) http.begin(secure, url);
  else http.begin(url);

  http.addHeader("Content-Type", "application/json");
  http.addHeader("Authorization", "Bearer " + deviceToken);
  const int status = http.POST(payload);

  Serial.printf("[HTTP] Привязка к компьютеру %s: %d\n", pcId.c_str(), status);
  http.end();
}

void connectWebSocket() {
  String url = relayUrl;
  url.trim();

  bool secure = true;
  if (url.startsWith("wss://")) url = url.substring(6);
  else if (url.startsWith("https://")) url = url.substring(8);
  else if (url.startsWith("ws://")) { url = url.substring(5); secure = false; }
  else if (url.startsWith("http://")) { url = url.substring(7); secure = false; }

  String host = url;
  uint16_t port = secure ? 443 : 80;

  const int slash = host.indexOf('/');
  if (slash >= 0) host = host.substring(0, slash);

  const int colon = host.indexOf(':');
  if (colon >= 0) {
    port = host.substring(colon + 1).toInt();
    host = host.substring(0, colon);
  }

  const String path = "/ws?token=" + deviceToken;
  Serial.printf("[WS] Подключаемся: %s%s:%u%s\n", secure ? "wss://" : "ws://", host.c_str(), port, "/ws");

  if (secure) webSocket.beginSSL(host.c_str(), port, path.c_str());
  else webSocket.begin(host.c_str(), port, path.c_str());

  webSocket.onEvent(onWebSocketEvent);
  webSocket.setReconnectInterval(5000);
  webSocket.enableHeartbeat(20000, 10000, 3);
}

}  // namespace

void setup() {
  Serial.begin(115200);
  delay(300);
  Serial.printf("\nPC MATE — мост пробуждения %s\n", PCMATE_VERSION);

  pinMode(kLedPin, OUTPUT);
  digitalWrite(kLedPin, LOW);

  prefs.begin("pcmate", false);
  deviceId = prefs.getString("deviceId", "");
  deviceToken = prefs.getString("token", "");
  publicKey = prefs.getString("pub", "");
  relayUrl = prefs.getString("relay", "");
  pcId = prefs.getString("pcId", "");
  registrationSecret = prefs.getString("secret", "");

  if (deviceId.length() == 0) {
    deviceId = makeUuid();
    prefs.putString("deviceId", deviceId);
  }
  if (publicKey.length() == 0) {
    publicKey = randomToken(43);
    prefs.putString("pub", publicKey);
  }

  // Портал настройки: точка доступа «PC MATE Bridge», пароль pcmate123.
  WiFiManager wm;
  WiFiManagerParameter relayParam("relay", "Адрес сервера (wss://...)", relayUrl.c_str(), 120);
  WiFiManagerParameter pcParam("pcid", "Идентификатор компьютера", pcId.c_str(), 64);
  WiFiManagerParameter secretParam("secret", "Секрет регистрации (если задан)", registrationSecret.c_str(), 64);

  wm.addParameter(&relayParam);
  wm.addParameter(&pcParam);
  wm.addParameter(&secretParam);
  wm.setConfigPortalTimeout(300);

  if (!wm.autoConnect("PC MATE Bridge", "pcmate123")) {
    Serial.println("[WiFi] Настройка не завершена — перезагружаемся");
    delay(2000);
    ESP.restart();
  }

  if (String(relayParam.getValue()) != relayUrl) {
    relayUrl = relayParam.getValue();
    prefs.putString("relay", relayUrl);
    // Сервер сменился — старый токен больше не действует.
    deviceToken = "";
    prefs.remove("token");
  }
  if (String(pcParam.getValue()) != pcId) {
    pcId = pcParam.getValue();
    prefs.putString("pcId", pcId);
  }
  if (String(secretParam.getValue()) != registrationSecret) {
    registrationSecret = secretParam.getValue();
    prefs.putString("secret", registrationSecret);
  }

  Serial.printf("[WiFi] Подключено: %s, IP %s\n", WiFi.SSID().c_str(), WiFi.localIP().toString().c_str());

  configTime(0, 0, "pool.ntp.org", "time.google.com");
  udp.begin(0);

  if (relayUrl.length() == 0) {
    Serial.println("[!] Не задан адрес сервера. Зажмите BOOT и перезагрузите плату, чтобы открыть портал настройки.");
    return;
  }

  if (!registerBridge()) {
    Serial.println("[!] Регистрация не удалась — повторим после перезагрузки");
    delay(10000);
    ESP.restart();
  }

  bindToPc();
  connectWebSocket();
}

void loop() {
  webSocket.loop();

  const uint32_t now = millis();

  if (connected && now - lastHeartbeat > kHeartbeatIntervalMs) {
    lastHeartbeat = now;
    JsonDocument data;
    data["state"] = "running";
    data["uptimeSec"] = now / 1000;
    data["rssi"] = WiFi.RSSI();
    sendSys("heartbeat", data);
  }

  // Светодиод: горит ровно — связь есть, мигает — нет.
  if (now - lastLedToggle > kStatusLedIntervalMs) {
    lastLedToggle = now;
    digitalWrite(kLedPin, connected ? HIGH : !digitalRead(kLedPin));
  }

  if (WiFi.status() != WL_CONNECTED) {
    Serial.println("[WiFi] Потеряно подключение — переподключаемся");
    WiFi.reconnect();
    delay(2000);
  }
}
