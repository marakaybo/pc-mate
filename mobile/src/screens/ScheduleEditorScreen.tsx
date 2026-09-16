import React, { useMemo, useState } from 'react';
import { Alert, Pressable, StyleSheet, Text, View } from 'react-native';
import { useNavigation, useRoute, type RouteProp } from '@react-navigation/native';
import type { NativeStackNavigationProp } from '@react-navigation/native-stack';
import { Button, Card, Field, Screen, Section, SwitchRow, Title } from '../components/ui';
import { colors, radius, spacing, typography } from '../theme';
import { t } from '../i18n';
import { useStore } from '../state/store';
import { formatNextOccurrence } from '../lib/format';
import type { RootStackParams } from '../navigation';
import type { Schedule, ScheduleAction } from '../lib/types';

type Nav = NativeStackNavigationProp<RootStackParams>;
type Route = RouteProp<RootStackParams, 'ScheduleEditor'>;

const ACTIONS: ScheduleAction[] = ['wake', 'shutdown', 'sleep', 'hibernate', 'reboot', 'scenario'];
const DAY_LABELS = ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'];
const DAY_VALUES = [1, 2, 3, 4, 5, 6, 0];

export function ScheduleEditorScreen() {
  const navigation = useNavigation<Nav>();
  const route = useRoute<Route>();
  const existingId = route.params?.id;

  const schedules = useStore((s) => s.schedules);
  const scenarios = useStore((s) => s.scenarios);
  const saveSchedule = useStore((s) => s.saveSchedule);

  const initial = useMemo<Schedule>(() => {
    const found = existingId ? schedules.find((s) => s.id === existingId) : undefined;
    return (
      found ?? {
        id: '',
        name: '',
        enabled: true,
        action: 'wake',
        time: '23:00',
        days: [1, 2, 3, 4, 5],
        wakeBeforeSec: 0,
        scenarioId: null
      }
    );
  }, [existingId, schedules]);

  const [schedule, setSchedule] = useState<Schedule>(initial);
  const [saving, setSaving] = useState(false);

  const update = (patch: Partial<Schedule>) => setSchedule((prev) => ({ ...prev, ...patch }));

  const toggleDay = (day: number) =>
    setSchedule((prev) => ({
      ...prev,
      days: prev.days.includes(day) ? prev.days.filter((d) => d !== day) : [...prev.days, day].sort()
    }));

  const save = async () => {
    if (!/^\d{1,2}:\d{2}$/.test(schedule.time)) {
      Alert.alert(t('common.error'), 'Время указывается в формате ЧЧ:ММ, например 23:00.');
      return;
    }

    const [hours, minutes] = schedule.time.split(':').map((v) => Number.parseInt(v, 10));
    if (!Number.isFinite(hours) || !Number.isFinite(minutes) || hours! > 23 || minutes! > 59) {
      Alert.alert(t('common.error'), 'Такого времени не бывает.');
      return;
    }

    if (schedule.action === 'scenario' && !schedule.scenarioId) {
      Alert.alert(t('common.error'), 'Выберите сценарий.');
      return;
    }

    setSaving(true);
    try {
      await saveSchedule({
        ...schedule,
        id: schedule.id || Math.random().toString(36).slice(2, 10),
        time: `${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}`,
        name: schedule.name.trim() || `${t(`schedules.actions.${schedule.action}`)} в ${schedule.time}`,
        updatedAt: new Date().toISOString()
      });
      navigation.goBack();
    } catch (error) {
      Alert.alert(t('common.error'), (error as Error).message);
    } finally {
      setSaving(false);
    }
  };

  const preview = formatNextOccurrence(schedule);

  return (
    <Screen>
      <Title>{existingId ? t('common.edit') : t('schedules.create')}</Title>

      <Card>
        <Field
          label={t('scenarios.nameLabel')}
          value={schedule.name}
          onChangeText={(name) => update({ name })}
          placeholder="Будни, 23:00 — включить"
        />

        <Text style={styles.label}>{t('schedules.action')}</Text>
        <View style={styles.chips}>
          {ACTIONS.map((action) => (
            <Pressable
              key={action}
              onPress={() => update({ action })}
              style={[styles.chip, schedule.action === action && styles.chipActive]}
            >
              <Text style={[styles.chipText, schedule.action === action && styles.chipTextActive]}>
                {t(`schedules.actions.${action}`)}
              </Text>
            </Pressable>
          ))}
        </View>

        <Field
          label={t('schedules.time')}
          value={schedule.time}
          onChangeText={(time) => update({ time })}
          keyboardType="numeric"
          placeholder="23:00"
        />

        <Text style={styles.label}>{t('schedules.days')}</Text>
        <View style={styles.days}>
          {DAY_VALUES.map((day, index) => (
            <Pressable
              key={day}
              onPress={() => toggleDay(day)}
              style={[styles.day, schedule.days.includes(day) && styles.dayActive]}
            >
              <Text style={[styles.dayText, schedule.days.includes(day) && styles.dayTextActive]}>
                {DAY_LABELS[index]}
              </Text>
            </Pressable>
          ))}
        </View>

        <SwitchRow
          label={t('common.enabled')}
          value={schedule.enabled}
          onValueChange={(enabled) => update({ enabled })}
        />
      </Card>

      {schedule.action === 'wake' ? (
        <Card>
          <Field
            label={t('schedules.wakeBefore')}
            value={String(Math.round((schedule.wakeBeforeSec ?? 0) / 60))}
            onChangeText={(value) =>
              update({ wakeBeforeSec: (Number.parseInt(value, 10) || 0) * 60 })
            }
            keyboardType="numeric"
            hint="Компьютер проснётся заранее, чтобы к указанному времени всё было готово."
          />
        </Card>
      ) : null}

      {schedule.action === 'wake' || schedule.action === 'scenario' ? (
        <Section title={t('schedules.scenario')}>
          <Card>
            <Pressable
              onPress={() => update({ scenarioId: null })}
              style={[styles.scenarioRow, !schedule.scenarioId && styles.scenarioRowActive]}
            >
              <Text style={styles.scenarioText}>{t('schedules.noScenario')}</Text>
            </Pressable>

            {scenarios.map((scenario) => (
              <Pressable
                key={scenario.id}
                onPress={() => update({ scenarioId: scenario.id })}
                style={[styles.scenarioRow, schedule.scenarioId === scenario.id && styles.scenarioRowActive]}
              >
                <Text style={styles.scenarioText}>
                  {scenario.icon ? `${scenario.icon}  ` : ''}
                  {scenario.name}
                </Text>
              </Pressable>
            ))}
          </Card>
        </Section>
      ) : null}

      {preview ? (
        <Card style={styles.preview}>
          <Text style={styles.previewText}>{t('schedules.nextRun', { time: preview })}</Text>
        </Card>
      ) : null}

      <Button title={t('common.save')} onPress={() => void save()} loading={saving} fullWidth />
    </Screen>
  );
}

