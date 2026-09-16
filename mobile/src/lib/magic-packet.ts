/**
 * Чистая арифметика Wake-on-LAN: сборка магического пакета и работа с адресами.
 * Вынесено отдельно от wol.ts, потому что здесь нет ни одной зависимости
 * от React Native — эти функции можно проверять обычными тестами в Node.
 */

export const WOL_PORTS = [9, 7] as const;
export const WOL_REPEAT = 3;
export const WOL_INTERVAL_MS = 1000;

export function buildMagicPacket(mac: string): Uint8Array {
  const bytes = parseMac(mac);
  const packet = new Uint8Array(6 + 16 * 6);
  packet.fill(0xff, 0, 6);
  for (let i = 0; i < 16; i++) packet.set(bytes, 6 + i * 6);
  return packet;
}

export function parseMac(mac: string): Uint8Array {
  const clean = (mac ?? '').replace(/[^0-9a-fA-F]/g, '');
  if (clean.length !== 12) throw new Error(`Некорректный MAC-адрес: ${mac}`);

  const bytes = new Uint8Array(6);
  for (let i = 0; i < 6; i++) bytes[i] = Number.parseInt(clean.slice(i * 2, i * 2 + 2), 16);
  return bytes;
}

export function formatMac(bytes: Uint8Array): string {
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0').toUpperCase()).join(':');
}

/** Грубая проверка «та же подсеть» по первым трём октетам — этого хватает для домашних сетей. */
export function sameSubnet24(a?: string | null, b?: string | null): boolean {
  if (!a || !b) return false;
  const x = a.split('.');
  const y = b.split('.');
  if (x.length !== 4 || y.length !== 4) return false;
  return x[0] === y[0] && x[1] === y[1] && x[2] === y[2];
}

export function broadcastFor(ip: string): string {
  const parts = ip.split('.');
  if (parts.length !== 4) return '255.255.255.255';
  return `${parts[0]}.${parts[1]}.${parts[2]}.255`;
}

/** Убирает порт из записи вида «192.168.1.50:8760». */
export function stripPort(address: string): string {
  return address.split(':')[0] ?? address;
}
