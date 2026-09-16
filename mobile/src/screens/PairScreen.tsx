import React, { useCallback, useRef, useState } from 'react';
import { Alert, Platform, StyleSheet, Text, View } from 'react-native';
import { CameraView, useCameraPermissions } from 'expo-camera';
import * as Device from 'expo-device';
import { useNavigation } from '@react-navigation/native';
import type { NativeStackNavigationProp } from '@react-navigation/native-stack';
import { Button, Card, Field, Loading, Screen, Title } from '../components/ui';
import { colors, radius, spacing, typography } from '../theme';
import { t } from '../i18n';
import { parsePairingUri, isOfferExpired, type PairingOffer } from '../lib/protocol';
import { useStore } from '../state/store';
import type { RootStackParams } from '../navigation';

type Nav = NativeStackNavigationProp<RootStackParams>;

/**
 * Сопряжение: телефон читает QR с компьютера и обменивается публичными ключами.
 * Сначала пробуем напрямую по локальной сети, потом — через сервер.
 */
export function PairScreen() {
  const navigation = useNavigation<Nav>();
  const [permission, requestPermission] = useCameraPermissions();
  const [manual, setManual] = useState(false);
  const [manualCode, setManualCode] = useState('');
  const [busy, setBusy] = useState(false);
  const handledRef = useRef(false);

  const pair = useStore((s) => s.pair);

  const handleOffer = useCallback(
    async (uri: string) => {
      if (handledRef.current) return;
      handledRef.current = true;

      const offer: PairingOffer | null = parsePairingUri(uri.trim());
      if (!offer) {
        Alert.alert(t('common.error'), t('pair.invalid'));
        handledRef.current = false;
        return;
      }

      if (isOfferExpired(offer)) {
        Alert.alert(t('common.error'), t('pair.expired'));
        handledRef.current = false;
        return;
      }

      setBusy(true);
      try {
        const phoneName = Device.deviceName || `${Device.brand ?? ''} ${Device.modelName ?? ''}`.trim() || 'Телефон';
        const pc = await pair(offer, phoneName);

        Alert.alert(
          t('pair.success'),
          `${pc.pcName}\n\n${t('pair.fingerprintTitle')}: ${pc.fingerprint}\n${t('pair.fingerprintHint')}`,
          [{ text: t('common.ok'), onPress: () => navigation.goBack() }]
        );
      } catch (error) {
        Alert.alert(t('common.error'), (error as Error).message);
        handledRef.current = false;
      } finally {
        setBusy(false);
      }
    },
    [navigation, pair]
  );

  if (busy) {
    return (
      <Screen>
        <Title>{t('pair.title')}</Title>
        <Loading text={t('pair.connecting')} />
      </Screen>
    );
  }

  if (manual) {
    return (
      <Screen>
        <Title>{t('pair.title')}</Title>
        <Card>
          <Field
            label={t('pair.manualEntry')}
            value={manualCode}
            onChangeText={setManualCode}
            placeholder={t('pair.manualPlaceholder')}
            autoCapitalize="none"
            multiline
          />
          <Button
            title={t('common.done')}
            onPress={() => void handleOffer(manualCode)}
            disabled={manualCode.trim().length === 0}
            fullWidth
          />
          <Button
            title={t('common.back')}
            variant="ghost"
            onPress={() => setManual(false)}
            fullWidth
            style={{ marginTop: spacing.sm }}
          />
        </Card>
      </Screen>
    );
  }

  if (!permission) {
    return (
      <Screen>
        <Loading />
      </Screen>
    );
  }

  if (!permission.granted) {
    return (
      <Screen>
        <Title>{t('pair.title')}</Title>
        <Card>
          <Text style={styles.permissionTitle}>{t('pair.permissionTitle')}</Text>
          <Text style={styles.hint}>{t('pair.permissionBody')}</Text>
          <Button
            title={t('pair.permissionButton')}
            onPress={() => void requestPermission()}
            fullWidth
            style={{ marginTop: spacing.lg }}
          />
          <Button
            title={t('pair.manualEntry')}
            variant="ghost"
            onPress={() => setManual(true)}
            fullWidth
            style={{ marginTop: spacing.sm }}
          />
        </Card>
      </Screen>
    );
  }

  return (
    <Screen scroll={false}>
      <View style={styles.container}>
        <Title>{t('pair.title')}</Title>
        <Text style={styles.hint}>{t('pair.scanHint')}</Text>

        <View style={styles.cameraWrapper}>
          <CameraView
            style={StyleSheet.absoluteFill}
            facing="back"
            barcodeScannerSettings={{ barcodeTypes: ['qr'] }}
            onBarcodeScanned={({ data }) => void handleOffer(data)}
          />
          <View style={styles.frame} pointerEvents="none" />
        </View>

        <Button
          title={t('pair.manualEntry')}
          variant="secondary"
          onPress={() => setManual(true)}
          fullWidth
          style={{ marginTop: spacing.lg }}
        />

        {Platform.OS === 'android' ? (
          <Text style={styles.footnote}>
            Подключение работает и без интернета, если телефон и компьютер в одной сети Wi-Fi.
          </Text>
        ) : null}
      </View>
    </Screen>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, paddingHorizontal: spacing.lg },
  hint: { ...typography.small, color: colors.textMuted, lineHeight: 20, marginBottom: spacing.lg },
  cameraWrapper: {
    flex: 1,
    borderRadius: radius.xl,
    overflow: 'hidden',
    backgroundColor: '#000000',
    maxHeight: 420
  },
  frame: {
    position: 'absolute',
    top: '15%',
    left: '12%',
    right: '12%',
    bottom: '15%',
    borderWidth: 2,
    borderColor: colors.accent,
    borderRadius: radius.lg
  },
  permissionTitle: { ...typography.subheading, color: colors.text, marginBottom: spacing.sm },
  footnote: {
    ...typography.small,
    color: colors.textFaint,
    textAlign: 'center',
    marginTop: spacing.md,
    marginBottom: spacing.lg
  }
});
