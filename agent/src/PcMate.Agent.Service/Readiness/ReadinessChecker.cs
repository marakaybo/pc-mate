using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcMate.Agent.Service.Net;
using PcMate.Agent.Service.Services;
using PcMate.Agent.Service.State;
using PcMate.Core.Models;
using PcMate.Core.Util;

namespace PcMate.Agent.Service.Readiness;

/// <summary>
/// Мастер проверки готовности ПК к удалённому включению (ТЗ §4.1.2).
/// Каждая проверка возвращает статус и, если возможно, умеет чинить себя сама.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class ReadinessChecker
{
    private const string SubSleepGuid = "238c9fa8-0aad-41ed-83f4-97be242c8f20";
    private const string WakeTimersGuid = "bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d";
    private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Power";
    private const string HiberbootKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";
    private const string WinlogonKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

    private readonly ILogger<ReadinessChecker> _log;
    private readonly AgentStore _store;
    private readonly RelayStatus _relay;

    private List<CheckResult> _last = new();

    public ReadinessChecker(ILogger<ReadinessChecker> log, AgentStore store, RelayStatus relay)
    {
        _log = log;
        _store = store;
        _relay = relay;
    }

    public IReadOnlyList<CheckResult> LastResults => _last;

    public async Task<List<CheckResult>> RunAsync(IEnumerable<string>? ids = null, CancellationToken ct = default)
    {
        var wanted = ids?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = new List<CheckResult>();

        foreach (var id in CheckIds.Ordered)
        {
            if (wanted is not null && !wanted.Contains(id)) continue;
            try
            {
                results.Add(await RunSingleAsync(id, ct).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Проверка {Id} завершилась ошибкой", id);
                results.Add(new CheckResult
                {
                    Id = id,
                    Title = TitleOf(id),
                    Status = CheckStatus.Unknown,
                    Detail = "Не удалось выполнить проверку: " + ex.Message
                });
            }
        }

        if (wanted is null) _last = results;
        else
        {
            // Частичный прогон обновляет только затронутые пункты.
            var merged = _last.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var r in results) merged[r.Id] = r;
            _last = CheckIds.Ordered.Where(merged.ContainsKey).Select(id => merged[id]).ToList();
        }

        return results;
    }

    public ReadinessSummary Summarize(IEnumerable<CheckResult>? results = null)
    {
        var list = (results ?? _last).ToList();
        var summary = new ReadinessSummary
        {
            Ok = list.Count(r => r.Status == CheckStatus.Ok),
            Warn = list.Count(r => r.Status == CheckStatus.Warn),
            Fail = list.Count(r => r.Status == CheckStatus.Fail)
        };

        // «Готов к пробуждению» = таймеры разрешены и есть рабочее состояние сна,
        // либо сетевая карта настроена на магический пакет.
        bool Ok(string id) => list.FirstOrDefault(r => r.Id == id)?.Status == CheckStatus.Ok;
        summary.WakeReady = Ok(CheckIds.WakeTimers) && (Ok(CheckIds.SleepStates) || Ok(CheckIds.HibernateEnabled));
        return summary;
    }

    private Task<CheckResult> RunSingleAsync(string id, CancellationToken ct) => id switch
    {
        CheckIds.HibernateEnabled => CheckHibernateAsync(ct),
        CheckIds.SleepStates => CheckSleepStatesAsync(ct),
        CheckIds.WakeTimers => CheckWakeTimersAsync(ct),
        CheckIds.FastStartup => CheckFastStartupAsync(ct),
        CheckIds.NicWakeArmed => CheckNicWakeArmedAsync(ct),
        CheckIds.NicMagicPacket => CheckNicMagicPacketAsync(ct),
        CheckIds.WiredConnection => CheckWiredAsync(ct),
        CheckIds.NetworkIdentity => CheckNetworkIdentityAsync(ct),
        CheckIds.StaticIp => CheckStaticIpAsync(ct),
        CheckIds.BiosWol => CheckBiosWolAsync(ct),
        CheckIds.ServerLink => CheckServerLinkAsync(ct),
        CheckIds.AutoLogon => CheckAutoLogonAsync(ct),
        _ => Task.FromResult(new CheckResult
        {
            Id = id, Title = id, Status = CheckStatus.Unknown, Detail = "Неизвестная проверка."
        })
    };

    // ------------------------------------------------------------------ 1

    private async Task<CheckResult> CheckHibernateAsync(CancellationToken ct)
    {
        var caps = PowerCapabilities.Query();
        var registryEnabled = ReadDword(Registry.LocalMachine, PowerKey, "HibernateEnabled") == 1;
        var available = caps?.S4 == true && caps.HiberFilePresent;

        var result = new CheckResult
        {
            Id = CheckIds.HibernateEnabled,
            Title = TitleOf(CheckIds.HibernateEnabled),
            CanFix = true,
            RequiresElevation = true,
            FixHint = "Включить гибернацию (powercfg /h on)"
        };

        if (available || registryEnabled)
        {
            result.Status = CheckStatus.Ok;
            result.Detail = "Гибернация доступна — компьютер может засыпать глубоко и просыпаться по таймеру.";
            result.CanFix = false;
        }
        else
        {
            result.Status = CheckStatus.Fail;
            result.Detail = "Гибернация выключена. Это лучший режим для PC MATE: из неё компьютер " +
                            "просыпается по таймеру и сохраняет открытые программы.";
        }

        await Task.CompletedTask;
        return result;
    }

    // ------------------------------------------------------------------ 2

    private async Task<CheckResult> CheckSleepStatesAsync(CancellationToken ct)
    {
        var caps = PowerCapabilities.Query();
        var result = new CheckResult
        {
            Id = CheckIds.SleepStates,
            Title = TitleOf(CheckIds.SleepStates),
            CanFix = false
        };

        if (caps is null)
        {
            result.Status = CheckStatus.Unknown;
            result.Detail = "Не удалось получить список режимов сна у системы.";
            return result;
        }

        result.Detail = "Доступные режимы: " + caps.DescribeStates();

        if (caps.S3 || caps.S4)
        {
            result.Status = CheckStatus.Ok;
        }
        else if (caps.ModernStandby)
        {
            result.Status = CheckStatus.Warn;
            result.Detail += ". Это Modern Standby (S0): компьютер не спит глубоко, " +
                             "пробуждение по таймеру работает, но расход энергии выше.";
        }
        else
        {
            result.Status = CheckStatus.Fail;
            result.Detail += ". Нет ни S3, ни S4 — удалённое включение возможно только из выключенного состояния (S5) через Wake-on-LAN.";
            result.ManualSteps = new List<string>
            {
                "Откройте BIOS/UEFI и включите режим сна S3 (ACPI Sleep State / Suspend to RAM).",
                "Либо включите гибернацию командой powercfg /h on и пользуйтесь ей."
            };
        }

        await Task.CompletedTask;
        return result;
    }

    // ------------------------------------------------------------------ 3

    private async Task<CheckResult> CheckWakeTimersAsync(CancellationToken ct)
    {
        var result = new CheckResult
        {
            Id = CheckIds.WakeTimers,
            Title = TitleOf(CheckIds.WakeTimers),
            CanFix = true,
            RequiresElevation = true,
            FixHint = "Разрешить таймеры пробуждения в текущей схеме питания"
        };

        var (ac, dc) = await ReadWakeTimerIndexesAsync(ct).ConfigureAwait(false);

        if (ac is null)
        {
            result.Status = CheckStatus.Unknown;
            result.Detail = "Не удалось прочитать параметр «Разрешить таймеры пробуждения».";
            return result;
        }

        var acOn = ac.Value != 0;
        var dcOn = dc is null || dc.Value != 0;
        var hasBattery = PowerCapabilities.Query()?.BatteriesPresent == true;

        result.Detail = $"От сети: {(acOn ? "разрешены" : "запрещены")}" +
                        (hasBattery ? $", от батареи: {(dcOn ? "разрешены" : "запрещены")}" : "");

        if (acOn && (!hasBattery || dcOn || !_store.Settings.WakeTimersOnBattery))
        {
            result.Status = acOn ? CheckStatus.Ok : CheckStatus.Fail;
            if (hasBattery && !dcOn)
            {
                result.Status = CheckStatus.Warn;
                result.Detail += ". На батарее компьютер по таймеру не проснётся — " +
                                 "включите параметр «таймеры пробуждения от батареи», если это ноутбук.";
            }
        }
        else
        {
            result.Status = CheckStatus.Fail;
            result.Detail += ". Без этого расписание «включить» работать не будет.";
        }

        return result;
    }

    private static async Task<(int? Ac, int? Dc)> ReadWakeTimerIndexesAsync(CancellationToken ct)
    {
        var res = await ProcessRunner.PowerCfgAsync(
            $"/q scheme_current {SubSleepGuid} {WakeTimersGuid}", ct).ConfigureAwait(false);
        if (!res.Ok) return (null, null);

        // Локализованные подписи игнорируем: индексы всегда идут как 0x........, сначала AC, потом DC.
        var matches = HexIndex().Matches(res.StdOut);
        if (matches.Count < 2) return (null, null);

        var values = matches.Select(m => Convert.ToInt32(m.Groups[1].Value, 16)).ToList();
        return (values[^2], values[^1]);
    }

    // ------------------------------------------------------------------ 4

    private async Task<CheckResult> CheckFastStartupAsync(CancellationToken ct)
    {
        var value = ReadDword(Registry.LocalMachine, HiberbootKey, "HiberbootEnabled");
        var result = new CheckResult
        {
            Id = CheckIds.FastStartup,
            Title = TitleOf(CheckIds.FastStartup),
            CanFix = value == 1,
            RequiresElevation = true,
            FixHint = "Отключить быстрый запуск (HiberbootEnabled = 0)"
        };

        if (value is null)
        {
            result.Status = CheckStatus.Unknown;
            result.Detail = "Параметр быстрого запуска не найден (возможно, гибернация отключена).";
        }
        else if (value == 0)
        {
            result.Status = CheckStatus.Ok;
            result.Detail = "Быстрый запуск отключён — пробуждение из полностью выключенного состояния (S5) возможно.";
            result.CanFix = false;
        }
        else
        {
            result.Status = CheckStatus.Warn;
            result.Detail = "Быстрый запуск включён. Из полного выключения (S5) Wake-on-LAN у большинства " +
                            "материнских плат работать не будет. Для сна и гибернации это не мешает.";
        }

        await Task.CompletedTask;
        return result;
    }

    // ------------------------------------------------------------------ 5

    private async Task<CheckResult> CheckNicWakeArmedAsync(CancellationToken ct)
    {
        var adapter = NetworkProbe.GetPrimaryAdapter();
        var result = new CheckResult
        {
            Id = CheckIds.NicWakeArmed,
            Title = TitleOf(CheckIds.NicWakeArmed),
            RequiresElevation = true,
            FixHint = "Разрешить сетевой карте будить компьютер (powercfg /deviceenablewake)"
        };

        if (adapter is null)
        {
            result.Status = CheckStatus.Fail;
            result.Detail = "Активный сетевой адаптер не найден.";
            return result;
        }

        var armed = await ProcessRunner.PowerCfgAsync("/devicequery wake_armed", ct).ConfigureAwait(false);
        var programmable = await ProcessRunner.PowerCfgAsync("/devicequery wake_programmable", ct).ConfigureAwait(false);

        var isArmed = ContainsDevice(armed.StdOut, adapter.Description);
        var isProgrammable = ContainsDevice(programmable.StdOut, adapter.Description);

        if (isArmed)
        {
            result.Status = CheckStatus.Ok;
            result.Detail = $"Адаптер «{adapter.Description}» может будить компьютер.";
            result.CanFix = false;
        }
        else if (isProgrammable)
        {
            result.Status = CheckStatus.Fail;
            result.CanFix = true;
            result.Detail = $"Адаптер «{adapter.Description}» умеет будить компьютер, но это запрещено в настройках Windows.";
        }
        else
        {
            result.Status = CheckStatus.Warn;
            result.CanFix = true;
            result.Detail = $"Windows не показывает адаптер «{adapter.Description}» среди устройств, " +
                            "которым разрешено пробуждение. Обычно причина — выключенный WOL в BIOS/UEFI " +
                            "или драйвер без поддержки пробуждения.";
        }

        return result;
    }

    // ------------------------------------------------------------------ 6

    private async Task<CheckResult> CheckNicMagicPacketAsync(CancellationToken ct)
    {
        var adapter = NetworkProbe.GetPrimaryAdapter();
        var result = new CheckResult
        {
            Id = CheckIds.NicMagicPacket,
            Title = TitleOf(CheckIds.NicMagicPacket),
            RequiresElevation = true,
            FixHint = "Включить пробуждение по магическому пакету в свойствах адаптера"
        };

        if (adapter is null)
        {
            result.Status = CheckStatus.Fail;
            result.Detail = "Активный сетевой адаптер не найден.";
            return result;
        }

        var script = $$"""
                       $ErrorActionPreference = 'SilentlyContinue'
                       $name = '{{EscapePs(adapter.Name)}}'
                       $pm  = Get-NetAdapterPowerManagement -Name $name
                       $adv = Get-NetAdapterAdvancedProperty -Name $name -RegistryKeyword '*WakeOnMagicPacket'
                       [pscustomobject]@{
                         powerMgmt = "$($pm.WakeOnMagicPacket)"
                         advValue  = "$($adv.RegistryValue)"
                         supported = [bool]$pm
                       } | ConvertTo-Json -Compress
                       """;

        var res = await ProcessRunner.PowerShellAsync(script, ct: ct).ConfigureAwait(false);
        var json = res.StdOut.Trim();

        string powerMgmt = "", advValue = "";
        var supported = false;
        if (json.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                powerMgmt = doc.RootElement.TryGetProperty("powerMgmt", out var p) ? p.GetString() ?? "" : "";
                advValue = doc.RootElement.TryGetProperty("advValue", out var a) ? a.GetString() ?? "" : "";
                supported = doc.RootElement.TryGetProperty("supported", out var s) && s.ValueKind == JsonValueKind.True;
            }
            catch (JsonException) { /* разберём как «неизвестно» ниже */ }
        }

        var enabled = powerMgmt.Contains("Enabled", StringComparison.OrdinalIgnoreCase) || advValue.Trim() == "1";

        if (enabled)
        {
            result.Status = CheckStatus.Ok;
            result.Detail = $"«Пробуждение по магическому пакету» включено на адаптере «{adapter.Name}».";
            result.CanFix = false;
        }
        else if (supported || !string.IsNullOrEmpty(advValue))
        {
            result.Status = CheckStatus.Fail;
            result.CanFix = true;
            result.Detail = $"На адаптере «{adapter.Name}» выключено пробуждение по магическому пакету — " +
                            "команда «включить» с телефона не дойдёт.";
        }
        else
        {
            result.Status = CheckStatus.Warn;
            result.CanFix = true;
            result.Detail = "Драйвер адаптера не сообщает о поддержке магического пакета. " +
                            "Попробуйте исправление — оно запишет настройку напрямую.";
        }

        return result;
    }

    // ------------------------------------------------------------------ 7

    private async Task<CheckResult> CheckWiredAsync(CancellationToken ct)
    {
        var adapter = NetworkProbe.GetPrimaryAdapter();
        var result = new CheckResult
        {
            Id = CheckIds.WiredConnection,
            Title = TitleOf(CheckIds.WiredConnection),
            CanFix = false
        };

        if (adapter is null)
        {
            result.Status = CheckStatus.Fail;
            result.Detail = "Активное подключение не найдено.";
        }
        else if (adapter.IsVirtual)
        {
            result.Status = CheckStatus.Fail;
            result.Detail = $"Единственный активный адаптер «{adapter.Name}» — виртуальный. " +
                            "Магический пакет принимает только физическая сетевая карта.";
            result.ManualSteps = new List<string>
            {
                "Подключите компьютер к роутеру кабелем Ethernet или включите Wi-Fi.",
                "Виртуальные адаптеры (VPN, Hyper-V, VirtualBox) для Wake-on-LAN не годятся."
            };
        }
        else if (adapter.IsWired)
        {
            result.Status = CheckStatus.Ok;
            result.Detail = $"Подключение по кабелю: {adapter.Name}.";
        }
        else
        {
            result.Status = CheckStatus.Warn;
            result.Detail = $"Активное подключение — Wi-Fi ({adapter.Name}). " +
                            "Wake-on-WLAN на настольных ПК почти не работает: включение с телефона " +
                            "будет доступно только по таймеру пробуждения.";
            result.ManualSteps = new List<string>
            {
                "Подключите компьютер к роутеру кабелем Ethernet.",
                "Расписания «включить» работают и без кабеля — они не используют сеть."
            };
        }

        await Task.CompletedTask;
        return result;
    }

    // ------------------------------------------------------------------ 8

    private async Task<CheckResult> CheckNetworkIdentityAsync(CancellationToken ct)
    {
        var adapter = NetworkProbe.GetPrimaryAdapter();
        var result = new CheckResult
        {
            Id = CheckIds.NetworkIdentity,
            Title = TitleOf(CheckIds.NetworkIdentity),
            CanFix = false
        };

        if (adapter?.Ip is null || string.IsNullOrEmpty(adapter.Mac))
        {
            result.Status = CheckStatus.Fail;
            result.Detail = "Не удалось определить MAC-адрес и локальный IP — без них Wake-on-LAN невозможен.";
        }
        else
        {
            result.Status = CheckStatus.Ok;
            result.Detail = $"MAC {adapter.Mac}, IP {adapter.Ip}, широковещательный адрес {adapter.Broadcast}.";
        }

        await Task.CompletedTask;
        return result;
    }

    // ------------------------------------------------------------------ 9

    private async Task<CheckResult> CheckStaticIpAsync(CancellationToken ct)
    {
        var adapter = NetworkProbe.GetPrimaryAdapter();
        var result = new CheckResult
        {
            Id = CheckIds.StaticIp,
            Title = TitleOf(CheckIds.StaticIp),
            CanFix = false
        };

        if (adapter?.Ip is null)
        {
            result.Status = CheckStatus.Unknown;
            result.Detail = "Нет активного IPv4-адреса.";
            return result;
        }

        var historyPath = Path.Combine(_store.RootPath, "network-history.json");
        var history = ReadIpHistory(historyPath);
        var current = adapter.Ip.ToString();
        var changed = history.Count > 0 && history[^1] != current;

        if (history.Count == 0 || changed)
        {
            history.Add(current);
            if (history.Count > 10) history.RemoveRange(0, history.Count - 10);
            try { File.WriteAllText(historyPath, PcMateJson.Serialize(history)); }
            catch (Exception ex) { _log.LogDebug(ex, "Не удалось сохранить историю IP"); }
        }

        if (!adapter.IsDhcp)
        {
            result.Status = CheckStatus.Ok;
            result.Detail = $"Задан статический IP {current} — он не поменяется после перезагрузки роутера.";
        }
        else if (changed)
        {
            result.Status = CheckStatus.Warn;
            result.Detail = $"IP-адрес изменился ({history[^2]} → {current}). Телефон может не найти компьютер в локальной сети.";
            result.ManualSteps = RouterReservationSteps(adapter.Mac, current);
        }
        else
        {
            result.Status = CheckStatus.Ok;
            result.Detail = $"Адрес {current} выдан по DHCP и пока не менялся. " +
                            "Для надёжности стоит закрепить его в роутере.";
            result.ManualSteps = RouterReservationSteps(adapter.Mac, current);
        }

        await Task.CompletedTask;
        return result;
    }

    private static List<string> RouterReservationSteps(string mac, string ip) => new()
    {
        "Откройте веб-интерфейс роутера (обычно 192.168.0.1 или 192.168.1.1).",
        "Найдите раздел DHCP → «Резервирование адресов» / «Address Reservation».",
        $"Закрепите за MAC-адресом {mac} адрес {ip}.",
        "Сохраните настройки — после этого компьютер всегда будет получать один и тот же IP."
    };

    // ------------------------------------------------------------------ 10

    private async Task<CheckResult> CheckBiosWolAsync(CancellationToken ct)
    {
        var result = new CheckResult
        {
            Id = CheckIds.BiosWol,
            Title = TitleOf(CheckIds.BiosWol),
            Status = CheckStatus.Unknown,
            CanFix = false,
            Detail = "Проверить настройку BIOS/UEFI программно нельзя. " +
                     "Запустите тестовое пробуждение — оно покажет, работает ли WOL на самом деле.",
            ManualSteps = new List<string>
            {
                "Перезагрузите компьютер и войдите в BIOS/UEFI (обычно Del или F2 при включении).",
                "Найдите пункт Wake on LAN / Power on by PCI-E / Resume by LAN / ErP.",
                "Включите Wake on LAN и выключите ErP / Deep Sleep, если такой пункт есть.",
                "Сохраните настройки (F10) и вернитесь в Windows.",
                "Запустите тестовое пробуждение в PC MATE."
            }
        };

        // Косвенный признак: если карта «wake_armed», BIOS почти наверняка не мешает.
        var adapter = NetworkProbe.GetPrimaryAdapter();
        if (adapter is not null)
        {
            var armed = await ProcessRunner.PowerCfgAsync("/devicequery wake_armed", ct).ConfigureAwait(false);
            if (ContainsDevice(armed.StdOut, adapter.Description))
            {
                result.Status = CheckStatus.Warn;
                result.Detail = "Windows разрешает адаптеру будить компьютер — это хороший признак, " +
                                "но окончательно подтвердит только тестовое пробуждение.";
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ 11

    private async Task<CheckResult> CheckServerLinkAsync(CancellationToken ct)
    {
        var result = new CheckResult
        {
            Id = CheckIds.ServerLink,
            Title = TitleOf(CheckIds.ServerLink),
            CanFix = false
        };

        if (string.IsNullOrWhiteSpace(_store.Settings.RelayUrl))
        {
            result.Status = CheckStatus.Warn;
            result.Detail = "Сервер-ретранслятор не настроен. Управление работает только из домашней сети.";
            result.ManualSteps = new List<string>
            {
                "Укажите адрес сервера в настройках агента (значок в трее → Настройки).",
                "Или пользуйтесь локальным режимом — он не требует сервера."
            };
        }
        else if (_relay.Connected)
        {
            result.Status = CheckStatus.Ok;
            result.Detail = $"Связь с сервером есть: {_relay.RelayUrl}.";
        }
        else
        {
            result.Status = CheckStatus.Fail;
            result.Detail = $"Нет связи с сервером {_store.Settings.RelayUrl}. " +
                            (_relay.LastError ?? "Проверьте интернет и адрес сервера.");
        }

        await Task.CompletedTask;
        return result;
    }

    // ------------------------------------------------------------------ 12

    private async Task<CheckResult> CheckAutoLogonAsync(CancellationToken ct)
    {
        var auto = ReadString(Registry.LocalMachine, WinlogonKey, "AutoAdminLogon");
        var user = ReadString(Registry.LocalMachine, WinlogonKey, "DefaultUserName");
        var enabled = auto == "1";

        var result = new CheckResult
        {
            Id = CheckIds.AutoLogon,
            Title = TitleOf(CheckIds.AutoLogon),
            CanFix = false,
            Status = enabled ? CheckStatus.Warn : CheckStatus.Ok,
            Detail = enabled
                ? $"Автовход включён для «{user}». Сценарии запустятся даже после включения из полного выключения, " +
                  "но любой, кто имеет физический доступ к компьютеру, попадёт в вашу учётную запись."
                : "Автовход выключен — это безопаснее. После гибернации сеанс сохраняется, " +
                  "и сценарии работают без автовхода.",
            ManualSteps = enabled
                ? new List<string> { "Отключить автовход: netplwiz → включить «Требовать ввод имени пользователя и пароля»." }
                : new List<string>
                {
                    "Автовход нужен, только если вы будите компьютер из полностью выключенного состояния (S5).",
                    "Включается вручную: netplwiz → снять галочку «Требовать ввод имени пользователя и пароля».",
                    "PC MATE не хранит и не запрашивает пароль Windows."
                }
        };

        await Task.CompletedTask;
        return result;
    }

    // ------------------------------------------------------------------ Исправления

    public async Task<CheckResult> FixAsync(string id, CancellationToken ct = default)
    {
        _log.LogInformation("Исправление проверки {Id}", id);

        switch (id)
        {
            case CheckIds.HibernateEnabled:
                await ProcessRunner.PowerCfgAsync("/hibernate on", ct).ConfigureAwait(false);
                break;

            case CheckIds.WakeTimers:
            {
                await ProcessRunner.PowerCfgAsync(
                    $"/setacvalueindex scheme_current {SubSleepGuid} {WakeTimersGuid} 1", ct).ConfigureAwait(false);
                if (_store.Settings.WakeTimersOnBattery)
                    await ProcessRunner.PowerCfgAsync(
                        $"/setdcvalueindex scheme_current {SubSleepGuid} {WakeTimersGuid} 1", ct).ConfigureAwait(false);
                await ProcessRunner.PowerCfgAsync("/setactive scheme_current", ct).ConfigureAwait(false);
                break;
            }

            case CheckIds.FastStartup:
                WriteDword(Registry.LocalMachine, HiberbootKey, "HiberbootEnabled", 0);
                break;

            case CheckIds.NicWakeArmed:
            {
                var adapter = NetworkProbe.GetPrimaryAdapter();
                if (adapter is not null)
                {
                    await ProcessRunner.PowerCfgAsync($"/deviceenablewake \"{adapter.Description}\"", ct)
                        .ConfigureAwait(false);
                    // Некоторые драйверы регистрируются под именем подключения, а не описанием.
                    await ProcessRunner.PowerCfgAsync($"/deviceenablewake \"{adapter.Name}\"", ct)
                        .ConfigureAwait(false);
                }
                break;
            }

            case CheckIds.NicMagicPacket:
            {
                var adapter = NetworkProbe.GetPrimaryAdapter();
                if (adapter is not null)
                {
                    var script = $$"""
                                   $ErrorActionPreference = 'SilentlyContinue'
                                   $name = '{{EscapePs(adapter.Name)}}'
                                   Set-NetAdapterPowerManagement -Name $name -WakeOnMagicPacket Enabled
                                   Set-NetAdapterPowerManagement -Name $name -DeviceSleepOnDisconnect Disabled
                                   Set-NetAdapterAdvancedProperty -Name $name -RegistryKeyword '*WakeOnMagicPacket' -RegistryValue 1
                                   Set-NetAdapterAdvancedProperty -Name $name -RegistryKeyword '*WakeOnPattern' -RegistryValue 1
                                   """;
                    await ProcessRunner.PowerShellAsync(script, ct: ct).ConfigureAwait(false);
                }
                break;
            }

            default:
                return new CheckResult
                {
                    Id = id,
                    Title = TitleOf(id),
                    Status = CheckStatus.Unknown,
                    Detail = "Для этой проверки нет автоматического исправления."
                };
        }

        var updated = await RunAsync(new[] { id }, ct).ConfigureAwait(false);
        return updated.FirstOrDefault() ?? new CheckResult { Id = id, Title = TitleOf(id) };
    }

    // ------------------------------------------------------------------ Утилиты

    public static string TitleOf(string id) => id switch
    {
        CheckIds.HibernateEnabled => "Гибернация включена",
        CheckIds.SleepStates => "Доступные режимы сна (S3/S4)",
        CheckIds.WakeTimers => "Таймеры пробуждения разрешены",
        CheckIds.FastStartup => "Быстрый запуск отключён",
        CheckIds.NicWakeArmed => "Сетевая карта может будить компьютер",
        CheckIds.NicMagicPacket => "Пробуждение по магическому пакету",
        CheckIds.WiredConnection => "Подключение по кабелю",
        CheckIds.NetworkIdentity => "MAC-адрес и локальный IP определены",
        CheckIds.StaticIp => "Постоянный IP-адрес",
        CheckIds.BiosWol => "Wake-on-LAN в BIOS/UEFI",
        CheckIds.ServerLink => "Связь с сервером",
        CheckIds.AutoLogon => "Автовход после включения",
        _ => id
    };

    private static bool ContainsDevice(string output, string deviceName)
    {
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(deviceName)) return false;
        return output.Split('\n')
            .Select(l => l.Trim())
            .Any(l => l.Length > 0 &&
                      (l.Contains(deviceName, StringComparison.OrdinalIgnoreCase) ||
                       deviceName.Contains(l, StringComparison.OrdinalIgnoreCase)));
    }

    private static string EscapePs(string value) => value.Replace("'", "''");

    private static List<string> ReadIpHistory(string path)
    {
        try
        {
            if (!File.Exists(path)) return new List<string>();
            return PcMateJson.Deserialize<List<string>>(File.ReadAllText(path)) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static int? ReadDword(RegistryKey root, string subKey, string name)
    {
        try
        {
            using var key = root.OpenSubKey(subKey);
            var value = key?.GetValue(name);
            return value is null ? null : Convert.ToInt32(value);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(RegistryKey root, string subKey, string name)
    {
        try
        {
            using var key = root.OpenSubKey(subKey);
            return key?.GetValue(name)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private void WriteDword(RegistryKey root, string subKey, string name, int value)
    {
        try
        {
            using var key = root.OpenSubKey(subKey, writable: true) ?? root.CreateSubKey(subKey, true);
            key?.SetValue(name, value, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Не удалось записать {Key}\\{Name}", subKey, name);
            throw;
        }
    }

    [GeneratedRegex(@"0x([0-9a-fA-F]{8})")]
    private static partial Regex HexIndex();
}
