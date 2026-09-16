import React, { useMemo, useState } from 'react';
import { Alert, Modal, Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';
import { useNavigation, useRoute, type RouteProp } from '@react-navigation/native';
import type { NativeStackNavigationProp } from '@react-navigation/native-stack';
import { Button, Card, Field, Loading, Screen, Section, Segmented, SwitchRow, Title } from '../components/ui';
import { colors, radius, spacing, typography } from '../theme';
import { t } from '../i18n';
import { useStore } from '../state/store';
import type { RootStackParams } from '../navigation';
import type { AppEntry, Scenario, ScenarioStep, StepType } from '../lib/types';

type Nav = NativeStackNavigationProp<RootStackParams>;
type Route = RouteProp<RootStackParams, 'ScenarioEditor'>;

const STEP_TYPES: StepType[] = ['launch', 'open', 'url', 'close', 'wait', 'volume', 'power', 'notify', 'command'];

const STEP_ICONS: Record<string, string> = {
  launch: '🚀',
  open: '📂',
  url: '🌐',
  close: '✖️',
  wait: '⏳',
  volume: '🔊',
  power: '⏻',
  notify: '📱',
  command: '⌨️',
  uwp: '🧩'
};

export function ScenarioEditorScreen() {
  const navigation = useNavigation<Nav>();
  const route = useRoute<Route>();
  const existingId = route.params?.id;

  const scenarios = useStore((s) => s.scenarios);
  const saveScenario = useStore((s) => s.saveScenario);
  const loadApps = useStore((s) => s.loadApps);
  const apps = useStore((s) => s.apps);
  const busy = useStore((s) => s.busy);

  const initial = useMemo<Scenario>(() => {
    const found = existingId ? scenarios.find((s) => s.id === existingId) : undefined;
    return (
      found ?? {
        id: '',
        name: '',
        icon: '🎬',
        favorite: false,
        onError: 'continue',
        steps: []
      }
    );
  }, [existingId, scenarios]);

  const [scenario, setScenario] = useState<Scenario>(initial);
  const [pickerVisible, setPickerVisible] = useState(false);
  const [saving, setSaving] = useState(false);

  const update = (patch: Partial<Scenario>) => setScenario((prev) => ({ ...prev, ...patch }));

  const updateStep = (index: number, patch: Partial<ScenarioStep>) =>
    setScenario((prev) => ({
      ...prev,
      steps: prev.steps.map((step, i) => (i === index ? { ...step, ...patch } : step))
    }));

  const removeStep = (index: number) =>
    setScenario((prev) => ({ ...prev, steps: prev.steps.filter((_, i) => i !== index) }));

  const moveStep = (index: number, delta: number) =>
    setScenario((prev) => {
      const target = index + delta;
      if (target < 0 || target >= prev.steps.length) return prev;

      const steps = [...prev.steps];
      const [moved] = steps.splice(index, 1);
      steps.splice(target, 0, moved!);
      return { ...prev, steps };
    });

  const addStep = (type: StepType) => {
    const defaults: Record<string, Partial<ScenarioStep>> = {
      wait: { seconds: 10 },
      volume: { level: 40 },
      power: { action: 'sleep' },
      notify: { text: 'ПК готов 👍', notifyLevel: 'info' },
      command: { shell: 'powershell', hidden: true }
    };

    setScenario((prev) => ({
      ...prev,
      steps: [...prev.steps, { type, enabled: true, timeoutSec: 60, ...(defaults[type] ?? {}) }]
    }));
  };

  const pickApp = async () => {
    setPickerVisible(true);
    if (apps.length === 0) {
      try {
        await loadApps(false);
      } catch (error) {
        Alert.alert(t('common.error'), (error as Error).message);
        setPickerVisible(false);
      }
    }
  };

  const applyApp = (app: AppEntry) => {
    setScenario((prev) => ({
      ...prev,
      steps: [
        ...prev.steps,
        app.uwpAppId
          ? { type: 'uwp', appId: app.uwpAppId, title: app.name, enabled: true, timeoutSec: 60 }
          : {
              type: 'launch',
              path: app.path ?? '',
              args: app.args ?? undefined,
              cwd: app.cwd ?? undefined,
              title: app.name,
              enabled: true,
              timeoutSec: 60
            }
      ]
    }));
    setPickerVisible(false);
  };

  const save = async () => {
    if (!scenario.name.trim()) {
      Alert.alert(t('common.error'), t('scenarios.nameLabel'));
      return;
    }

    setSaving(true);
    try {
      await saveScenario({
        ...scenario,
        id: scenario.id || Math.random().toString(36).slice(2, 10),
        name: scenario.name.trim()
      });
      navigation.goBack();
    } catch (error) {
      Alert.alert(t('common.error'), (error as Error).message);
    } finally {
      setSaving(false);
    }
  };

  return (
    <Screen>
      <Title>{existingId ? t('common.edit') : t('scenarios.create')}</Title>

      <Card>
        <Field
          label={t('scenarios.nameLabel')}
          value={scenario.name}
          onChangeText={(name) => update({ name })}
          placeholder={t('scenarios.namePlaceholder')}
        />
        <Field
          label="Значок"
          value={scenario.icon ?? ''}
          onChangeText={(icon) => update({ icon })}
          placeholder="🎮"
        />
        <SwitchRow
          label={t('scenarios.favorite')}
          value={!!scenario.favorite}
          onValueChange={(favorite) => update({ favorite })}
        />
        <Text style={styles.label}>{t('scenarios.onError')}</Text>
        <Segmented
          value={scenario.onError ?? 'continue'}
          onChange={(onError) => update({ onError })}
          options={[
            { value: 'continue', label: t('scenarios.onErrorContinue') },
            { value: 'stop', label: t('scenarios.onErrorStop') }
          ]}
        />
      </Card>

      <Section title={`Шаги · ${scenario.steps.length}`}>
        {scenario.steps.map((step, index) => (
          <Card key={index}>
            <View style={styles.stepHeader}>
              <Text style={styles.stepIcon}>{STEP_ICONS[step.type] ?? '•'}</Text>
              <Text style={styles.stepTitle}>
                {index + 1}. {t(`steps.${step.type}`)}
              </Text>
              <View style={styles.stepControls}>
                <Pressable onPress={() => moveStep(index, -1)} hitSlop={8}>
                  <Text style={styles.control}>↑</Text>
                </Pressable>
                <Pressable onPress={() => moveStep(index, 1)} hitSlop={8}>
                  <Text style={styles.control}>↓</Text>
                </Pressable>
                <Pressable onPress={() => removeStep(index)} hitSlop={8}>
                  <Text style={[styles.control, { color: colors.danger }]}>✕</Text>
                </Pressable>
              </View>
            </View>

            <StepFields step={step} onChange={(patch) => updateStep(index, patch)} />
          </Card>
        ))}

        <Card>
          <Text style={styles.label}>{t('scenarios.addStep')}</Text>
          <View style={styles.typeGrid}>
            <Pressable style={styles.typeChip} onPress={() => void pickApp()}>
              <Text style={styles.typeChipText}>📦 {t('scenarios.pickApp')}</Text>
            </Pressable>
            {STEP_TYPES.map((type) => (
              <Pressable key={type} style={styles.typeChip} onPress={() => addStep(type)}>
                <Text style={styles.typeChipText}>
                  {STEP_ICONS[type]} {t(`steps.${type}`)}
                </Text>
              </Pressable>
            ))}
          </View>
        </Card>
      </Section>

      <Button title={t('common.save')} onPress={() => void save()} loading={saving} fullWidth />

      <Modal visible={pickerVisible} animationType="slide" onRequestClose={() => setPickerVisible(false)}>
        <View style={styles.modal}>
          <Text style={styles.modalTitle}>{t('scenarios.pickApp')}</Text>

          {busy === 'apps' ? (
            <Loading text={t('scenarios.appsLoading')} />
          ) : apps.length === 0 ? (
            <Text style={styles.hint}>{t('scenarios.appsEmpty')}</Text>
          ) : (
            <ScrollView>
              {apps.map((app) => (
                <Pressable key={`${app.source}:${app.path ?? app.uwpAppId}`} onPress={() => applyApp(app)}>
                  <View style={styles.appRow}>
                    <Text style={styles.appName} numberOfLines={1}>
                      {app.name}
                    </Text>
                    <Text style={styles.appPath} numberOfLines={1}>
                      {app.path ?? app.uwpAppId}
                    </Text>
                  </View>
                </Pressable>
              ))}
            </ScrollView>
          )}

          <Button
            title={t('common.close')}
            variant="secondary"
            onPress={() => setPickerVisible(false)}
            fullWidth
            style={{ marginTop: spacing.md }}
          />
        </View>
      </Modal>
    </Screen>
  );
}

function StepFields({ step, onChange }: { step: ScenarioStep; onChange: (patch: Partial<ScenarioStep>) => void }) {
  switch (step.type) {
    case 'launch':
      return (
        <>
          <Field
            label={t('steps.pathLabel')}
            value={step.path ?? ''}
            onChangeText={(path) => onChange({ path })}
            autoCapitalize="none"
            placeholder="C:\\Program Files\\App\\app.exe"
          />
          <Field
            label={t('steps.argsLabel')}
            value={step.args ?? ''}
            onChangeText={(args) => onChange({ args })}
            autoCapitalize="none"
          />
        </>
      );

    case 'open':
      return (
        <Field
          label={t('steps.pathLabel')}
          value={step.path ?? ''}
          onChangeText={(path) => onChange({ path })}
          autoCapitalize="none"
        />
      );

    case 'url':
      return (
        <Field
          label={t('steps.urlLabel')}
          value={step.url ?? ''}
          onChangeText={(url) => onChange({ url })}
          autoCapitalize="none"
          keyboardType="url"
          placeholder="https://youtube.com"
        />
      );

    case 'close':
      return (
        <>
          <Field
            label={t('steps.processLabel')}
            value={step.process ?? ''}
            onChangeText={(process) => onChange({ process })}
            autoCapitalize="none"
            placeholder="chrome"
          />
          <SwitchRow
            label={t('power.forceLabel')}
            value={!!step.force}
            onValueChange={(force) => onChange({ force })}
          />
        </>
      );

    case 'wait':
      return (
        <Field
          label={t('steps.secondsLabel')}
          value={String(step.seconds ?? 0)}
          onChangeText={(value) => onChange({ seconds: Number.parseFloat(value.replace(',', '.')) || 0 })}
          keyboardType="numeric"
        />
      );

    case 'volume':
      return (
        <>
          <Field
            label={t('steps.volumeLabel')}
            value={String(step.level ?? 0)}
            onChangeText={(value) => onChange({ level: Math.min(100, Math.max(0, Number.parseInt(value, 10) || 0)) })}
            keyboardType="numeric"
          />
          <SwitchRow
            label={t('steps.muteLabel')}
            value={!!step.mute}
            onValueChange={(mute) => onChange({ mute })}
          />
        </>
      );

    case 'power':
      return (
        <Segmented
          value={step.action ?? 'sleep'}
          onChange={(action) => onChange({ action })}
          options={[
            { value: 'sleep', label: t('power.sleep') },
            { value: 'hibernate', label: t('power.hibernate') },
            { value: 'lock', label: t('power.lock') },
            { value: 'shutdown', label: t('power.shutdown') }
          ]}
        />
      );

    case 'notify':
      return (
        <Field label={t('steps.textLabel')} value={step.text ?? ''} onChangeText={(text) => onChange({ text })} />
      );

    case 'command':
      return (
        <>
          <Segmented
            value={step.shell ?? 'powershell'}
            onChange={(shell) => onChange({ shell })}
            options={[
              { value: 'powershell', label: 'PowerShell' },
              { value: 'cmd', label: 'cmd' }
            ]}
          />
          <Field
            label={t('steps.command')}
            value={step.command ?? ''}
            onChangeText={(command) => onChange({ command })}
            autoCapitalize="none"
            multiline
            hint={t('steps.commandDisabled')}
          />
        </>
      );

    case 'uwp':
      return (
        <Field
          label="AppID"
          value={step.appId ?? ''}
          onChangeText={(appId) => onChange({ appId })}
          autoCapitalize="none"
        />
      );

    default:
      return null;
  }
}

const styles = StyleSheet.create({
  label: { ...typography.small, color: colors.textMuted, marginBottom: spacing.sm },

  stepHeader: { flexDirection: 'row', alignItems: 'center', marginBottom: spacing.md },
  stepIcon: { fontSize: 18, marginRight: spacing.sm },
  stepTitle: { ...typography.subheading, color: colors.text, flex: 1 },
  stepControls: { flexDirection: 'row', gap: spacing.md },
  control: { ...typography.heading, color: colors.textMuted, paddingHorizontal: 4 },

  typeGrid: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm },
  typeChip: {
    backgroundColor: colors.surfaceAlt,
    borderRadius: radius.pill,
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.sm,
    borderWidth: 1,
    borderColor: colors.border
  },
  typeChipText: { ...typography.small, color: colors.text },

  modal: { flex: 1, backgroundColor: colors.background, padding: spacing.lg, paddingTop: spacing.xxl },
  modalTitle: { ...typography.heading, color: colors.text, marginBottom: spacing.lg },
  appRow: { paddingVertical: spacing.md, borderBottomWidth: 1, borderBottomColor: colors.border },
  appName: { ...typography.body, color: colors.text },
  appPath: { ...typography.small, color: colors.textFaint, marginTop: 2 },
  hint: { ...typography.small, color: colors.textMuted }
});
