import React, { useCallback, useMemo, useState } from 'react';
import { Alert, Pressable, RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import * as Haptics from 'expo-haptics';
import type { NativeStackNavigationProp } from '@react-navigation/native-stack';
import { useNavigation } from '@react-navigation/native';
import { Button, Card, EmptyState, Screen, Section, Title } from '../components/ui';
import { StatusCard } from '../components/StatusCard';
import { UpdateBanner } from '../components/UpdateBanner';
import { colors, radius, spacing, typography } from '../theme';
import { t } from '../i18n';
import { activePc, useStore } from '../state/store';
import type { RootStackParams } from '../navigation';

type Nav = NativeStackNavigationProp<RootStackParams>;

export function HomeScreen() {
  const navigation = useNavigation<Nav>();
  const [refreshing, setRefreshing] = useState(false);

  const pcs = useStore((s) => s.pcs);
  const status = useStore((s) => s.status);
  const connection = useStore((s) => s.connection);
  const countdown = useStore((s) => s.countdown);
  const scenarios = useStore((s) => s.scenarios);
  const busy = useStore((s) => s.busy);
  const settings = useStore((s) => s.settings);
  const pc = useStore(activePc);

  const power = useStore((s) => s.power);
  const cancelPower = useStore((s) => s.cancelPower);
  const wake = useStore((s) => s.wake);
  const runScenario = useStore((s) => s.runScenario);
  const refreshAll = useStore((s) => s.refreshAll);
  const selectPc = useStore((s) => s.selectPc);

  const online = connection.state === 'connected';
  const favorites = useMemo(() => scenarios.filter((s) => s.favorite), [scenarios]);

  const onRefresh = useCallback(async () => {
    setRefreshing(true);
    try {
      await refreshAll();
    } finally {
      setRefreshing(false);
    }
  }, [refreshAll]);

  if (pcs.length === 0) {
    return (
      <Screen>
        <Title>{t('home.title')}</Title>
        <EmptyState
          icon="🖥"
          title={t('home.empty')}
          hint={t('home.emptyHint')}
          action={<Button title={t('home.addPc')} onPress={() => navigation.navigate('Pair')} fullWidth />}
        />
      </Screen>
    );
  }

  const handleMainAction = async () => {
    await Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);

    if (!online) {
      const result = await wake();
      Alert.alert(t('wake.title'), result.message);
      return;
    }

    if (settings.confirmShutdown) {
      Alert.alert(t('power.confirmShutdownTitle'), t('power.confirmShutdownBody'), [
        { text: t('common.cancel'), style: 'cancel' },
        { text: t('power.shutdown'), style: 'destructive', onPress: () => void power('shutdown') }
      ]);
      return;
    }

    await power('shutdown');
  };

  return (
    <Screen refreshControl={<RefreshControl refreshing={refreshing} onRefresh={onRefresh} tintColor={colors.accent} />}>
      <Title>{t('home.title')}</Title>
      <UpdateBanner compact />

      {pcs.length > 1 ? (
        <ScrollView horizontal showsHorizontalScrollIndicator={false} style={styles.pcRow}>
          {pcs.map((item) => (
            <Pressable
              key={item.pcId}
              onPress={() => void selectPc(item.pcId)}
              style={[styles.pcChip, item.pcId === pc?.pcId && styles.pcChipActive]}
            >
              <Text style={[styles.pcChipText, item.pcId === pc?.pcId && styles.pcChipTextActive]}>
                {item.pcName}
              </Text>
            </Pressable>
          ))}
        </ScrollView>
      ) : null}

      {pc ? <StatusCard pc={pc} status={status} connection={connection} /> : null}

      {countdown ? (
        <Card style={styles.countdownCard}>
          <Text style={styles.countdownTitle}>
            {t(`power.${countdown.action === 'reboot' ? 'reboot' : countdown.action}`)} ·{' '}
            {t('power.countdown', { seconds: countdown.secondsLeft })}
          </Text>
          <Button
            title={t('power.cancel')}
            variant="danger"
            onPress={() => void cancelPower()}
            fullWidth
            style={{ marginTop: spacing.md }}
          />
        </Card>
      ) : null}

      <Button
        title={online ? t('home.turnOff') : t('home.turnOn')}
        variant={online ? 'danger' : 'primary'}
        icon={online ? '⏻' : '⚡'}
        onPress={handleMainAction}
        loading={busy === 'wake' || busy === 'shutdown'}
        fullWidth
        style={styles.mainButton}
      />

      <Section title={t('home.quickActions')}>
        <View style={styles.grid}>
          <QuickAction
            icon="🌙"
            label={t('power.sleep')}
            disabled={!online}
            onPress={() => void power('sleep')}
          />
          <QuickAction
            icon="💤"
            label={t('power.hibernate')}
            disabled={!online}
            onPress={() => void power('hibernate')}
          />
          <QuickAction
            icon="🔒"
            label={t('power.lock')}
            disabled={!online}
            onPress={() => void power('lock')}
          />
          <QuickAction
            icon="🔄"
            label={t('power.reboot')}
            disabled={!online}
            onPress={() =>
              Alert.alert(t('power.reboot'), t('power.confirmShutdownBody'), [
                { text: t('common.cancel'), style: 'cancel' },
                { text: t('power.reboot'), onPress: () => void power('reboot') }
              ])
            }
          />
          <QuickAction icon="⏱" label={t('home.arrive')} onPress={() => navigation.navigate('Arrive')} />
          <QuickAction icon="🩺" label={t('home.wizard')} onPress={() => navigation.navigate('Wizard')} />
        </View>
      </Section>

      <Section
        title={t('home.favorites')}
        action={
          <Pressable onPress={() => navigation.navigate('Tabs', { screen: 'Scenarios' })}>
            <Text style={styles.link}>{t('common.edit')}</Text>
          </Pressable>
        }
      >
        {favorites.length === 0 ? (
          <Card>
            <Text style={styles.hint}>{t('home.noFavorites')}</Text>
          </Card>
        ) : (
          favorites.map((scenario) => (
            <Card key={scenario.id} onPress={() => void runScenario(scenario.id)}>
              <View style={styles.scenarioRow}>
                <Text style={styles.scenarioIcon}>{scenario.icon || '▶️'}</Text>
                <View style={styles.scenarioText}>
                  <Text style={styles.scenarioName}>{scenario.name}</Text>
                  <Text style={styles.hint}>{t('scenarios.stepsCount', { count: scenario.steps.length })}</Text>
                </View>
                <Text style={styles.runGlyph}>›</Text>
              </View>
            </Card>
          ))
        )}
      </Section>

      <Section title={t('home.history')}>
        <Button
          title={t('history.title')}
          variant="secondary"
          onPress={() => navigation.navigate('History')}
          fullWidth
        />
      </Section>
    </Screen>
  );
}

