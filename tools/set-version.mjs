/**
 * Проставляет версию во все компоненты PC MATE перед выпуском.
 *
 * Версия должна совпадать везде: установщик Windows, APK и карточка «О программе».
 * Android дополнительно требует versionCode — целое число, которое обязано расти
 * с каждым обновлением, иначе система откажется ставить новую версию поверх старой.
 *
 *   node tools/set-version.mjs 1.2.3
 */
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = dirname(dirname(fileURLToPath(import.meta.url)));

const raw = process.argv[2];
if (!raw) {
  console.error('Использование: node tools/set-version.mjs 1.2.3');
  process.exit(1);
}

const version = raw.replace(/^v/i, '').trim();
if (!/^\d+\.\d+\.\d+$/.test(version)) {
  console.error(`Версия должна быть вида 1.2.3, получено: ${raw}`);
  process.exit(1);
}

const [major, minor, patch] = version.split('.').map(Number);

// 1.2.3 → 10203. Схема выдерживает до 99 минорных и патч-версий и всегда растёт.
const versionCode = major * 10000 + minor * 100 + patch;

function patchJson(path, mutate) {
  const file = join(ROOT, path);
  const json = JSON.parse(readFileSync(file, 'utf8'));
  mutate(json);
  writeFileSync(file, JSON.stringify(json, null, 2) + '\n', 'utf8');
  console.log(`  ${path}`);
}

function patchText(path, replacements) {
  const file = join(ROOT, path);
  let text = readFileSync(file, 'utf8');

  for (const [pattern, replacement] of replacements) {
    if (!pattern.test(text)) {
      console.warn(`  ! в ${path} не найдено совпадение для ${pattern}`);
      continue;
    }
    text = text.replace(pattern, replacement);
  }

  writeFileSync(file, text, 'utf8');
  console.log(`  ${path}`);
}

console.log(`Версия ${version} (versionCode ${versionCode}):`);

patchJson('mobile/app.json', (json) => {
  json.expo.version = version;
  json.expo.android = json.expo.android ?? {};
  json.expo.android.versionCode = versionCode;
  json.expo.ios = json.expo.ios ?? {};
  json.expo.ios.buildNumber = String(versionCode);
});

patchJson('mobile/package.json', (json) => {
  json.version = version;
});

patchJson('server/package.json', (json) => {
  json.version = version;
});

patchText('server/src/config.ts', [[/version: '[\d.]+'/, `version: '${version}'`]]);

patchText('agent/Directory.Build.props', [
  [/<Version>[\d.]+<\/Version>/, `<Version>${version}</Version>`]
]);

patchText('bridge/esp32/platformio.ini', [
  [/-D PCMATE_VERSION=\\"[\d.]+\\"/, `-D PCMATE_VERSION=\\"${version}\\"`]
]);

console.log('\nГотово. Проверьте изменения и создайте тег:');
console.log(`  git commit -am "Версия ${version}"`);
console.log(`  git tag v${version} && git push --follow-tags`);
