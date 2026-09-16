import React from 'react';
import { Alert, Pressable, StyleSheet, Text, View } from 'react-native';
import { useNavigation } from '@react-navigation/native';
import type { NativeStackNavigationProp } from '@react-navigation/native-stack';
import { Button, Card, EmptyState, Pill, Screen, Title } from '../components/ui';
import { colors, spacing, typography } from '../theme';
import { t } from '../i18n';
import { useStore } from '../state/store';
import { describeDays, formatNextOccurrence } from '../lib/format';
import { isScheduleSynced, type Schedule } from '../lib/types';
import type { RootStackParams } from '../navigation';

type Nav = NativeStackNavigationProp<RootStackParams>;

export function SchedulesScreen() {
  const navigation = useNavigation<Nav>();
  const schedules = useStore((s) => s.schedules);
  const connection = useStore((s) => s.connection);
  const saveSchedule = useStore((s) => s.saveSchedule);
  const deleteSchedule = useStore((s) => s.deleteSchedule);

  const online = connection.state === 'connected';

  const toggle = (schedule: Schedule) => {
    void saveSchedule({ ...schedule, enabled: !schedule.enabled, updatedAt: new Date().toISOString() });
  };

  const confirmDelete = (schedule: Schedule) => {
    Alert.alert(t('schedules.title'), t('schedules.deleteConfirm', { name: schedule.name }), [
      { text: t('common.cancel'), style: 'cancel' },
      { text: t('common.delete'), style: 'destructive', onPress: () => void deleteSchedule(schedule.id) }
    ]);
  };

  return (
    <Screen>
      <Title>{t('schedules.title')}</Title>

      <Card style={styles.infoCard}>
        <Text style={styles.infoText}>
          Расписания «включить» работают даже без интернета: компьютер сам заводит таймер пробуждения Windows.
        </Text>
      </Card>

      {schedules.length === 0 ? (
        <EmptyState
          icon="🕒"
          title={t('schedules.empty')}
          hint={t('schedules.emptyHint')}
          action={
            <Button
              title={t('schedules.create')}
              onPress={() => navigation.navigate('ScheduleEditor', {})}
              fullWidth
              disabled={!online}
            />
          }
        />
      ) : (
        <>
          {schedules.map((schedule) => {
            const synced = isScheduleSynced(schedule);
            const next = formatNextOccurrence(schedule);

            return (
              <Card key={schedule.id}>
                <Pressable onPress={() => navigation.navigate('ScheduleEditor', { id: schedule.id })}>
                  <View style={styles.header}>
                    <View style={styles.headerText}>
                      <Text style={[styles.name, !schedule.enabled && styles.disabled]}>
                        {schedule.name || t(`schedules.actions.${schedule.action}`)}
                      </Text>
                      <Text style={styles.meta}>
                        {schedule.time} · {describeDays(schedule)} · {t(`schedules.actions.${schedule.action}`)}
                      </Text>
                    </View>
                    <Pill
                      text={synced ? '✓' : '⏳'}
                      color={synced ? colors.ok : colors.warn}
                    />
                  </View>

                  {next && schedule.enabled ? (
                    <Text style={styles.next}>{t('schedules.nextRun', { time: next })}</Text>
                  ) : null}

                  {!synced ? <Text style={styles.warn}>{t('schedules.notSyncedHint')}</Text> : null}
                </Pressable>

                <View style={styles.actions}>
                  <Pressable onPress={() => toggle(schedule)} hitSlop={8}>
                    <Text style={styles.action}>
                      {schedule.enabled ? t('common.disabled') : t('common.enabled')}
                    </Text>
                  </Pressable>
                  <Pressable onPress={() => confirmDelete(schedule)} hitSlop={8}>
                    <Text style={[styles.action, { color: colors.danger }]}>{t('common.delete')}</Text>
                  </Pressable>
                </View>
              </Card>
            );
          })}

          <Button
            title={t('schedules.create')}
            onPress={() => navigation.navigate('ScheduleEditor', {})}
            fullWidth
            disabled={!online}
            style={{ marginTop: spacing.sm }}
          />
        </>
      )}
    </Screen>
  );
}

const styles = StyleSheet.create({
  infoCard: { backgroundColor: colors.accentSoft + '44', borderColor: colors.accent + '44' },
  infoText: { ...typography.small, color: colors.textMuted, lineHeight: 19 },

  header: { flexDirection: 'row', alignItems: 'flex-start', justifyContent: 'space-between' },
  headerText: { flex: 1, paddingRight: spacing.md },
  name: { ...typography.subheading, color: colors.text },
  disabled: { color: colors.textFaint, textDecorationLine: 'line-through' },
  meta: { ...typography.small, color: colors.textMuted, marginTop: 2 },
  next: { ...typography.small, color: colors.accent, marginTop: spacing.sm },
  warn: { ...typography.small, color: colors.warn, marginTop: spacing.xs },

  actions: { flexDirection: 'row', justifyContent: 'space-between', marginTop: spacing.md },
  action: { ...typography.small, color: colors.accent }
});
