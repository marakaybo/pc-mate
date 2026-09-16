import React from 'react';
import { StyleSheet, Text, View } from 'react-native';
import { colors, radius, spacing, stateColor, stateGlyph, typography } from '../theme';
import { t } from '../i18n';
import type { ConnectionInfo } from '../lib/transport';
import type { PairedPc, StatusSnapshot } from '../lib/types';
import { formatRelative, formatUptime } from '../lib/format';
import { Pill } from './ui';

/** Карточка компьютера: состояние, нагрузка, канал связи. */
export function StatusCard({
  pc,
  status,
  connection
}: {
  pc: PairedPc;
  status: StatusSnapshot | null;
  connection: ConnectionInfo;
}) {
  const online = connection.state === 'connected';
  const state = online ? (status?.state ?? 'unknown') : 'unknown';
  const accent = stateColor(state);

  return (
    <View style={[styles.card, { borderColor: accent + '44' }]}>
      <View style={styles.header}>
        <View style={styles.headerText}>
          <Text style={styles.name} numberOfLines={1}>
            {status?.pcName ?? pc.pcName}
          </Text>
          <View style={styles.stateRow}>
            <Text style={styles.glyph}>{stateGlyph(state)}</Text>
            <Text style={[styles.state, { color: accent }]}>{t(`state.${state}`)}</Text>
          </View>
        </View>

        <Pill
          text={
            connection.state === 'connected'
              ? connection.kind === 'lan'
                ? t('connection.viaLan')
                : t('connection.viaRelay')
              : t(`connection.${connection.state}`)
          }
          color={online ? colors.ok : colors.textFaint}
        />
      </View>

      {online && status ? (
        <View style={styles.metrics}>
          <Metric label="ЦП" value={`${Math.round(status.cpuPercent)}%`} />
          <Metric
            label="ОЗУ"
            value={
              status.ramTotalMb > 0
                ? `${Math.round((status.ramUsedMb / status.ramTotalMb) * 100)}%`
                : '—'
            }
          />
          <Metric label="Аптайм" value={formatUptime(status.uptimeSec)} />
          {status.battery ? (
            <Metric
              label="Батарея"
              value={`${status.battery.percent}%${status.battery.charging ? ' ⚡' : ''}`}
            />
          ) : null}
        </View>
      ) : (
        <Text style={styles.offline}>
          {connection.error ??
            (status?.at ? t('connection.lastSeen', { time: formatRelative(status.at) }) : t('connection.idle'))}
        </Text>
      )}

      {online && status?.activeWindow ? (
        <Text style={styles.activeWindow} numberOfLines={1}>
          {status.activeWindow}
        </Text>
      ) : null}

      {status?.readiness && !status.readiness.wakeReady ? (
        <View style={styles.warning}>
          <Text style={styles.warningText}>
            Компьютер пока не готов к удалённому включению — откройте проверку готовности.
          </Text>
        </View>
      ) : null}
    </View>
  );
}

function Metric({ label, value }: { label: string; value: string }) {
  return (
    <View style={styles.metric}>
      <Text style={styles.metricValue}>{value}</Text>
      <Text style={styles.metricLabel}>{label}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  card: {
    backgroundColor: colors.surface,
    borderRadius: radius.xl,
    padding: spacing.lg,
    borderWidth: 1,
    marginBottom: spacing.lg
  },
  header: { flexDirection: 'row', alignItems: 'flex-start', justifyContent: 'space-between' },
  headerText: { flex: 1, paddingRight: spacing.md },
  name: { ...typography.heading, color: colors.text },
  stateRow: { flexDirection: 'row', alignItems: 'center', marginTop: 2 },
  glyph: { fontSize: 13, marginRight: 6 },
  state: { ...typography.small, fontWeight: '600' },

  metrics: { flexDirection: 'row', marginTop: spacing.lg, justifyContent: 'space-between' },
  metric: { flex: 1 },
  metricValue: { ...typography.subheading, color: colors.text },
  metricLabel: { ...typography.tiny, color: colors.textFaint, marginTop: 2, textTransform: 'uppercase' },

  offline: { ...typography.small, color: colors.textMuted, marginTop: spacing.md, lineHeight: 19 },
  activeWindow: { ...typography.small, color: colors.textFaint, marginTop: spacing.md },

  warning: {
    marginTop: spacing.md,
    padding: spacing.md,
    borderRadius: radius.md,
    backgroundColor: colors.warn + '18',
    borderWidth: 1,
    borderColor: colors.warn + '44'
  },
  warningText: { ...typography.small, color: colors.warn, lineHeight: 18 }
});
