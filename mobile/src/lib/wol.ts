import * as Network from 'expo-network';
import {
  broadcastFor,
  buildMagicPacket,
  parseMac,
  sameSubnet24,
  stripPort,
  WOL_INTERVAL_MS,
  WOL_PORTS,
  WOL_REPEAT
} from './magic-packet';

/**
 * Wake-on-LAN с телефона. Работает только когда телефон в той же сети, что и ПК:
 * магический пакет живёт внутри локального сегмента и через интернет не проходит.
 * Для включения «из любой точки» нужен мост (bridge/).
 */

export interface WolResult {
  sent: boolean;
  packets: number;
  reason?: string;
}

/**
 * Минимальный контракт UDP-сокета, которым мы пользуемся.
 * Типы react-native-udp наследуются от Node-овского EventEmitter, которого
 * в React Native нет, поэтому описываем нужную часть сами.
 */
interface UdpLike {
  bind(port: number, callback: () => void): void;
  setBroadcast(flag: boolean): void;
  send(
    packet: Uint8Array,
    offset: number,
    length: number,
    port: number,
    address: string,
    callback: (error?: Error | null) => void
  ): void;
  on(event: 'error', listener: (error: Error) => void): void;
  close(): void;
}

/**
 * Отправляет магический пакет. Требует нативный модуль react-native-udp,
 * поэтому в Expo Go не работает — нужен dev build или обычная сборка.
 */
export async function sendMagicPacket(mac: string, targetIp?: string | null): Promise<WolResult> {
  let dgram: typeof import('react-native-udp').default;
  try {
    dgram = (await import('react-native-udp')).default;
  } catch {
    return {
      sent: false,
      packets: 0,
      reason:
        'Модуль отправки UDP недоступен. Включение по Wake-on-LAN с телефона работает в обычной сборке приложения, но не в Expo Go.'
    };
  }

  const state = await Network.getNetworkStateAsync();
  if (!state.isConnected) {
    return { sent: false, packets: 0, reason: 'Нет подключения к сети.' };
  }

  const phoneIp = await getPhoneIp();
  const targets = new Set<string>(['255.255.255.255']);
  if (phoneIp) targets.add(broadcastFor(phoneIp));
  if (targetIp) {
    targets.add(broadcastFor(targetIp));
    targets.add(targetIp); // на случай, если роутер ещё держит ARP-запись
  }

  const packet = buildMagicPacket(mac);
  const socket = dgram.createSocket({ type: 'udp4' }) as unknown as UdpLike;

  return new Promise<WolResult>((resolve) => {
    let packets = 0;
    let finished = false;

    const finish = (result: WolResult) => {
      if (finished) return;
      finished = true;
      try {
        socket.close();
      } catch {
        // сокет мог закрыться сам
      }
      resolve(result);
    };

    const failTimer = setTimeout(
      () => finish({ sent: packets > 0, packets, reason: 'Истекло время ожидания отправки.' }),
      WOL_REPEAT * WOL_INTERVAL_MS + 5000
    );

    socket.on('error', (error: Error) => {
      clearTimeout(failTimer);
      finish({ sent: false, packets, reason: error.message });
    });

    socket.bind(0, () => {
      try {
        socket.setBroadcast(true);
      } catch {
        // некоторые прошивки не дают включить broadcast — пробуем всё равно
      }

      let attempt = 0;
      const burst = () => {
        for (const target of targets) {
          for (const port of WOL_PORTS) {
            socket.send(packet, 0, packet.length, port, target, (error?: Error | null) => {
              if (!error) packets++;
            });
          }
        }

        attempt++;
        if (attempt < WOL_REPEAT) {
          setTimeout(burst, WOL_INTERVAL_MS);
        } else {
          setTimeout(() => {
            clearTimeout(failTimer);
            finish({
              sent: packets > 0,
              packets,
              reason: packets > 0 ? undefined : 'Ни один пакет не ушёл — проверьте права приложения на сеть.'
            });
          }, 600);
        }
      };

      burst();
    });
  });
}

export async function getPhoneIp(): Promise<string | null> {
  try {
    return await Network.getIpAddressAsync();
  } catch {
    return null;
  }
}

export async function isOnWifi(): Promise<boolean> {
  try {
    const state = await Network.getNetworkStateAsync();
    return state.type === Network.NetworkStateType.WIFI && !!state.isConnected;
  } catch {
    return false;
  }
}

export { buildMagicPacket, parseMac, sameSubnet24, broadcastFor, stripPort };
