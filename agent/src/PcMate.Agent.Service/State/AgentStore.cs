using System.Runtime.Versioning;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PcMate.Core.Crypto;
using PcMate.Core.Models;
using PcMate.Core.Util;

namespace PcMate.Agent.Service.State;

public sealed class AgentIdentity
{
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = Guid.NewGuid().ToString("D");
    [JsonPropertyName("pcName")] public string PcName { get; set; } = Environment.MachineName;
    [JsonPropertyName("publicKey")] public string PublicKey { get; set; } = "";
    [JsonPropertyName("privateKeyProtected")] public string PrivateKeyProtected { get; set; } = "";
    [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = TimeUtil.IsoNow();
}

public sealed class ServerRegistration
{
    [JsonPropertyName("relayUrl")] public string RelayUrl { get; set; } = "";
    [JsonPropertyName("deviceTokenProtected")] public string DeviceTokenProtected { get; set; } = "";
    [JsonPropertyName("registeredAt")] public string? RegisteredAt { get; set; }
}

/// <summary>
/// Файловое состояние агента в %ProgramData%\PcMate. Всё, что переживает перезагрузку.
/// Записи атомарные: сначала .tmp, затем замена.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentStore
{
    private readonly ILogger<AgentStore> _log;
    private readonly object _gate = new();

    public string RootPath { get; }
    public string LogsPath { get; }

    public AgentStore(ILogger<AgentStore> log)
    {
        _log = log;
        RootPath = Environment.GetEnvironmentVariable("PCMATE_DATA_DIR")
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PcMate");
        LogsPath = Path.Combine(RootPath, "logs");
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(LogsPath);
    }

    private string File_(string name) => Path.Combine(RootPath, name);

    // ---------- Идентичность ----------

    private AgentIdentity? _identity;
    private KeyPairX25519? _keys;

    public AgentIdentity Identity
    {
        get
        {
            EnsureIdentity();
            return _identity!;
        }
    }

    public KeyPairX25519 Keys
    {
        get
        {
            EnsureIdentity();
            return _keys!;
        }
    }

    private void EnsureIdentity()
    {
        lock (_gate)
        {
            if (_identity is not null && _keys is not null) return;

            var path = File_("identity.json");
            var identity = ReadJson<AgentIdentity>(path);

            if (identity is not null && SecretStore.TryUnprotect(identity.PrivateKeyProtected, out var priv))
            {
                try
                {
                    _keys = KeyPairX25519.FromPrivateKey(priv);
                    if (_keys.PublicKeyB64 != identity.PublicKey)
                    {
                        identity.PublicKey = _keys.PublicKeyB64;
                        WriteJson(path, identity);
                    }
                    _identity = identity;
                    return;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Не удалось восстановить ключ агента, создаём новый.");
                }
            }

            _keys = KeyPairX25519.Generate();
            _identity = new AgentIdentity
            {
                PublicKey = _keys.PublicKeyB64,
                PrivateKeyProtected = SecretStore.Protect(_keys.PrivateKey)
            };
            WriteJson(path, _identity);
            _log.LogInformation("Создана новая идентичность агента: {DeviceId}", _identity.DeviceId);
        }
    }

    // ---------- Настройки ----------

    private AgentSettings? _settings;

    public AgentSettings Settings
    {
        get
        {
            lock (_gate)
            {
                return _settings ??= ReadJson<AgentSettings>(File_("settings.json")) ?? new AgentSettings();
            }
        }
    }

    public AgentSettings SaveSettings(AgentSettings settings)
    {
        lock (_gate)
        {
            _settings = settings;
            WriteJson(File_("settings.json"), settings);
            return settings;
        }
    }

    // ---------- Сопряжённые телефоны ----------

    public List<PairedPhone> LoadPhones()
    {
        lock (_gate)
            return ReadJson<List<PairedPhone>>(File_("phones.json")) ?? new List<PairedPhone>();
    }

    public void SavePhones(List<PairedPhone> phones)
    {
        lock (_gate) WriteJson(File_("phones.json"), phones);
    }

    // ---------- Сценарии ----------

    public List<Scenario> LoadScenarios()
    {
        lock (_gate)
            return ReadJson<List<Scenario>>(File_("scenarios.json")) ?? new List<Scenario>();
    }

    public void SaveScenarios(List<Scenario> scenarios)
    {
        lock (_gate) WriteJson(File_("scenarios.json"), scenarios);
    }

    // ---------- Расписания ----------

    public List<Schedule> LoadSchedules()
    {
        lock (_gate)
            return ReadJson<List<Schedule>>(File_("schedules.json")) ?? new List<Schedule>();
    }

    public void SaveSchedules(List<Schedule> schedules)
    {
        lock (_gate) WriteJson(File_("schedules.json"), schedules);
    }

    // ---------- Регистрация на сервере ----------

    public ServerRegistration LoadServerRegistration()
    {
        lock (_gate)
            return ReadJson<ServerRegistration>(File_("server.json")) ?? new ServerRegistration();
    }

    public void SaveServerRegistration(ServerRegistration reg)
    {
        lock (_gate) WriteJson(File_("server.json"), reg);
    }

    public string? GetDeviceToken()
    {
        var reg = LoadServerRegistration();
        return SecretStore.TryUnprotect(reg.DeviceTokenProtected, out var bytes)
            ? System.Text.Encoding.UTF8.GetString(bytes)
            : null;
    }

    public void SaveDeviceToken(string relayUrl, string token)
    {
        SaveServerRegistration(new ServerRegistration
        {
            RelayUrl = relayUrl,
            DeviceTokenProtected = SecretStore.Protect(System.Text.Encoding.UTF8.GetBytes(token)),
            RegisteredAt = TimeUtil.IsoNow()
        });
    }

    // ---------- Журнал событий ----------

    private const int MaxEvents = 500;

    public void AppendEvent(string kind, string summary, string source = "pc")
    {
        lock (_gate)
        {
            var events = LoadEventsUnlocked();
            events.Add(new EventRecord
            {
                Id = events.Count == 0 ? 1 : events[^1].Id + 1,
                PcId = Identity.DeviceId,
                Kind = kind,
                Summary = summary,
                Source = source,
                At = TimeUtil.IsoNow()
            });
            if (events.Count > MaxEvents) events.RemoveRange(0, events.Count - MaxEvents);
            WriteJson(File_("events.json"), events);
        }
    }

    public List<EventRecord> LoadEvents(int limit = 100)
    {
        lock (_gate)
        {
            var events = LoadEventsUnlocked();
            return events.Count <= limit ? events : events.GetRange(events.Count - limit, limit);
        }
    }

    private List<EventRecord> LoadEventsUnlocked()
        => ReadJson<List<EventRecord>>(File_("events.json")) ?? new List<EventRecord>();

    // ---------- Примитивы файлов ----------

    private T? ReadJson<T>(string path) where T : class
    {
        try
        {
            if (!System.IO.File.Exists(path)) return null;
            var json = System.IO.File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(json) ? null : PcMateJson.Deserialize<T>(json);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Не удалось прочитать {Path}", path);
            return null;
        }
    }

    private void WriteJson<T>(string path, T value)
    {
        try
        {
            var tmp = path + ".tmp";
            System.IO.File.WriteAllText(tmp, PcMateJson.SerializePretty(value));
            if (System.IO.File.Exists(path))
                System.IO.File.Replace(tmp, path, null);
            else
                System.IO.File.Move(tmp, path);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Не удалось записать {Path}", path);
        }
    }
}
