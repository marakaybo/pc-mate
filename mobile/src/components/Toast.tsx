import React, { useEffect, useRef } from 'react';
import { Animated, Pressable, StyleSheet, Text } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { colors, radius, spacing, typography } from '../theme';
import { useStore } from '../state/store';

/** Всплывающее сообщение поверх экрана: результат команды, событие с ПК. */
export function Toast() {
  const toast = useStore((s) => s.toast);
  const setToast = useStore((s) => s.setToast);
  const insets = useSafeAreaInsets();
  const opacity = useRef(new Animated.Value(0)).current;

  useEffect(() => {
    if (!toast) return;

    Animated.timing(opacity, { toValue: 1, duration: 180, useNativeDriver: true }).start();

    const timer = setTimeout(() => {
      Animated.timing(opacity, { toValue: 0, duration: 220, useNativeDriver: true }).start(() => setToast(null));
    }, 4000);

    return () => clearTimeout(timer);
  }, [toast, opacity, setToast]);

  if (!toast) return null;

  const color =
    toast.level === 'error'
      ? colors.danger
      : toast.level === 'warn'
        ? colors.warn
        : toast.level === 'success'
          ? colors.ok
          : colors.accent;

  return (
    <Animated.View style={[styles.container, { opacity, bottom: insets.bottom + 78, borderColor: color + '66' }]}>
      <Pressable onPress={() => setToast(null)} style={styles.press}>
        <Text style={[styles.text, { color }]} numberOfLines={3}>
          {toast.text}
        </Text>
      </Pressable>
    </Animated.View>
  );
}

const styles = StyleSheet.create({
  container: {
    position: 'absolute',
    left: spacing.lg,
    right: spacing.lg,
    backgroundColor: colors.surface,
    borderRadius: radius.md,
    borderWidth: 1,
    shadowColor: '#000',
    shadowOpacity: 0.3,
    shadowRadius: 12,
    shadowOffset: { width: 0, height: 6 },
    elevation: 8
  },
  press: { paddingVertical: spacing.md, paddingHorizontal: spacing.lg },
  text: { ...typography.small, lineHeight: 19 }
});
