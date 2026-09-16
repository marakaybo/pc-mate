/** Модели предметной области — зеркало PcMate.Core.Models. */

export type PowerState =
  | 'running'
  | 'sleeping'
  | 'hibernating'
  | 'shutting-down'
  | 'locked'
  | 'off'
  | 'unknown';

export interface BatteryInfo {
  percent: number;
  charging: boolean;
}

export interface UserSessionInfo {
  active: boolean;
  userName?: string | null;
  locked: boolean;
}

export interface NetworkInfo {
  mac?: string | null;
  ip?: string | null;
  adapter?: string | null;
  isWired: boolean;
  broadcast?: string | null;
}

export interface ReadinessSummary {
  ok: number;
  warn: number;
  fail: number;
  wakeReady: boolean;
}

export interface PendingPowerInfo {
  action: string;
  secondsLeft: number;
  cancellable: boolean;
}

export interface StatusSnapshot {
  pcId: string;
  pcName: string;
  state: PowerState;
  uptimeSec: number;
  cpuPercent: number;
  ramUsedMb: number;
  ramTotalMb: number;
  battery?: BatteryInfo | null;
  activeWindow?: string | null;
  userSession?: UserSessionInfo | null;
  network?: NetworkInfo | null;
  agentVersion: string;
  readiness?: ReadinessSummary | null;
  pendingPower?: PendingPowerInfo | null;
  at: string;
}

export type StepType =
  | 'launch'
  | 'open'
  | 'url'
  | 'close'
  | 'wait'
  | 'volume'
  | 'command'
  | 'power'
  | 'notify'
  | 'uwp';

export interface ScenarioStep {
  id?: string;
  type: StepType;
  title?: string;
  enabled?: boolean;
  timeoutSec?: number;
  continueOnError?: boolean;

  path?: string;
  args?: string;
  cwd?: string;
  window?: 'normal' | 'min' | 'max';
  waitForExit?: boolean;
  appId?: string;

  url?: string;
  browser?: string;

  process?: string;
  force?: boolean;

  seconds?: number;

  level?: number;
  mute?: boolean;

  shell?: 'cmd' | 'powershell';
  command?: string;
  hidden?: boolean;

  action?: 'sleep' | 'hibernate' | 'shutdown' | 'lock';
  delaySec?: number;

  text?: string;
  notifyLevel?: 'info' | 'warn' | 'error';
}

export interface Scenario {
  id: string;
  name: string;
  icon?: string | null;
  favorite?: boolean;
  onError?: 'continue' | 'stop';
  steps: ScenarioStep[];
  updatedAt?: string;
}

export interface StepResult {
  index: number;
  type: string;
  title: string;
  status: 'ok' | 'error' | 'skipped';
  message?: string | null;
  durationMs: number;
}

export interface ScenarioRunReport {
  runId: string;
  id: string;
  name: string;
  ok: boolean;
  steps: StepResult[];
  startedAt: string;
  finishedAt?: string | null;
}

export type ScheduleAction = 'wake' | 'shutdown' | 'sleep' | 'hibernate' | 'reboot' | 'scenario';

export interface Schedule {
  id: string;
  name: string;
  enabled: boolean;
  action: ScheduleAction;
  scenarioId?: string | null;
  time: string;
  days: number[];
  date?: string | null;
  wakeBeforeSec?: number;
  syncedAt?: string | null;
  lastFiredAt?: string | null;
  updatedAt?: string;
}

export type CheckStatus = 'ok' | 'warn' | 'fail' | 'unknown';

export interface CheckResult {
  id: string;
  title: string;
  status: CheckStatus;
  detail: string;
  canFix: boolean;
  fixHint?: string | null;
  docUrl?: string | null;
  requiresElevation: boolean;
  manualSteps?: string[] | null;
  checkedAt: string;
}

export interface AppEntry {
  name: string;
  path?: string | null;
  args?: string | null;
  cwd?: string | null;
  iconB64?: string | null;
  source: string;
  uwpAppId?: string | null;
}

export interface EventRecord {
  id: number;
  pcId: string;
  kind: string;
  summary: string;
  at: string;
  source: string;
}

export interface AgentSettings {
  countdownSec: number;
  allowShellSteps: boolean;
  allowAutoLogon: boolean;
  relayUrl: string;
  lanPort: number;
  lanEnabled: boolean;
  heartbeatSec: number;
  language: string;
  wakeTimersOnBattery: boolean;
  logLevel: string;
}

/** Сопряжённый компьютер в памяти телефона. */
export interface PairedPc {
  pcId: string;
  pcName: string;
  pairId: string;
  pcPub: string;
  mac?: string | null;
  lanIps: string[];
  lanPort: number;
  relayUrl?: string | null;
  fingerprint: string;
  pairedAt: string;
}

export function isScheduleSynced(schedule: Schedule): boolean {
  if (!schedule.syncedAt || !schedule.updatedAt) return false;
  return new Date(schedule.syncedAt).getTime() >= new Date(schedule.updatedAt).getTime();
}
