import React from 'react';
import { Alert, Linking, Pressable, StyleSheet, Text, View } from 'react-native';
import Constants from 'expo-constants';
import { useNavigation } from '@react-navigation/native';
import type { NativeStackNavigationProp } from '@react-navigation/native-stack';
import { Button, Card, Screen, Section, Segmented, SwitchRow, Title } from '../components/ui';
import { colors, spacing, typography } from '../theme';
import { t } from '../i18n';
import { activePc, useStore } from '../state/store';
import { formatRelative } from '../lib/format';
import { UpdateBanner } from '../components/UpdateBanner';
import { releasesUrl } from '../lib/updates';
import type { RootStackParams } from '../navigation';

type Nav = NativeStackNavigationProp<RootStackParams>;

export function SettingsScreen() {
  const navigation = useNavigation<Nav>();

  const pcs = useStore((s) => s.pcs);
  const settings = useStore((s) => s.settings);
  const connection = useStore((s) => s.connection);
  const status = useStore((s) => s.status);
  const pc = useStore(activePc);

  const updateSettings = useStore((s) => s.updateSettings);
  const forgetPc = useStore((s) => s.forgetPc);
  const selectPc = useStore((s) => s.selectPc);
  const connect = useStore((s) => s.connect);

  const version = Constants.expoConfig?.version ?? '1.0.0';

  const confirmForget = (pcId: string, name: string) => {
    Alert.alert(t('settings.forget'), t('settings.forgetConfirm', { name }), [
      { text: t('common.cancel'), style: 'cancel' },
      { text: t('common.delete'), style: 'destructive', onPress: () => void forgetPc(pcId) }
    ]);
  };

  return (
    <Screen>
      <Title>{t('settings.title')}</Title>

      <Section title={t('settings.computers')}>
        {pcs.map((item) => (
          <Card key={item.pcId}>
            <Pressable onPress={() => void selectPc(item.pcId)}>
              <View style={styles.row}>
                <View style={styles.rowText}>
                  <Text style={styles.name}>{item.pcName}</Text>
                  <Text style={styles.meta}>
                    {item.mac ?? '—'} · {item.lanIps[0] ?? 'нет адреса'}
                  </Text>
                  <Text style={styles.fingerprint}>{item.fingerprint}</Text>
                </View>
                {item.pcId === pc?.pcId ? <Text style={styles.active}>●</Text> : null}
              </View>
            </Pressable>

            <Pressable onPress={() => confirmForget(item.pcId, item.pcName)} hitSlop={8}>
              <Text style={styles.forget}>{t('settings.forget')}</Text>
            </Pressable>
          </Card>
        ))}

        <Button title={t('home.addPc')} onPress={() => navigation.navigate('Pair')} fullWidth />
      </Section>

      <Section title={t('settings.title')}>
        <Card>
          <Text style={styles.label}>{t('settings.language')}</Text>
          <Segmented
            value={settings.language}
            onChange={(language) => void updateSettings({ language })}
            options={[
              { value: 'ru', label: 'Русский' },
              { value: 'en', label: 'English' }
            ]}
          />

          <SwitchRow
            label={t('settings.preferLan')}
            hint={t('settings.preferLanHint')}
            value={settings.preferLan}
            onValueChange={(preferLan) => void updateSettings({ preferLan })}
          />

          <SwitchRow
            label={t('settings.confirmShutdown')}
            value={settings.confirmShutdown}
            onValueChange={(confirmShutdown) => void updateSettings({ confirmShutdown })}
          />

          <SwitchRow
            label={t('settings.biometric')}
            hint={t('settings.biometricHint')}
            value={settings.biometricLock}
            onValueChange={(biometricLock) => void updateSettings({ biometricLock })}
          />

          <SwitchRow
            label={t('settings.notifications')}
            value={settings.notifications}
            onValueChange={(notifications) => void updateSettings({ notifications })}
          />
        </Card>
      </Section>

      <Section title={t('settings.diagnostics')}>
        <Card>
          <Row label="Связь" value={t(`connection.${connection.state}`)} />
          <Row
            label="Канал"
            value={
              connection.kind === 'lan'
                ? t('connection.viaLan')
                : connection.kind === 'relay'
                  ? t('connection.viaRelay')
                  : '—'
            }
          />
          <Row label="Агент" value={status?.agentVersion ?? '—'} />
          <Row label="Сервер" value={pc?.relayUrl ?? 'не настроен'} />
          <Row label="Обновлено" value={formatRelative(status?.at)} />

          {pc ? (
            <>
              <Text style={[styles.meta, { marginTop: spacing.md }]}>
                Идентификатор компьютера — понадобится при настройке моста пробуждения:
              </Text>
              <Text style={styles.mono} selectable>
                {pc.pcId}
              </Text>
            </>
          ) : null}

          {connection.error ? <Text style={styles.error}>{connection.error}</Text> : null}

          <Button
            title={t('common.retry')}
            variant="secondary"
            onPress={() => void connect()}
            fullWidth
            style={{ marginTop: spacing.md }}
          />
        </Card>
      </Section>

      <Section title={t('settings.about')}>
        <UpdateBanner />
        <Card>
          <Text style={styles.body}>PC MATE — {t('app.tagline')}</Text>
          <Text style={styles.meta}>{t('settings.version', { version })}</Text>
          <Pressable onPress={() => void Linking.openURL(releasesUrl() ?? 'https://github.com')}>
            <Text style={styles.link}>Все версии и документация на GitHub</Text>
          </Pressable>
        </Card>
      </Section>
    </Screen>
  );
}

function Row({ label, value }: { label: string; value: string }) {
  return (
    <View style={styles.diagRow}>
      <Text style={styles.meta}>{label}</Text>
      <Text style={styles.diagValue} numberOfLines={1}>
        {value}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  row: { flexDirection: 'row', alignItems: 'center' },
  rowText: { flex: 1 },
  name: { ...typography.subheading, color: colors.text },
  meta: { ...typography.small, color: colors.textMuted, marginTop: 2 },
  fingerprint: { ...typography.tiny, color: colors.textFaint, marginTop: 4, letterSpacing: 1 },
  active: { color: colors.ok, fontSize: 16 },
  forget: { ...typography.small, color: colors.danger, marginTop: spacing.md },

  label: { ...typography.small, color: colors.textMuted, marginBottom: spacing.sm },
  body: { ...typography.body, color: colors.text },
  link: { ...typography.small, color: colors.accent, marginTop: spacing.sm },
  error: { ...typography.small, color: colors.danger, marginTop: spacing.sm },

  mono: {
    ...typography.small,
    color: colors.text,
    fontFamily: 'monospace',
    marginTop: 4,
    letterSpacing: 0.5
  },
  diagRow: { flexDirection: 'row', justifyContent: 'space-between', paddingVertical: 6 },
  diagValue: { ...typography.small, color: colors.text, maxWidth: '60%', textAlign: 'right' }
});
