using PcMate.Core.Models;
using PcMate.Core.Util;
using Xunit;

namespace PcMate.Core.Tests;

public class ScheduleTests
{
    [Fact]
    public void NextOccurrence_FindsNextWeekdayEvening()
    {
        var schedule = new Schedule
        {
            Action = ScheduleActions.Wake,
            Time = "23:00",
            Days = new List<int> { 1, 2, 3, 4, 5 } // Пн–Пт
        };

        // Среда, 16 сентября 2026, 12:00
        var after = new DateTime(2026, 9, 16, 12, 0, 0);
        var next = schedule.NextOccurrence(after);

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 9, 16, 23, 0, 0), next!.Value);
    }

    [Fact]
    public void NextOccurrence_SkipsToMondayFromSaturday()
    {
        var schedule = new Schedule
        {
            Time = "07:30",
            Days = new List<int> { 1, 2, 3, 4, 5 }
        };

        // Суббота, 19 сентября 2026
        var next = schedule.NextOccurrence(new DateTime(2026, 9, 19, 10, 0, 0));

        Assert.NotNull(next);
        Assert.Equal(DayOfWeek.Monday, next!.Value.DayOfWeek);
        Assert.Equal(new TimeSpan(7, 30, 0), next.Value.TimeOfDay);
    }

    [Fact]
    public void NextOccurrence_AppliesWakeBeforeMargin()
    {
        var schedule = new Schedule
        {
            Action = ScheduleActions.Wake,
            Time = "23:00",
            Days = new List<int> { 3 },
            WakeBeforeSec = 300
        };

        var next = schedule.NextOccurrence(new DateTime(2026, 9, 16, 12, 0, 0));

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 9, 16, 22, 55, 0), next!.Value);
    }

    [Fact]
    public void NextOccurrence_OneTimeInPastReturnsNull()
    {
        var schedule = new Schedule
        {
            Time = "08:00",
            Days = new List<int>(),
            Date = "2026-09-01"
        };

        Assert.Null(schedule.NextOccurrence(new DateTime(2026, 9, 16, 12, 0, 0)));
    }

    [Fact]
    public void DisabledScheduleHasNoOccurrence()
    {
        var schedule = new Schedule { Enabled = false, Time = "10:00", Days = new List<int> { 0, 1, 2, 3, 4, 5, 6 } };
        Assert.Null(schedule.NextOccurrence(DateTime.Now));
    }

    [Fact]
    public void IsSynced_ComparesTimestamps()
    {
        var schedule = new Schedule
        {
            UpdatedAt = "2026-09-16T10:00:00Z",
            SyncedAt = "2026-09-16T10:00:05Z"
        };
        Assert.True(schedule.IsSynced);

        schedule.UpdatedAt = "2026-09-16T11:00:00Z";
        Assert.False(schedule.IsSynced);
    }

    [Theory]
    [InlineData(new[] { 1, 2, 3, 4, 5 }, "по будням")]
    [InlineData(new[] { 0, 6 }, "по выходным")]
    [InlineData(new[] { 0, 1, 2, 3, 4, 5, 6 }, "ежедневно")]
    public void DescribeDays_UsesNaturalLanguage(int[] days, string expected)
    {
        var schedule = new Schedule { Days = days.ToList() };
        Assert.Equal(expected, schedule.DescribeDays());
    }
}

public class ScenarioTests
{
    [Fact]
    public void StepsNeedingDesktopAreMarked()
    {
        Assert.True(new ScenarioStep { Type = StepTypes.Launch }.NeedsUserSession);
        Assert.True(new ScenarioStep { Type = StepTypes.Url }.NeedsUserSession);
        Assert.True(new ScenarioStep { Type = StepTypes.Volume }.NeedsUserSession);

        Assert.False(new ScenarioStep { Type = StepTypes.Wait }.NeedsUserSession);
        Assert.False(new ScenarioStep { Type = StepTypes.Power }.NeedsUserSession);
        Assert.False(new ScenarioStep { Type = StepTypes.Notify }.NeedsUserSession);
    }

    [Fact]
    public void DisplayTitleFallsBackToDescription()
    {
        var step = new ScenarioStep { Type = StepTypes.Wait, Seconds = 10 };
        Assert.Equal("Пауза 10 с", step.DisplayTitle);

        var named = new ScenarioStep { Type = StepTypes.Wait, Seconds = 10, Title = "Ждём Steam" };
        Assert.Equal("Ждём Steam", named.DisplayTitle);
    }

