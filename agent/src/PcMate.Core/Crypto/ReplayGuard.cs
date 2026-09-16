using System.Collections.Concurrent;

namespace PcMate.Core.Crypto;

/// <summary>
/// Скользящее окно nonce'ов: команда не может быть повторена, даже если перехвачена целиком.
/// Дополнительно проверяется расхождение часов (по умолчанию ±120 с).
/// </summary>
public sealed class ReplayGuard
{
    private readonly ConcurrentDictionary<string, long> _seen = new(StringComparer.Ordinal);
    private readonly TimeSpan _window;
    private readonly TimeSpan _clockSkew;
    private readonly Func<DateTimeOffset> _now;
    private long _lastSweepTicks;

    public ReplayGuard(TimeSpan? window = null, TimeSpan? clockSkew = null, Func<DateTimeOffset>? now = null)
    {
        _window = window ?? TimeSpan.FromSeconds(600);
        _clockSkew = clockSkew ?? TimeSpan.FromSeconds(120);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public enum Verdict
    {
        Accepted,
        StaleTimestamp,
        DuplicateNonce,
        MissingNonce
    }

    public Verdict Check(string? nonce, long unixTs)
    {
        if (string.IsNullOrWhiteSpace(nonce)) return Verdict.MissingNonce;

        var now = _now();
        var delta = Math.Abs(now.ToUnixTimeSeconds() - unixTs);
        if (delta > (long)_clockSkew.TotalSeconds) return Verdict.StaleTimestamp;

        Sweep(now);

        var expiresAt = now.Add(_window).ToUnixTimeSeconds();
        return _seen.TryAdd(nonce, expiresAt) ? Verdict.Accepted : Verdict.DuplicateNonce;
    }

    public int TrackedCount => _seen.Count;

    private void Sweep(DateTimeOffset now)
    {
        // Чистим не чаще раза в 30 секунд, чтобы не нагружать горячий путь.
        var nowTicks = now.UtcTicks;
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (nowTicks - last < TimeSpan.TicksPerSecond * 30) return;
        if (Interlocked.CompareExchange(ref _lastSweepTicks, nowTicks, last) != last) return;

        var cutoff = now.ToUnixTimeSeconds();
        foreach (var kv in _seen)
            if (kv.Value <= cutoff)
                _seen.TryRemove(kv.Key, out _);
    }
}
