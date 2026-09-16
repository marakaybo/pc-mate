import AsyncStorage from '@react-native-async-storage/async-storage';
import Constants from 'expo-constants';

/**
 * Проверка обновлений через GitHub Releases.
 *
 * Приложение раздаётся файлом APK, а не через магазин, поэтому автоматических
 * обновлений нет: если не сказать пользователю, он останется на старой версии
 * навсегда. Проверяем раз в сутки и показываем ненавязчивую карточку.
 */

const CHECK_INTERVAL_MS = 24 * 60 * 60 * 1000;
const KEY_LAST_CHECK = 'pcmate.updates.lastCheck';
const KEY_SKIPPED = 'pcmate.updates.skipped';

export interface UpdateInfo {
  version: string;
  currentVersion: string;
  notes: string;
  releaseUrl: string;
  apkUrl: string | null;
  publishedAt: string;
}

interface GithubAsset {
  name: string;
  browser_download_url: string;
  size: number;
}

interface GithubRelease {
  tag_name: string;
  name: string;
  body: string;
  html_url: string;
  draft: boolean;
  prerelease: boolean;
  published_at: string;
  assets: GithubAsset[];
}

function repository(): { owner: string; repo: string } | null {
  const extra = Constants.expoConfig?.extra as { github?: { owner?: string; repo?: string } } | undefined;
  const owner = extra?.github?.owner;
  const repo = extra?.github?.repo;

  if (!owner || !repo || owner === 'ВАШ_ЛОГИН') return null;
  return { owner, repo };
}

export function currentVersion(): string {
  return Constants.expoConfig?.version ?? '0.0.0';
}

/** Сравнение версий вида 1.2.3: возвращает 1, если a новее b. */
export function compareVersions(a: string, b: string): number {
  const parse = (v: string) =>
    v
      .replace(/^v/i, '')
      .split(/[.\-+]/)
      .map((part) => Number.parseInt(part, 10))
      .filter((n) => Number.isFinite(n));

  const left = parse(a);
  const right = parse(b);

  for (let i = 0; i < Math.max(left.length, right.length); i++) {
    const x = left[i] ?? 0;
    const y = right[i] ?? 0;
    if (x !== y) return x > y ? 1 : -1;
  }
  return 0;
}

/**
 * Возвращает информацию об обновлении или null.
 * Сеть недоступна или репозиторий не настроен — молча возвращаем null:
 * проверка обновлений не должна мешать работе приложения.
 */
export async function checkForUpdate(options: { force?: boolean } = {}): Promise<UpdateInfo | null> {
  const repoInfo = repository();
  if (!repoInfo) return null;

  if (!options.force) {
    const last = Number.parseInt((await AsyncStorage.getItem(KEY_LAST_CHECK)) ?? '0', 10);
    if (Number.isFinite(last) && Date.now() - last < CHECK_INTERVAL_MS) return null;
  }

  let release: GithubRelease;
  try {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 10000);

    const response = await fetch(
      `https://api.github.com/repos/${repoInfo.owner}/${repoInfo.repo}/releases/latest`,
      { headers: { Accept: 'application/vnd.github+json' }, signal: controller.signal }
    );
    clearTimeout(timer);

    if (!response.ok) return null;
    release = (await response.json()) as GithubRelease;
  } catch {
    return null;
  } finally {
    await AsyncStorage.setItem(KEY_LAST_CHECK, String(Date.now())).catch(() => undefined);
  }

  if (release.draft || release.prerelease) return null;

  const latest = release.tag_name.replace(/^v/i, '');
  const current = currentVersion();
  if (compareVersions(latest, current) <= 0) return null;

  if (!options.force) {
    const skipped = await AsyncStorage.getItem(KEY_SKIPPED);
    if (skipped === latest) return null;
  }

  const apk = release.assets.find((a) => a.name.toLowerCase().endsWith('.apk'));

  return {
    version: latest,
    currentVersion: current,
    notes: (release.body ?? '').trim().slice(0, 600),
    releaseUrl: release.html_url,
    apkUrl: apk?.browser_download_url ?? null,
    publishedAt: release.published_at
  };
}

export async function skipVersion(version: string): Promise<void> {
  await AsyncStorage.setItem(KEY_SKIPPED, version);
}

export function releasesUrl(): string | null {
  const repoInfo = repository();
  return repoInfo ? `https://github.com/${repoInfo.owner}/${repoInfo.repo}/releases` : null;
}
