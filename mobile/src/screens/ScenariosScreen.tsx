import React from 'react';
import { Alert, Pressable, StyleSheet, Text, View } from 'react-native';
import { useNavigation } from '@react-navigation/native';
import type { NativeStackNavigationProp } from '@react-navigation/native-stack';
import { Button, Card, EmptyState, Screen, Title } from '../components/ui';
import { colors, spacing, typography } from '../theme';
import { t } from '../i18n';
import { useStore } from '../state/store';
import type { RootStackParams } from '../navigation';
import type { Scenario } from '../lib/types';

type Nav = NativeStackNavigationProp<RootStackParams>;

export function ScenariosScreen() {
  const navigation = useNavigation<Nav>();
  const scenarios = useStore((s) => s.scenarios);
  const connection = useStore((s) => s.connection);
  const busy = useStore((s) => s.busy);
  const lastReport = useStore((s) => s.lastReport);
  const runScenario = useStore((s) => s.runScenario);
  const deleteScenario = useStore((s) => s.deleteScenario);
  const saveScenario = useStore((s) => s.saveScenario);

  const online = connection.state === 'connected';

  const toggleFavorite = (scenario: Scenario) => {
    void saveScenario({ ...scenario, favorite: !scenario.favorite });
  };

  const confirmDelete = (scenario: Scenario) => {
    Alert.alert(t('scenarios.title'), t('scenarios.deleteConfirm', { name: scenario.name }), [
      { text: t('common.cancel'), style: 'cancel' },
      { text: t('common.delete'), style: 'destructive', onPress: () => void deleteScenario(scenario.id) }
    ]);
  };

  return (
    <Screen>
      <Title>{t('scenarios.title')}</Title>

      {!online ? (
        <Card style={styles.offlineCard}>
          <Text style={styles.offlineText}>
            {t('errors.noConnection')} Сценарии хранятся на компьютере — список появится, когда он выйдет на связь.
          </Text>
        </Card>
      ) : null}

      {lastReport ? (
        <Card
          style={{ borderColor: (lastReport.ok ? colors.ok : colors.warn) + '66' }}
          onPress={() => navigation.navigate('ScenarioReport')}
        >
          <Text style={[styles.reportTitle, { color: lastReport.ok ? colors.ok : colors.warn }]}>
            {lastReport.ok ? t('scenarios.done') : t('scenarios.failed')}: {lastReport.name}
          </Text>
          <Text style={styles.hint}>
            {lastReport.steps.filter((s) => s.status === 'ok').length} / {lastReport.steps.length} шагов ·{' '}
            {t('scenarios.report')} ›
          </Text>
        </Card>
      ) : null}

      {scenarios.length === 0 ? (
        <EmptyState
          icon="🎬"
          title={t('scenarios.empty')}
          hint={t('scenarios.emptyHint')}
          action={
            <Button
              title={t('scenarios.create')}
              onPress={() => navigation.navigate('ScenarioEditor', {})}
              fullWidth
              disabled={!online}
            />
          }
        />
      ) : (
        <>
          {scenarios.map((scenario) => (
            <Card key={scenario.id}>
              <View style={styles.row}>
                <Pressable onPress={() => toggleFavorite(scenario)} hitSlop={10} style={styles.star}>
                  <Text style={styles.starGlyph}>{scenario.favorite ? '★' : '☆'}</Text>
                </Pressable>

                <Pressable
                  style={styles.info}
                  onPress={() => navigation.navigate('ScenarioEditor', { id: scenario.id })}
                >
                  <Text style={styles.name}>
                    {scenario.icon ? `${scenario.icon}  ` : ''}
                    {scenario.name}
                  </Text>
                  <Text style={styles.hint}>{t('scenarios.stepsCount', { count: scenario.steps.length })}</Text>
                </Pressable>

                <Button
                  title={busy === `scenario:${scenario.id}` ? t('scenarios.running') : t('scenarios.run')}
                  onPress={() => void runScenario(scenario.id)}
                  disabled={!online}
                  loading={busy === `scenario:${scenario.id}`}
                  style={styles.runButton}
                />
              </View>

              <Pressable onPress={() => confirmDelete(scenario)} hitSlop={8}>
                <Text style={styles.delete}>{t('common.delete')}</Text>
              </Pressable>
            </Card>
          ))}

          <Button
            title={t('scenarios.create')}
            onPress={() => navigation.navigate('ScenarioEditor', {})}
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
  row: { flexDirection: 'row', alignItems: 'center' },
  star: { paddingRight: spacing.md },
  starGlyph: { fontSize: 20, color: colors.warn },
  info: { flex: 1, paddingRight: spacing.md },
  name: { ...typography.subheading, color: colors.text },
  hint: { ...typography.small, color: colors.textMuted, marginTop: 2 },
  runButton: { minHeight: 38, paddingHorizontal: spacing.md },
  delete: { ...typography.small, color: colors.danger, marginTop: spacing.md },

  offlineCard: { borderColor: colors.warn + '55', backgroundColor: colors.warn + '12' },
  offlineText: { ...typography.small, color: colors.warn, lineHeight: 19 },

  reportTitle: { ...typography.subheading }
});
