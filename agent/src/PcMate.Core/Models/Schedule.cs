using System.Globalization;
using System.Text.Json.Serialization;
using PcMate.Core.Util;

namespace PcMate.Core.Models;

public static class ScheduleActions
{
    public const string Wake = "wake";
    public const string Shutdown = "shutdown";
    public const string Sleep = "sleep";
    public const string Hibernate = "hibernate";
    public const string Reboot = "reboot";
    public const string Scenario = "scenario";

    public static readonly HashSet<string> All =
        new(StringComparer.OrdinalIgnoreCase) { Wake, Shutdown, Sleep, Hibernate, Reboot, Scenario };
}

public sealed class Schedule
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("action")] public string Action { get; set; } = ScheduleActions.Wake;
    [JsonPropertyName("scenarioId")] public string? ScenarioId { get; set; }

    /// <summary>Локальное время ПК в формате HH:mm.</summary>
    [JsonPropertyName("time")] public string Time { get; set; } = "23:00";

    /// <summary>Дни недели: 0 = воскресенье … 6 = суббота. Пустой список = разовое событие.</summary>
    [JsonPropertyName("days")] public List<int> Days { get; set; } = new();

    /// <summary>Дата разового события, yyyy-MM-dd.</summary>
    [JsonPropertyName("date")] public string? Date { get; set; }

    /// <summary>Запас на прогрев: разбудить за N секунд до указанного времени.</summary>
    [JsonPropertyName("wakeBeforeSec")] public int WakeBeforeSec { get; set; }

    [JsonPropertyName("syncedAt")] public string? SyncedAt { get; set; }
    [JsonPropertyName("lastFiredAt")] public string? LastFiredAt { get; set; }
    [JsonPropertyName("updatedAt")] public string UpdatedAt { get; set; } = TimeUtil.IsoNow();

    [JsonIgnore]
    public bool IsSynced =>
        DateTimeOffset.TryParse(SyncedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var s) &&
        DateTimeOffset.TryParse(UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var u) &&
        s >= u;

    [JsonIgnore] public bool IsRecurring => Days.Count > 0;

    [JsonIgnore] public bool NeedsWakeTimer => string.Equals(Action, ScheduleActions.Wake, StringComparison.OrdinalIgnoreCase);

    public bool TryGetTimeOfDay(out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(Time)) return false;
        var parts = Time.Split(':');
        if (parts.Length < 2) return false;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return false;
        if (h is < 0 or > 23 || m is < 0 or > 59) return false;
        time = new TimeSpan(h, m, 0);
        return true;
    }

    /// <summary>Ближайшее срабатывание после <paramref name="after"/> в локальном времени ПК.</summary>
    public DateTime? NextOccurrence(DateTime after)
    {
        if (!Enabled) return null;
        if (!TryGetTimeOfDay(out var tod)) return null;
        var offset = TimeSpan.FromSeconds(NeedsWakeTimer ? WakeBeforeSec : 0);

        if (!IsRecurring)
        {
            if (!DateTime.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return null;
            var once = d.Date.Add(tod) - offset;
            return once > after ? once : null;
        }

        for (var i = 0; i <= 7; i++)
        {
            var day = after.Date.AddDays(i);
            if (!Days.Contains((int)day.DayOfWeek)) continue;
            var candidate = day.Add(tod) - offset;
            if (candidate > after) return candidate;
        }
        return null;
    }

    public string DescribeDays() => !IsRecurring
        ? (Date ?? "разово")
        : Days.Count == 7 ? "ежедневно"
        : Days.Count == 5 && Days.All(d => d is >= 1 and <= 5) ? "по будням"
        : Days.Count == 2 && Days.Contains(0) && Days.Contains(6) ? "по выходным"
        : string.Join(", ", Days.OrderBy(d => (d + 6) % 7).Select(d => RuDayNames[d]));

    private static readonly string[] RuDayNames = { "Вс", "Пн", "Вт", "Ср", "Чт", "Пт", "Сб" };
}
