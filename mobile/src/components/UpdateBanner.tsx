import React, { useEffect, useState } from 'react';
import { Linking, Pressable, StyleSheet, Text, View } from 'react-native';
import { colors, radius, spacing, typography } from '../theme';
import { Button } from './ui';
import { checkForUpdate, skipVersion, type UpdateInfo } from '../lib/updates';

/**
 * Карточка «вышла новая версия». Нужна, потому что приложение ставится файлом
 * APK с GitHub: магазина, который обновит его сам, здесь нет.
 */
export function UpdateBanner({ compact = false }: { compact?: boolean }) {
  const [update, setUpdate] = useState<UpdateInfo | null>(null);

  useEffect(() => {
    let cancelled = false;

    void checkForUpdate().then((info) => {
      if (!cancelled) setUpdate(info);
    });

    return () => {
      cancelled = true;
    };
  }, []);

  if (!update) return null;

  const open = () => {
    void Linking.openURL(update.apkUrl ?? update.releaseUrl);
  };

  const dismiss = () => {
    void skipVersion(update.version);
    setUpdate(null);
  };

  if (compact) {
    return (
      <Pressable onPress={open} style={styles.compact}>
        <Text style={styles.compactText}>
          Доступна версия {update.version} — нажмите, чтобы скачать
        </Text>
      </Pressable>
    );
  }

  return (
    <View style={styles.card}>
      <Text style={styles.title}>Вышла версия {update.version}</Text>
      <Text style={styles.meta}>Установлена {update.currentVersion}</Text>

      {update.notes ? (
        <Text style={styles.notes} numberOfLines={6}>
          {update.notes}
        </Text>
      ) : null}

      <View style={styles.actions}>
        <Button title="Скачать APK" onPress={open} style={styles.button} />
        <Button title="Пропустить" variant="ghost" onPress={dismiss} style={styles.button} />
      </View>

      <Text style={styles.hint}>
        После загрузки Android попросит разрешить установку из этого источника — это нормально
        для приложений не из магазина.
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  card: {
    backgroundColor: colors.accentSoft + '55',
    borderColor: colors.accent + '66',
    borderWidth: 1,
    borderRadius: radius.lg,
    padding: spacing.lg,
    marginBottom: spacing.md
  },
  title: { ...typography.subheading, color: colors.accent },
  meta: { ...typography.small, color: colors.textMuted, marginTop: 2 },
  notes: { ...typography.small, color: colors.text, marginTop: spacing.md, lineHeight: 19 },
  actions: { flexDirection: 'row', gap: spacing.sm, marginTop: spacing.md },
  button: { flex: 1 },
  hint: { ...typography.tiny, color: colors.textFaint, marginTop: spacing.md, lineHeight: 15 },

  compact: {
    backgroundColor: colors.accentSoft + '44',
    borderColor: colors.accent + '55',
    borderWidth: 1,
    borderRadius: radius.md,
    paddingVertical: spacing.sm,
    paddingHorizontal: spacing.md,
    marginBottom: spacing.md
  },
  compactText: { ...typography.small, color: colors.accent }
});
