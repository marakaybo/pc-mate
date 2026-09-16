namespace PcMate.Core.Protocol;

/// <summary>Имена команд телефон → ПК (docs/protocol.md §4).</summary>
public static class Commands
{
    public const string StatusGet = "status.get";

    public const string PowerShutdown = "power.shutdown";
    public const string PowerReboot = "power.reboot";
    public const string PowerSleep = "power.sleep";
    public const string PowerHibernate = "power.hibernate";
    public const string PowerLock = "power.lock";
    public const string PowerCancel = "power.cancel";

    public const string ScenarioList = "scenario.list";
    public const string ScenarioGet = "scenario.get";
    public const string ScenarioSave = "scenario.save";
    public const string ScenarioDelete = "scenario.delete";
    public const string ScenarioRun = "scenario.run";
    public const string ScenarioCancel = "scenario.cancel";

    public const string AppsList = "apps.list";

    public const string ScheduleList = "schedule.list";
    public const string ScheduleSave = "schedule.save";
    public const string ScheduleDelete = "schedule.delete";
    public const string ScheduleSync = "schedule.sync";

    public const string ArriveSet = "arrive.set";
    public const string ArriveCancel = "arrive.cancel";

    public const string WizardRun = "wizard.run";
    public const string WizardFix = "wizard.fix";
    public const string WakeTest = "wake.test";

    public const string HistoryList = "history.list";
    public const string PairRevoke = "pair.revoke";

    /// <summary>Только с самого ПК: создать новый код сопряжения для QR.</summary>
    public const string PairOffer = "pair.offer";

    /// <summary>Только с самого ПК: список сопряжённых телефонов.</summary>
    public const string PairList = "pair.list";

    public const string AgentInfo = "agent.info";
    public const string SettingsGet = "agent.settings.get";
    public const string SettingsSet = "agent.settings.set";
}

/// <summary>Имена событий ПК → телефон (docs/protocol.md §5).</summary>
public static class Events
{
    public const string PowerState = "power.state";
    public const string PowerWake = "power.wake";
    public const string PowerCountdown = "power.countdown";
    public const string Status = "status";
    public const string ScenarioProgress = "scenario.progress";
    public const string ScenarioDone = "scenario.done";
    public const string WizardResult = "wizard.result";
    public const string WakeTestResult = "wake.test.result";
    public const string ScheduleFired = "schedule.fired";
    public const string Notify = "notify";
    public const string AgentHello = "agent.hello";
}

/// <summary>Служебные сообщения устройство ↔ сервер (docs/protocol.md §6).</summary>
public static class SysMessages
{
    public const string Hello = "hello";
    public const string HelloOk = "hello.ok";
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string Heartbeat = "heartbeat";
    public const string PeerState = "peer.state";
    public const string RouteFail = "route.fail";
    public const string PairOffer = "pair.offer";
    public const string PairClaim = "pair.claim";
    public const string PairResult = "pair.result";
    public const string WolRequest = "wol.request";
    public const string WolResult = "wol.result";
    public const string BridgePing = "bridge.ping";
    public const string BridgePong = "bridge.pong";
    public const string SchedulePush = "schedule.push";
    public const string Error = "error";
}

public static class ErrorCodes
{
    public const string Unknown = "E_UNKNOWN";
    public const string UnknownCommand = "E_UNKNOWN_CMD";
    public const string BadArgs = "E_BAD_ARGS";
    public const string NoSession = "E_NO_SESSION";
    public const string Denied = "E_DENIED";
    public const string Replay = "E_REPLAY";
    public const string Crypto = "E_CRYPTO";
    public const string Busy = "E_BUSY";
    public const string NotFound = "E_NOT_FOUND";
    public const string Os = "E_OS";
    public const string Timeout = "E_TIMEOUT";
    public const string Disabled = "E_DISABLED";
    public const string Version = "E_VERSION";
}

public static class DeviceRoles
{
    public const string Pc = "pc";
    public const string Phone = "phone";
    public const string Bridge = "bridge";
}

public static class PowerStates
{
    public const string Running = "running";
    public const string Sleeping = "sleeping";
    public const string Hibernating = "hibernating";
    public const string ShuttingDown = "shutting-down";
    public const string Locked = "locked";
    public const string Off = "off";
    public const string Unknown = "unknown";
}
