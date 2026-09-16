using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PcMate.Agent.Service.Net;
using PcMate.Agent.Service.State;
using PcMate.Core.Crypto;
using PcMate.Core.Models;
using PcMate.Core.Util;

namespace PcMate.Agent.Service.Pairing;

/// <summary>
/// Сопряжение телефона и ПК по QR-коду: одноразовый токен на 5 минут,
/// обмен публичными ключами X25519, вывод направленных ключей шифрования.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PairingService
{
    private const int OfferLifetimeSeconds = 300;

    private readonly ILogger<PairingService> _log;
    private readonly AgentStore _store;
    private readonly ConcurrentDictionary<string, SessionKeys> _keyCache = new(StringComparer.Ordinal);

    private PairingOffer? _offer;
    private readonly object _gate = new();

    public PairingService(ILogger<PairingService> log, AgentStore store)
    {
        _log = log;
        _store = store;
    }

    public event Action<PairedPhone>? PhonePaired;

    /// <summary>Сообщает транспортам о новом коде: серверу нужно знать токен, чтобы принять claim.</summary>
    public event Action<PairingOffer>? OfferCreated;

    public PairingOffer? CurrentOffer
    {
        get
        {
            lock (_gate)
            {
                if (_offer is not null && _offer.IsExpired) _offer = null;
                return _offer;
            }
        }
    }

    /// <summary>Создаёт новый одноразовый код сопряжения и возвращает данные для QR.</summary>
    public PairingOffer CreateOffer()
    {
        var adapter = NetworkProbe.GetPrimaryAdapter();
        var settings = _store.Settings;

        var offer = new PairingOffer
        {
            PcId = _store.Identity.DeviceId,
            PcName = _store.Identity.PcName,
            Pub = _store.Identity.PublicKey,
            Token = B64Url.Encode(RandomNumberGenerator.GetBytes(16)),
            Exp = TimeUtil.UnixNow() + OfferLifetimeSeconds,
            Relay = string.IsNullOrWhiteSpace(settings.RelayUrl) ? null : settings.RelayUrl,
            Mac = adapter?.Mac,
            Fp = Fingerprint.ForKey(B64Url.Decode(_store.Identity.PublicKey))
        };

        offer.Lan.AddRange(NetworkProbe.LocalIps().Select(ip => $"{ip}:{settings.LanPort}"));

        lock (_gate) _offer = offer;
        _log.LogInformation("Создан код сопряжения, действует {Seconds} с", OfferLifetimeSeconds);
        OfferCreated?.Invoke(offer);
        return offer;
    }

    public void ClearOffer()
    {
        lock (_gate) _offer = null;
    }

    /// <summary>Завершает сопряжение: проверяет токен, сохраняет телефон, выводит ключи.</summary>
    public PairResult Claim(PairClaim claim)
    {
        var offer = CurrentOffer;

        if (offer is null)
            return Fail("Код сопряжения не запрошен или уже истёк. Откройте QR-код на компьютере заново.");

        if (!FixedTimeEquals(offer.Token, claim.Token))
            return Fail("Неверный код сопряжения.");

        if (!B64Url.TryDecode(claim.PhonePub, out var phonePub) || phonePub.Length != KeyPairX25519.KeySize)
            return Fail("Некорректный публичный ключ телефона.");

        var pairId = string.IsNullOrWhiteSpace(claim.PairId) ? Guid.NewGuid().ToString("D") : claim.PairId!;
        var phoneId = string.IsNullOrWhiteSpace(claim.PhoneId) ? Guid.NewGuid().ToString("D") : claim.PhoneId;

        string fingerprint;
        try
        {
            var shared = _store.Keys.Agree(phonePub);
            var keys = SessionKeys.Derive(shared, pairId, PairRole.Pc);
            _keyCache[phoneId] = keys;
            fingerprint = Fingerprint.ForPair(_store.Keys.PublicKey, phonePub);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Не удалось согласовать ключи с телефоном");
            return Fail("Не удалось согласовать ключи шифрования.");
        }

        var phones = _store.LoadPhones();
        phones.RemoveAll(p => p.PhoneId == phoneId);
        var phone = new PairedPhone
        {
            PairId = pairId,
            PhoneId = phoneId,
            PhoneName = string.IsNullOrWhiteSpace(claim.PhoneName) ? "Телефон" : claim.PhoneName,
            PhonePub = claim.PhonePub,
            Fingerprint = fingerprint,
            PairedAt = TimeUtil.IsoNow()
        };
        phones.Add(phone);
        _store.SavePhones(phones);

        // Токен одноразовый.
        ClearOffer();

        _store.AppendEvent("pair.ok", $"Телефон «{phone.PhoneName}» сопряжён");
        _log.LogInformation("Телефон {Name} ({Id}) сопряжён, отпечаток {Fp}", phone.PhoneName, phoneId, fingerprint);
        PhonePaired?.Invoke(phone);

        var adapter = NetworkProbe.GetPrimaryAdapter();
        return new PairResult
        {
            Ok = true,
            PairId = pairId,
            PcId = _store.Identity.DeviceId,
            PcName = _store.Identity.PcName,
            PcPub = _store.Identity.PublicKey,
            Mac = adapter?.Mac,
            LanIps = NetworkProbe.LocalIps(),
            Relay = string.IsNullOrWhiteSpace(_store.Settings.RelayUrl) ? null : _store.Settings.RelayUrl,
            Fingerprint = fingerprint
        };
    }

    public List<PairedPhone> Phones => _store.LoadPhones().Where(p => !p.Revoked).ToList();

    public bool Revoke(string phoneId)
    {
        var phones = _store.LoadPhones();
        var phone = phones.FirstOrDefault(p => p.PhoneId == phoneId);
        if (phone is null) return false;

        phones.Remove(phone);
        _store.SavePhones(phones);
        _keyCache.TryRemove(phoneId, out _);
        _store.AppendEvent("pair.revoke", $"Доступ телефона «{phone.PhoneName}» отозван");
        _log.LogInformation("Отозван доступ телефона {Name} ({Id})", phone.PhoneName, phoneId);
        return true;
    }

    /// <summary>Возвращает ключи шифрования для сопряжённого телефона (выводит их при первом обращении).</summary>
    public bool TryGetKeys(string phoneId, out SessionKeys keys)
    {
        if (_keyCache.TryGetValue(phoneId, out var cached))
        {
            keys = cached;
            return true;
        }

        var phone = _store.LoadPhones().FirstOrDefault(p => p.PhoneId == phoneId && !p.Revoked);
        if (phone is null || !B64Url.TryDecode(phone.PhonePub, out var pub))
        {
            keys = null!;
            return false;
        }

        try
        {
            var shared = _store.Keys.Agree(pub);
            keys = SessionKeys.Derive(shared, phone.PairId, PairRole.Pc);
            _keyCache[phoneId] = keys;
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Не удалось восстановить ключи для телефона {Id}", phoneId);
            keys = null!;
            return false;
        }
    }

    public void TouchPhone(string phoneId)
    {
        var phones = _store.LoadPhones();
        var phone = phones.FirstOrDefault(p => p.PhoneId == phoneId);
        if (phone is null) return;
        phone.LastSeenAt = TimeUtil.IsoNow();
        _store.SavePhones(phones);
    }

    private static PairResult Fail(string error) => new() { Ok = false, Error = error };

    private static bool FixedTimeEquals(string a, string b)
    {
        var x = System.Text.Encoding.UTF8.GetBytes(a ?? "");
        var y = System.Text.Encoding.UTF8.GetBytes(b ?? "");
        return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
    }
}
