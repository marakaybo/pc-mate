import React from 'react';
import { StyleSheet, Text, View } from 'react-native';
import { Card, EmptyState, Screen, Title } from '../components/ui';
import { colors, spacing, typography } from '../theme';
import { t } from '../i18n';
import { useStore } from '../state/store';
import { formatDateTime } from '../lib/format';

/** Отчёт о последнем запуске сценария: что выполнилось, что нет и почему. */
export function ScenarioReportScreen() {
  const report = useStore((s) => s.lastReport);

  if (!report) {
    return (
      <Screen>
        <Title>{t('scenarios.report')}</Title>
        <EmptyState icon="📋" title={t('history.empty')} />
      </Screen>
    );
  }

  const okCount = report.steps.filter((s) => s.status === 'ok').length;

  return (
    <Screen>
      <Title>{report.name}</Title>

      <Card style={{ borderColor: (report.ok ? colors.ok : colors.warn) + '66' }}>
        <Text style={[styles.status, { color: report.ok ? colors.ok : colors.warn }]}>
          {report.ok ? t('scenarios.done') : t('scenarios.failed')}
        </Text>
        <Text style={styles.meta}>
          {okCount} / {report.steps.length} шагов · {formatDateTime(report.startedAt)}
        </Text>
      </Card>

      {report.steps.map((step) => {
        const color =
          step.status === 'ok' ? colors.ok : step.status === 'error' ? colors.danger : colors.textFaint;
        const glyph = step.status === 'ok' ? '✓' : step.status === 'error' ? '✕' : '–';

        return (
          <Card key={step.index} style={styles.step}>
            <View style={styles.row}>
              <Text style={[styles.glyph, { color }]}>{glyph}</Text>
              <View style={styles.text}>
                <Text style={styles.title}>{step.title}</Text>
                {step.message ? <Text style={[styles.message, { color }]}>{step.message}</Text> : null}
                <Text style={styles.duration}>{step.durationMs} мс</Text>
              </View>
            </View>
          </Card>
        );
      })}
    </Screen>
  );
}

const styles = StyleSheet.create({
  status: { ...typography.subheading },
  meta: { ...typography.small, color: colors.textMuted, marginTop: 4 },

  step: { paddingVertical: spacing.md },
  row: { flexDirection: 'row', alignItems: 'flex-start' },
  glyph: { ...typography.subheading, width: 24 },
  text: { flex: 1 },
  title: { ...typography.body, color: colors.text },
  message: { ...typography.small, marginTop: 2, lineHeight: 18 },
  duration: { ...typography.tiny, color: colors.textFaint, marginTop: 4 }
});