    [Fact]
    public void ScenarioRoundTripsThroughJson()
    {
        var scenario = new Scenario
        {
            Id = "evening",
            Name = "Вечер: игры и запись",
            Icon = "🎮",
            Favorite = true,
            OnError = "continue",
            Steps = new List<ScenarioStep>
            {
                new() { Type = StepTypes.Launch, Path = @"C:\Program Files (x86)\Steam\steam.exe" },
                new() { Type = StepTypes.Wait, Seconds = 10 },
                new() { Type = StepTypes.Url, Url = "https://youtube.com" },
                new() { Type = StepTypes.Volume, Level = 40 },
                new() { Type = StepTypes.Notify, Text = "ПК готов 👍" }
            }
        };

        var json = PcMateJson.Serialize(scenario);
        var restored = PcMateJson.Deserialize<Scenario>(json);

        Assert.NotNull(restored);
        Assert.Equal(scenario.Name, restored!.Name);
        Assert.Equal(5, restored.Steps.Count);
        Assert.Equal(@"C:\Program Files (x86)\Steam\steam.exe", restored.Steps[0].Path);
        Assert.Equal(40, restored.Steps[3].Level);
        Assert.Contains("👍", restored.Steps[4].Text);
    }
}

public class PairingOfferTests
{
    [Fact]
    public void OfferRoundTripsThroughUri()
    {
        var offer = new PairingOffer
        {
            PcId = Guid.NewGuid().ToString("D"),
            PcName = "DESKTOP-ПК",
            Pub = "abc123",
            Token = "token-1",
            Exp = TimeUtil.UnixNow() + 300,
            Relay = "wss://relay.example.com",
            Mac = "AA:BB:CC:DD:EE:FF",
            Lan = new List<string> { "192.168.1.50:8760" },
            Fp = "K7M2-9QX4-11BC"
        };

        var uri = offer.ToUri();
        Assert.StartsWith("pcmate://pair?d=", uri);

        var parsed = PairingOffer.FromUri(uri);
        Assert.NotNull(parsed);
        Assert.Equal(offer.PcId, parsed!.PcId);
        Assert.Equal(offer.PcName, parsed.PcName);
        Assert.Equal(offer.Mac, parsed.Mac);
        Assert.Single(parsed.Lan);
        Assert.False(parsed.IsExpired);
    }

    [Fact]
    public void ExpiredOfferIsDetected()
    {
        var offer = new PairingOffer { Exp = TimeUtil.UnixNow() - 1 };
        Assert.True(offer.IsExpired);
    }

    [Fact]
    public void GarbageUriReturnsNull()
    {
        Assert.Null(PairingOffer.FromUri("не ссылка"));
        Assert.Null(PairingOffer.FromUri("pcmate://pair?d=!!!"));
    }
}

public class MagicPacketTests
{
    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF")]
    [InlineData("aa-bb-cc-dd-ee-ff")]
    [InlineData("AABBCCDDEEFF")]
    public void ParsesEveryCommonMacFormat(string mac)
    {
        var bytes = MagicPacket.ParseMac(mac);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF }, bytes);
    }

    [Fact]
    public void BuildsCanonicalMagicPacket()
    {
        var packet = MagicPacket.Build("AA:BB:CC:DD:EE:FF");

        Assert.Equal(102, packet.Length);
        Assert.All(packet.Take(6), b => Assert.Equal(0xFF, b));

        for (var repeat = 0; repeat < 16; repeat++)
            Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF },
                packet.Skip(6 + repeat * 6).Take(6));
    }

    [Fact]
    public void RejectsBadMac()
    {
        Assert.Throws<FormatException>(() => MagicPacket.Build("AA:BB:CC"));
        Assert.Throws<ArgumentException>(() => MagicPacket.Build(""));
    }

    [Fact]
    public void DetectsSameSubnet()
    {
        Assert.True(MagicPacket.SameSubnet24("192.168.1.10", "192.168.1.50"));
        Assert.False(MagicPacket.SameSubnet24("192.168.1.10", "192.168.2.50"));
        Assert.False(MagicPacket.SameSubnet24("192.168.1.10", null));
    }
}
