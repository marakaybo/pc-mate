import React, { useState } from 'react';
import { Alert, Pressable, StyleSheet, Text, View } from 'react-native';
import { useNavigation } from '@react-navigation/native';
import { Button, Card, Screen, Section, Title } from '../components/ui';
import { colors, radius, spacing, typography } from '../theme';
import { plural, t } from '../i18n';
import { useStore } from '../state/store';
import { formatTime } from '../lib/format';

const PRESETS = [15, 30, 45, 60, 90, 120];

/**
 * «Буду через N минут»: компьютер просыпается по таймеру Windows заранее
 * и выполняет сценарий, чтобы к приходу всё было открыто.
 */
export function ArriveScreen() {
  const navigation = useNavigation();
  const scenarios = useStore((s) => s.scenarios);
  const arriveAt = useStore((s) => s.arriveAt);
  const setArrive = useStore((s) => s.setArrive);
  const cancelArrive = useStore((s) => s.cancelArrive);
  const connection = useStore((s) => s.connection);

  const [minutes, setMinutes] = useState(30);
  const [scenarioId, setScenarioId] = useState<string | null>(scenarios.find((s) => s.favorite)?.id ?? null);
  const [busy, setBusy] = useState(false);

  const online = connection.state === 'connected';

  const apply = async () => {
    setBusy(true);
    try {
      await setArrive(minutes, scenarioId);
      Alert.alert(t('arrive.title'), t('arrive.done'), [
        { text: t('common.ok'), onPress: () => navigation.goBack() }
      ]);
    } catch (error) {
      Alert.alert(t('common.error'), (error as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Screen>
      <Title>{t('arrive.title')}</Title>
      <Text style={styles.subtitle}>{t('arrive.subtitle')}</Text>

      {arriveAt ? (
        <Card style={styles.activeCard}>
          <Text style={styles.activeText}>{t('arrive.active', { time: formatTime(arriveAt) })}</Text>
          <Button
            title={t('arrive.cancel')}
            variant="secondary"
            onPress={() => void cancelArrive()}
            fullWidth
            style={{ marginTop: spacing.md }}
          />
        </Card>
      ) : null}

      <Card>
        <Text style={styles.big}>
          {t('arrive.minutes', { count: minutes, unit: plural(minutes, 'минуту', 'минуты', 'минут') })}
        </Text>

        <View style={styles.presets}>
          {PRESETS.map((value) => (
            <Pressable
              key={value}
              onPress={() => setMinutes(value)}
              style={[styles.preset, minutes === value && styles.presetActive]}
            >
              <Text style={[styles.presetText, minutes === value && styles.presetTextActive]}>{value}</Text>
            </Pressable>
          ))}
        </View>

        <View style={styles.stepper}>
          <Button title="−5" variant="secondary" onPress={() => setMinutes((m) => Math.max(5, m - 5))} />
          <Button title="+5" variant="secondary" onPress={() => setMinutes((m) => Math.min(720, m + 5))} />
        </View>
      </Card>

      <Section title={t('arrive.scenario')}>
        <Card>
          <Pressable
            onPress={() => setScenarioId(null)}
            style={[styles.row, scenarioId === null && styles.rowActive]}
          >
            <Text style={styles.rowText}>{t('schedules.noScenario')}</Text>
          </Pressable>

          {scenarios.map((scenario) => (
            <Pressable
              key={scenario.id}
              onPress={() => setScenarioId(scenario.id)}
              style={[styles.row, scenarioId === scenario.id && styles.rowActive]}
            >
              <Text style={styles.rowText}>
                {scenario.icon ? `${scenario.icon}  ` : ''}
                {scenario.name}
              </Text>
            </Pressable>
          ))}
        </Card>
      </Section>

      <Button
        title={t('arrive.set')}
        onPress={() => void apply()}
        loading={busy}
        disabled={!online}
        fullWidth
      />

      {!online ? <Text style={styles.warn}>{t('errors.noConnection')}</Text> : null}
    </Screen>
  );
}

const styles = StyleSheet.create({
  subtitle: { ...typography.small, color: colors.textMuted, marginBottom: spacing.lg, lineHeight: 20 },
  big: { ...typography.title, color: colors.text, textAlign: 'center', marginBottom: spacing.lg },

  presets: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm, justifyContent: 'center' },
  preset: {
    width: 56,
    height: 44,
    borderRadius: radius.md,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: colors.surfaceAlt,
    borderWidth: 1,
    borderColor: colors.border
  },
  presetActive: { backgroundColor: colors.accent, borderColor: colors.accent },
  presetText: { ...typography.body, color: colors.textMuted },
  presetTextActive: { color: '#FFFFFF', fontWeight: '700' },

  stepper: { flexDirection: 'row', justifyContent: 'center', gap: spacing.md, marginTop: spacing.lg },

  row: { paddingVertical: spacing.md, paddingHorizontal: spacing.md, borderRadius: radius.md, marginBottom: spacing.xs },
  rowActive: { backgroundColor: colors.accentSoft },
  rowText: { ...typography.body, color: colors.text },

  activeCard: { backgroundColor: colors.accentSoft + '55', borderColor: colors.accent + '55' },
  activeText: { ...typography.subheading, color: colors.accent },

  warn: { ...typography.small, color: colors.warn, textAlign: 'center', marginTop: spacing.md }
});
