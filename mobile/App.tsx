import 'react-native-get-random-values';

import React, { useEffect, useState } from 'react';
import { ActivityIndicator, StyleSheet, Text, View } from 'react-native';
import { StatusBar } from 'expo-status-bar';
import { NavigationContainer, DarkTheme } from '@react-navigation/native';
import { SafeAreaProvider } from 'react-native-safe-area-context';
import * as LocalAuthentication from 'expo-local-authentication';
import { RootNavigator } from './src/navigation';
import { Toast } from './src/components/Toast';
import { colors, typography } from './src/theme';
import { useStore } from './src/state/store';

const navigationTheme = {
  ...DarkTheme,
  colors: {
    ...DarkTheme.colors,
    primary: colors.accent,
    background: colors.background,
    card: colors.surface,
    text: colors.text,
    border: colors.border,
    notification: colors.accent
  }
};

export default function App() {
  const ready = useStore((s) => s.ready);
  const settings = useStore((s) => s.settings);
  const init = useStore((s) => s.init);
  const [unlocked, setUnlocked] = useState(true);

  useEffect(() => {
    void init();
  }, [init]);

  // Биометрия — опция: включается в настройках и спрашивается один раз при старте.
  useEffect(() => {
    if (!ready || !settings.biometricLock) return;

    let cancelled = false;
    setUnlocked(false);

    void (async () => {
      const hasHardware = await LocalAuthentication.hasHardwareAsync();
      const enrolled = await LocalAuthentication.isEnrolledAsync();

      if (!hasHardware || !enrolled) {
        if (!cancelled) setUnlocked(true);
        return;
      }

      const result = await LocalAuthentication.authenticateAsync({
        promptMessage: 'PC MATE',
        cancelLabel: 'Отмена',
        disableDeviceFallback: false
      });

      if (!cancelled) setUnlocked(result.success);
    })();

    return () => {
      cancelled = true;
    };
  }, [ready, settings.biometricLock]);

  if (!ready) {
    return (
      <View style={styles.splash}>
        <Text style={styles.logo}>PC MATE</Text>
        <ActivityIndicator color={colors.accent} style={{ marginTop: 16 }} />
      </View>
    );
  }

  if (!unlocked) {
    return (
      <View style={styles.splash}>
        <Text style={styles.logo}>PC MATE</Text>
        <Text style={styles.locked}>Разблокируйте приложение биометрией</Text>
      </View>
    );
  }

  return (
    <SafeAreaProvider>
      <NavigationContainer theme={navigationTheme}>
        <StatusBar style="light" />
        <RootNavigator />
        <Toast />
      </NavigationContainer>
    </SafeAreaProvider>
  );
}

const styles = StyleSheet.create({
  splash: {
    flex: 1,
    backgroundColor: colors.background,
    alignItems: 'center',
    justifyContent: 'center'
  },
  logo: { ...typography.title, color: colors.text, letterSpacing: 2 },
  locked: { ...typography.small, color: colors.textMuted, marginTop: 12 }
});
