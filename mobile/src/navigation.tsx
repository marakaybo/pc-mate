import React from 'react';
import { Text } from 'react-native';
import { createNativeStackNavigator } from '@react-navigation/native-stack';
import { createBottomTabNavigator } from '@react-navigation/bottom-tabs';
import type { NavigatorScreenParams } from '@react-navigation/native';
import { colors } from './theme';
import { t } from './i18n';
import { HomeScreen } from './screens/HomeScreen';
import { PairScreen } from './screens/PairScreen';
import { ScenariosScreen } from './screens/ScenariosScreen';
import { ScenarioEditorScreen } from './screens/ScenarioEditorScreen';
import { ScenarioReportScreen } from './screens/ScenarioReportScreen';
import { SchedulesScreen } from './screens/SchedulesScreen';
import { ScheduleEditorScreen } from './screens/ScheduleEditorScreen';
import { ArriveScreen } from './screens/ArriveScreen';
import { WizardScreen } from './screens/WizardScreen';
import { HistoryScreen } from './screens/HistoryScreen';
import { SettingsScreen } from './screens/SettingsScreen';

export type TabParams = {
  Home: undefined;
  Scenarios: undefined;
  Schedules: undefined;
  Settings: undefined;
};

export type RootStackParams = {
  Tabs: NavigatorScreenParams<TabParams> | undefined;
  Pair: undefined;
  ScenarioEditor: { id?: string };
  ScenarioReport: undefined;
  ScheduleEditor: { id?: string };
  Arrive: undefined;
  Wizard: undefined;
  History: undefined;
};

const Tab = createBottomTabNavigator<TabParams>();
const Stack = createNativeStackNavigator<RootStackParams>();

const TAB_ICONS: Record<keyof TabParams, string> = {
  Home: '🏠',
  Scenarios: '🎬',
  Schedules: '🕒',
  Settings: '⚙️'
};

function Tabs() {
  return (
    <Tab.Navigator
      screenOptions={({ route }) => ({
        headerShown: false,
        tabBarActiveTintColor: colors.accent,
        tabBarInactiveTintColor: colors.textFaint,
        tabBarStyle: {
          backgroundColor: colors.surface,
          borderTopColor: colors.border,
          height: 62,
          paddingBottom: 8,
          paddingTop: 6
        },
        tabBarLabelStyle: { fontSize: 11 },
        tabBarIcon: ({ focused }) => (
          <Text style={{ fontSize: 20, opacity: focused ? 1 : 0.55 }}>{TAB_ICONS[route.name]}</Text>
        )
      })}
    >
      <Tab.Screen name="Home" component={HomeScreen} options={{ title: t('tabs.home') }} />
      <Tab.Screen name="Scenarios" component={ScenariosScreen} options={{ title: t('tabs.scenarios') }} />
      <Tab.Screen name="Schedules" component={SchedulesScreen} options={{ title: t('tabs.schedules') }} />
      <Tab.Screen name="Settings" component={SettingsScreen} options={{ title: t('tabs.settings') }} />
    </Tab.Navigator>
  );
}

export function RootNavigator() {
  return (
    <Stack.Navigator
      screenOptions={{
        headerStyle: { backgroundColor: colors.background },
        headerTintColor: colors.text,
        headerTitleStyle: { fontWeight: '600' },
        contentStyle: { backgroundColor: colors.background }
      }}
    >
      <Stack.Screen name="Tabs" component={Tabs} options={{ headerShown: false }} />
      <Stack.Screen name="Pair" component={PairScreen} options={{ title: t('pair.title') }} />
      <Stack.Screen
        name="ScenarioEditor"
        component={ScenarioEditorScreen}
        options={{ title: t('scenarios.title') }}
      />
      <Stack.Screen
        name="ScenarioReport"
        component={ScenarioReportScreen}
        options={{ title: t('scenarios.report') }}
      />
      <Stack.Screen
        name="ScheduleEditor"
        component={ScheduleEditorScreen}
        options={{ title: t('schedules.title') }}
      />
      <Stack.Screen name="Arrive" component={ArriveScreen} options={{ title: t('arrive.title') }} />
      <Stack.Screen name="Wizard" component={WizardScreen} options={{ title: t('wizard.title') }} />
      <Stack.Screen name="History" component={HistoryScreen} options={{ title: t('history.title') }} />
    </Stack.Navigator>
  );
}
