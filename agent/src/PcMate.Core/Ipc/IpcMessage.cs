using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PcMate.Core.Util;

namespace PcMate.Core.Ipc;

/// <summary>Сообщение канала «служба ↔ помощник в сеансе пользователя» (именованный канал).</summary>
public sealed class IpcMessage
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    [JsonPropertyName("re")] public string? Re { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; set; } = true;
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("data")] public JsonNode? Data { get; set; }

    public T? DataAs<T>() => Data is null ? default : Data.Deserialize<T>(PcMateJson.Options);
}

/// <summary>Имена сообщений IPC.</summary>
public static class IpcKinds
{
    // Служба → помощник
    public const string StepExec = "step.exec";
    public const string CountdownShow = "ui.countdown";
    public const string CountdownHide = "ui.countdown.cancel";
    public const string Notify = "ui.notify";
    public const string AppsList = "apps.list";
    public const string ActiveWindow = "ui.active-window";
    public const string ShowPairing = "ui.pairing";
    public const string ShowWizard = "ui.wizard";
    public const string StatusPush = "ui.status";
    public const string Lock = "session.lock";
    public const string Ping = "ping";

    // Помощник → служба
    public const string Hello = "hello";
    public const string CommandRun = "agent.command";   // помощник просит службу выполнить команду протокола
    public const string CountdownCancelled = "countdown.cancelled";
}

public sealed class IpcHello
{
    [JsonPropertyName("userName")] public string UserName { get; set; } = "";
    [JsonPropertyName("sessionId")] public int SessionId { get; set; }
    [JsonPropertyName("version")] public string Version { get; set; } = "";
}

public sealed class CountdownRequest
{
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("actionTitle")] public string ActionTitle { get; set; } = "";
    [JsonPropertyName("seconds")] public int Seconds { get; set; } = 30;
    [JsonPropertyName("cancellable")] public bool Cancellable { get; set; } = true;
}

public sealed class NotifyRequest
{
    [JsonPropertyName("title")] public string Title { get; set; } = "PC MATE";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("level")] public string Level { get; set; } = "info";
}

public sealed class StepExecResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class AgentCommandRequest
{
    [JsonPropertyName("cmd")] public string Cmd { get; set; } = "";
    [JsonPropertyName("args")] public JsonNode? Args { get; set; }
}
