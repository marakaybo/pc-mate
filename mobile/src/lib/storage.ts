import AsyncStorage from '@react-native-async-storage/async-storage';
import * as SecureStore from 'expo-secure-store';
import { generateKeyPair, publicKeyFrom, toBase64Url, fromBase64Url } from './crypto';
import type { PairedPc } from './types';

/**
 * Хранение на телефоне: приватный ключ и ключи сессий — только в Keystore/Keychain,
 * всё остальное (кэш статуса, настройки) — в обычном хранилище.
 */

const KEY_IDENTITY_PRIVATE = 'pcmate.identity.private';
const KEY_DEVICE_TOKEN_PREFIX = 'pcmate.token.';
const KEY_DEVICE_ID = 'pcmate.deviceId';
const KEY_PAIRED = 'pcmate.paired';
const KEY_SETTINGS = 'pcmate.settings';
const KEY_ACTIVE_PC = 'pcmate.activePc';

export interface AppSettings {
  language: 'ru' | 'en';
  biometricLock: boolean;
  confirmShutdown: boolean;
  preferLan: boolean;
  notifications: boolean;
}

export const defaultSettings: AppSettings = {
  language: 'ru',
  biometricLock: false,
  confirmShutdown: true,
  preferLan: true,
  notifications: true
};

async function secureGet(key: string): Promise<string | null> {
  try {
    return await SecureStore.getItemAsync(key);
  } catch {
    return null;
  }
}

async function secureSet(key: string, value: string): Promise<void> {
  await SecureStore.setItemAsync(key, value, {
    keychainAccessible: SecureStore.WHEN_UNLOCKED_THIS_DEVICE_ONLY
  });
}

/** Долговременная личность телефона: та же пара ключей во всех сопряжениях. */
export async function loadIdentity(): Promise<{ deviceId: string; privateKey: Uint8Array; publicKey: Uint8Array }> {
  let deviceId = await AsyncStorage.getItem(KEY_DEVICE_ID);
  let privateB64 = await secureGet(KEY_IDENTITY_PRIVATE);

  if (!privateB64) {
    const pair = generateKeyPair();
    privateB64 = toBase64Url(pair.privateKey);
    await secureSet(KEY_IDENTITY_PRIVATE, privateB64);
  }

  if (!deviceId) {
    deviceId = createUuid();
    await AsyncStorage.setItem(KEY_DEVICE_ID, deviceId);
  }

  const privateKey = fromBase64Url(privateB64);
  return { deviceId, privateKey, publicKey: publicKeyFrom(privateKey) };
}

export async function resetIdentity(): Promise<void> {
  await SecureStore.deleteItemAsync(KEY_IDENTITY_PRIVATE).catch(() => undefined);
  await AsyncStorage.multiRemove([KEY_DEVICE_ID, KEY_PAIRED, KEY_ACTIVE_PC]);
}

export async function loadPairedPcs(): Promise<PairedPc[]> {
  const raw = await AsyncStorage.getItem(KEY_PAIRED);
  if (!raw) return [];
  try {
    return JSON.parse(raw) as PairedPc[];
  } catch {
    return [];
  }
}

export async function savePairedPcs(list: PairedPc[]): Promise<void> {
  await AsyncStorage.setItem(KEY_PAIRED, JSON.stringify(list));
}

export async function upsertPairedPc(pc: PairedPc): Promise<PairedPc[]> {
  const list = await loadPairedPcs();
  const next = [...list.filter((p) => p.pcId !== pc.pcId), pc];
  await savePairedPcs(next);
  return next;
}

export async function removePairedPc(pcId: string): Promise<PairedPc[]> {
  const list = (await loadPairedPcs()).filter((p) => p.pcId !== pcId);
  await savePairedPcs(list);
  await SecureStore.deleteItemAsync(KEY_DEVICE_TOKEN_PREFIX + safeKey(pcId)).catch(() => undefined);
  return list;
}

export async function getActivePcId(): Promise<string | null> {
  return AsyncStorage.getItem(KEY_ACTIVE_PC);
}

export async function setActivePcId(pcId: string | null): Promise<void> {
  if (pcId) await AsyncStorage.setItem(KEY_ACTIVE_PC, pcId);
  else await AsyncStorage.removeItem(KEY_ACTIVE_PC);
}

/** Токен телефона для конкретного сервера-ретранслятора. */
export async function getRelayToken(relayUrl: string): Promise<string | null> {
  return secureGet(KEY_DEVICE_TOKEN_PREFIX + safeKey(relayUrl));
}

export async function setRelayToken(relayUrl: string, token: string): Promise<void> {
  await secureSet(KEY_DEVICE_TOKEN_PREFIX + safeKey(relayUrl), token);
}

export async function loadSettings(): Promise<AppSettings> {
  const raw = await AsyncStorage.getItem(KEY_SETTINGS);
  if (!raw) return defaultSettings;
  try {
    return { ...defaultSettings, ...(JSON.parse(raw) as Partial<AppSettings>) };
  } catch {
    return defaultSettings;
  }
}

export async function saveSettings(settings: AppSettings): Promise<void> {
  await AsyncStorage.setItem(KEY_SETTINGS, JSON.stringify(settings));
}

/** Кэш последнего статуса, чтобы карточка ПК не была пустой при запуске. */
export async function cacheStatus(pcId: string, status: unknown): Promise<void> {
  await AsyncStorage.setItem(`pcmate.status.${pcId}`, JSON.stringify(status));
}

export async function loadCachedStatus<T>(pcId: string): Promise<T | null> {
  const raw = await AsyncStorage.getItem(`pcmate.status.${pcId}`);
  if (!raw) return null;
  try {
    return JSON.parse(raw) as T;
  } catch {
    return null;
  }
}

/** SecureStore допускает только буквы, цифры, «.», «-» и «_». */
function safeKey(value: string): string {
  return value.replace(/[^A-Za-z0-9._-]/g, '_');
}

function createUuid(): string {
  const bytes = new Uint8Array(16);
  (globalThis as unknown as { crypto: Crypto }).crypto.getRandomValues(bytes);
  bytes[6] = (bytes[6]! & 0x0f) | 0x40;
  bytes[8] = (bytes[8]! & 0x3f) | 0x80;
  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
