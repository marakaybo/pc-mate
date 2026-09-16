using System.Text.Json.Serialization;
using PcMate.Core.Util;

namespace PcMate.Core.Models;

public static class StepTypes
{
    public const string Launch = "launch";
    public const string Open = "open";
    public const string Url = "url";
    public const string Close = "close";
    public const string Wait = "wait";
    public const string Volume = "volume";
    public const string Command = "command";
    public const string Power = "power";
    public const string Notify = "notify";
    public const string Uwp = "uwp";

    /// <summary>Шаги, которые обязаны выполняться в сеансе пользователя, а не в Session 0.</summary>
    public static readonly HashSet<string> RequireUserSession =
        new(StringComparer.OrdinalIgnoreCase) { Launch, Open, Url, Close, Volume, Uwp };
}

public sealed class ScenarioStep
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = StepTypes.Launch;
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("timeoutSec")] public int TimeoutSec { get; set; } = 60;
    [JsonPropertyName("continueOnError")] public bool? ContinueOnError { get; set; }

    // launch / open / uwp
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("args")] public string? Args { get; set; }
    [JsonPropertyName("cwd")] public string? Cwd { get; set; }
    [JsonPropertyName("window")] public string? Window { get; set; }
    [JsonPropertyName("waitForExit")] public bool WaitForExit { get; set; }
    [JsonPropertyName("appId")] public string? AppId { get; set; }

    // url
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("browser")] public string? Browser { get; set; }

    // close
    [JsonPropertyName("process")] public string? Process { get; set; }
    [JsonPropertyName("force")] public bool Force { get; set; }

    // wait
    [JsonPropertyName("seconds")] public double Seconds { get; set; }

    // volume
    [JsonPropertyName("level")] public int? Level { get; set; }
    [JsonPropertyName("mute")] public bool? Mute { get; set; }

    // command
    [JsonPropertyName("shell")] public string? Shell { get; set; }
    [JsonPropertyName("command")] public string? Command { get; set; }
    [JsonPropertyName("hidden")] public bool Hidden { get; set; } = true;

    // power
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("delaySec")] public int? DelaySec { get; set; }

    // notify
    // Имя отличается от volume.level намеренно: у шага один общий набор полей,
    // и два разных «level» (число громкости и уровень важности) конфликтовали бы.
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("notifyLevel")] public string? NotifyLevel { get; set; }

    public bool NeedsUserSession => StepTypes.RequireUserSession.Contains(Type);

    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title)
        ? Title!
        : Type switch
        {
            StepTypes.Launch => $"Запустить {System.IO.Path.GetFileName(Path) ?? "программу"}",
            StepTypes.Open => $"Открыть {System.IO.Path.GetFileName(Path) ?? Path}",
            StepTypes.Url => $"Открыть сайт {Url}",
            StepTypes.Close => $"Закрыть {Process}",
            StepTypes.Wait => $"Пауза {Seconds:0.#} с",
            StepTypes.Volume => Mute == true ? "Выключить звук" : $"Громкость {Level}%",
            StepTypes.Command => "Выполнить команду",
            StepTypes.Power => $"Питание: {Action}",
            StepTypes.Notify => "Уведомление на телефон",
            StepTypes.Uwp => $"Запустить приложение {AppId}",
            _ => Type
        };
}

public sealed class Scenario
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    [JsonPropertyName("name")] public string Name { get; set; } = "Новый сценарий";
    [JsonPropertyName("icon")] public string? Icon { get; set; }
    [JsonPropertyName("favorite")] public bool Favorite { get; set; }
    [JsonPropertyName("onError")] public string OnError { get; set; } = "continue";
    [JsonPropertyName("steps")] public List<ScenarioStep> Steps { get; set; } = new();
    [JsonPropertyName("updatedAt")] public string UpdatedAt { get; set; } = TimeUtil.IsoNow();

    public bool StopOnError => string.Equals(OnError, "stop", StringComparison.OrdinalIgnoreCase);
}

public sealed class StepResult
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "ok"; // ok | error | skipped
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("durationMs")] public long DurationMs { get; set; }
}

public sealed class ScenarioRunReport
{
    [JsonPropertyName("runId")] public string RunId { get; set; } = Guid.NewGuid().ToString("N")[..12];
    [JsonPropertyName("id")] public string ScenarioId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("steps")] public List<StepResult> Steps { get; set; } = new();
    [JsonPropertyName("startedAt")] public string StartedAt { get; set; } = TimeUtil.IsoNow();
    [JsonPropertyName("finishedAt")] public string? FinishedAt { get; set; }
}