const styles = StyleSheet.create({
  label: { ...typography.small, color: colors.textMuted, marginBottom: spacing.sm },

  chips: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm, marginBottom: spacing.md },
  chip: {
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.sm,
    borderRadius: radius.pill,
    backgroundColor: colors.surfaceAlt,
    borderWidth: 1,
    borderColor: colors.border
  },
  chipActive: { backgroundColor: colors.accent, borderColor: colors.accent },
  chipText: { ...typography.small, color: colors.textMuted },
  chipTextActive: { color: '#FFFFFF', fontWeight: '600' },

  days: { flexDirection: 'row', justifyContent: 'space-between', marginBottom: spacing.md },
  day: {
    width: 42,
    height: 42,
    borderRadius: 21,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: colors.surfaceAlt,
    borderWidth: 1,
    borderColor: colors.border
  },
  dayActive: { backgroundColor: colors.accent, borderColor: colors.accent },
  dayText: { ...typography.small, color: colors.textMuted },
  dayTextActive: { color: '#FFFFFF', fontWeight: '700' },

  scenarioRow: {
    paddingVertical: spacing.md,
    paddingHorizontal: spacing.md,
    borderRadius: radius.md,
    marginBottom: spacing.xs
  },
  scenarioRowActive: { backgroundColor: colors.accentSoft },
  scenarioText: { ...typography.body, color: colors.text },

  preview: { backgroundColor: colors.accentSoft + '55', borderColor: colors.accent + '44' },
  previewText: { ...typography.small, color: colors.accent }
});