function QuickAction({
  icon,
  label,
  onPress,
  disabled
}: {
  icon: string;
  label: string;
  onPress: () => void;
  disabled?: boolean;
}) {
  return (
    <Pressable
      onPress={onPress}
      disabled={disabled}
      style={({ pressed }) => [styles.action, disabled && styles.actionDisabled, pressed && { opacity: 0.7 }]}
      accessibilityRole="button"
      accessibilityLabel={label}
    >
      <View style={styles.actionInner}>
        <Text style={styles.actionIcon}>{icon}</Text>
        <Text style={styles.actionLabel} numberOfLines={1}>
          {label}
        </Text>
      </View>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  pcRow: { marginBottom: spacing.md },
  pcChip: {
    paddingHorizontal: spacing.lg,
    paddingVertical: spacing.sm,
    borderRadius: radius.pill,
    backgroundColor: colors.surfaceAlt,
    marginRight: spacing.sm
  },
  pcChipActive: { backgroundColor: colors.accent },
  pcChipText: { ...typography.small, color: colors.textMuted },
  pcChipTextActive: { color: '#FFFFFF', fontWeight: '600' },

  mainButton: { minHeight: 62, borderRadius: radius.lg },

  countdownCard: { borderColor: colors.warn + '66', backgroundColor: colors.warn + '14' },
  countdownTitle: { ...typography.subheading, color: colors.warn },

  grid: { flexDirection: 'row', flexWrap: 'wrap', marginHorizontal: -spacing.xs },
  action: {
    width: '33.33%',
    paddingHorizontal: spacing.xs,
    marginBottom: spacing.sm
  },
  actionDisabled: { opacity: 0.4 },
  actionInner: {
    backgroundColor: colors.surface,
    borderWidth: 1,
    borderColor: colors.border,
    borderRadius: radius.md,
    paddingVertical: spacing.md,
    paddingHorizontal: spacing.sm,
    alignItems: 'center'
  },
  actionIcon: { fontSize: 22, marginBottom: 6 },
  actionLabel: { ...typography.small, color: colors.text, textAlign: 'center' },

  scenarioRow: { flexDirection: 'row', alignItems: 'center' },
  scenarioIcon: { fontSize: 22, marginRight: spacing.md },
  scenarioText: { flex: 1 },
  scenarioName: { ...typography.subheading, color: colors.text },
  runGlyph: { ...typography.heading, color: colors.textFaint },

  hint: { ...typography.small, color: colors.textMuted, lineHeight: 19 },
  link: { ...typography.small, color: colors.accent }
});
