import React, { useState } from 'react';
import { RefreshControl, StyleSheet, Text, View } from 'react-native';
import { Card, EmptyState, Screen, Title } from '../components/ui';
import { colors, spacing, typography } from '../theme';
import { t } from '../i18n';
import { useStore } from '../state/store';
import { formatDateTime } from '../lib/format';

const KIND_ICONS: Record<string, string> = {
  'power.wake': '⚡',
  'power.sleep': '🌙',
  'power.hibernate': '💤',
  'power.shutdown': '⏻',
  'power.reboot': '🔄',
  'power.lock': '🔒',
  'power.cancel': '↩️',
  'scenario.done': '🎬',
  'scenario.save': '✏️',
  'schedule.fired': '⏰',
  'schedule.save': '🗓',
  'wake.test': '🧪',
  'wake.test.result': '🧪',
  'pair.ok': '🔗',
  'pair.revoke': '🚫',
  'relay.connect': '🌐',
  'relay.disconnect': '📴',
  'agent.start': '▶️',
  cmd: '📲'
};

export function HistoryScreen() {
  const events = useStore((s) => s.events);
  const refreshEvents = useStore((s) => s.refreshEvents);
  const [refreshing, setRefreshing] = useState(false);

  const onRefresh = async () => {
    setRefreshing(true);
    try {
      await refreshEvents();
    } catch {
      // Оффлайн — показываем то, что уже загружено.
    } finally {
      setRefreshing(false);
    }
  };

  return (
    <Screen
      refreshControl={
        <RefreshControl refreshing={refreshing} onRefresh={() => void onRefresh()} tintColor={colors.accent} />
      }
    >
      <Title>{t('history.title')}</Title>

      {events.length === 0 ? (
        <EmptyState icon="📜" title={t('history.empty')} />
      ) : (
        events.map((event) => (
          <Card key={event.id} style={styles.card}>
            <View style={styles.row}>
              <Text style={styles.icon}>{KIND_ICONS[event.kind] ?? '•'}</Text>
              <View style={styles.text}>
                <Text style={styles.summary}>{event.summary}</Text>
                <Text style={styles.meta}>
                  {formatDateTime(event.at)} · {event.source}
                </Text>
              </View>
            </View>
          </Card>
        ))
      )}
    </Screen>
  );
}

const styles = StyleSheet.create({
  card: { paddingVertical: spacing.md },
  row: { flexDirection: 'row', alignItems: 'flex-start' },
  icon: { fontSize: 18, marginRight: spacing.md, marginTop: 1 },
  text: { flex: 1 },
  summary: { ...typography.body, color: colors.text, lineHeight: 20 },
  meta: { ...typography.tiny, color: colors.textFaint, marginTop: 4 }
});
