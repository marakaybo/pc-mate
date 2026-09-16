import React, { useEffect } from 'react';
import { Alert, StyleSheet, Text, View } from 'react-native';
import { Button, Card, Loading, Screen, Title } from '../components/ui';
import { colors, radius, spacing, typography } from '../theme';
import { t } from '../i18n';
import { useStore } from '../state/store';
import type { CheckResult } from '../lib/types';

/** Мастер готовности: те же 12 проверок, что и в трее на компьютере. */
export function WizardScreen() {
  const checks = useStore((s) => s.checks);
  const busy = useStore((s) => s.busy);
  const connection = useStore((s) => s.connection);
  const runWizard = useStore((s) => s.runWizard);
  const fixCheck = useStore((s) => s.fixCheck);
  const startWakeTest = useStore((s) => s.startWakeTest);

  const online = connection.state === 'connected';

  useEffect(() => {
    if (online && checks.length === 0) void runWizard().catch(() => undefined);
  }, [online]);

  const ok = checks.filter((c) => c.status === 'ok').length;
  const warn = checks.filter((c) => c.status === 'warn').length;
  const fail = checks.filter((c) => c.status === 'fail').length;

  const summary =
    fail > 0
      ? t('wizard.summaryFail', { fail })
      : warn > 0
        ? t('wizard.summaryWarn', { warn })
        : checks.length > 0
          ? t('wizard.summaryOk')
          : '';

  const confirmTest = (mode: 'wol' | 'timer') => {
    Alert.alert(
      mode === 'wol' ? t('wizard.testWol') : t('wizard.testTimer'),
      mode === 'wol' ? t('wizard.testWolHint') : t('wizard.testTimerHint'),
      [
        { text: t('common.cancel'), style: 'cancel' },
        { text: t('common.ok'), onPress: () => void startWakeTest(mode) }
      ]
    );
  };

  return (
    <Screen>
      <Title>{t('wizard.title')}</Title>
      <Text style={styles.subtitle}>{t('wizard.subtitle')}</Text>

      {!online ? (
        <Card style={styles.offline}>
          <Text style={styles.offlineText}>{t('errors.noConnection')}</Text>
        </Card>
      ) : null}

      {summary ? (
        <Card style={{ borderColor: (fail > 0 ? colors.danger : warn > 0 ? colors.warn : colors.ok) + '66' }}>
          <Text style={[styles.summary, { color: fail > 0 ? colors.danger : warn > 0 ? colors.warn : colors.ok }]}>
            {summary}
          </Text>
          <Text style={styles.summaryMeta}>
            ✓ {ok} · ! {warn} · ✕ {fail}
          </Text>
        </Card>
      ) : null}

      {busy === 'wizard' ? <Loading text={t('common.loading')} /> : null}

      {checks.map((check) => (
        <CheckCard
          key={check.id}
          check={check}
          fixing={busy === `fix:${check.id}`}
          onFix={() => void fixCheck(check.id).catch((e) => Alert.alert(t('common.error'), (e as Error).message))}
        />
      ))}

      <Button
        title={t('wizard.run')}
        onPress={() => void runWizard().catch((e) => Alert.alert(t('common.error'), (e as Error).message))}
        loading={busy === 'wizard'}
        disabled={!online}
        fullWidth
        style={{ marginTop: spacing.md }}
      />

      <Button
        title={t('wizard.testWol')}
        variant="secondary"
        onPress={() => confirmTest('wol')}
        disabled={!online}
        fullWidth
        style={{ marginTop: spacing.sm }}
      />

      <Button
        title={t('wizard.testTimer')}
        variant="secondary"
        onPress={() => confirmTest('timer')}
        disabled={!online}
        fullWidth
        style={{ marginTop: spacing.sm }}
      />
    </Screen>
  );
}

function CheckCard({
  check,
  fixing,
  onFix
}: {
  check: CheckResult;
  fixing: boolean;
  onFix: () => void;
}) {
  const color =
    check.status === 'ok'
      ? colors.ok
      : check.status === 'warn'
        ? colors.warn
        : check.status === 'fail'
          ? colors.danger
          : colors.textFaint;

  const glyph = check.status === 'ok' ? '✓' : check.status === 'warn' ? '!' : check.status === 'fail' ? '✕' : '?';

  return (
    <Card>
      <View style={styles.checkHeader}>
        <View style={[styles.badge, { backgroundColor: color + '22', borderColor: color + '55' }]}>
          <Text style={[styles.badgeText, { color }]}>{glyph}</Text>
        </View>
        <Text style={styles.checkTitle}>{check.title}</Text>
      </View>

      <Text style={styles.checkDetail}>{check.detail}</Text>

      {check.manualSteps && check.manualSteps.length > 0 ? (
        <View style={styles.steps}>
          <Text style={styles.stepsTitle}>{t('wizard.manualSteps')}</Text>
          {check.manualSteps.map((step, index) => (
            <Text key={index} style={styles.step}>
              {index + 1}. {step}
            </Text>
          ))}
        </View>
      ) : null}

      {check.canFix ? (
        <Button
          title={fixing ? t('wizard.fixing') : t('wizard.fix')}
          onPress={onFix}
          loading={fixing}
          style={{ marginTop: spacing.md }}
        />
      ) : null}
    </Card>
  );
}

const styles = StyleSheet.create({
  subtitle: { ...typography.small, color: colors.textMuted, marginBottom: spacing.lg, lineHeight: 20 },

  summary: { ...typography.subheading },
  summaryMeta: { ...typography.small, color: colors.textMuted, marginTop: 4 },

  offline: { borderColor: colors.warn + '55', backgroundColor: colors.warn + '12' },
  offlineText: { ...typography.small, color: colors.warn },

  checkHeader: { flexDirection: 'row', alignItems: 'center', marginBottom: spacing.sm },
  badge: {
    width: 26,
    height: 26,
    borderRadius: 13,
    alignItems: 'center',
    justifyContent: 'center',
    borderWidth: 1,
    marginRight: spacing.md
  },
  badgeText: { ...typography.small, fontWeight: '700' },
  checkTitle: { ...typography.subheading, color: colors.text, flex: 1 },
  checkDetail: { ...typography.small, color: colors.textMuted, lineHeight: 19 },

  steps: {
    marginTop: spacing.md,
    padding: spacing.md,
    backgroundColor: colors.surfaceAlt,
    borderRadius: radius.md
  },
  stepsTitle: { ...typography.tiny, color: colors.textFaint, textTransform: 'uppercase', marginBottom: spacing.sm },
  step: { ...typography.small, color: colors.textMuted, lineHeight: 19, marginBottom: 4 }
});
