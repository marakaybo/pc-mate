using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using PcMate.Core.Util;

namespace PcMate.Core.Ipc;

/// <summary>
/// Двунаправленный канал запрос/ответ поверх потока (именованный канал Windows).
/// Кадрирование — JSON, разделённый переводом строки. Используется и службой, и помощником.
/// </summary>
public sealed class PipeLink : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly Func<IpcMessage, Task<object?>> _handler;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcMessage>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    public event Action<Exception?>? Closed;

    public PipeLink(Stream stream, Func<IpcMessage, Task<object?>> handler)
    {
        _stream = stream;
        _handler = handler;
    }

    public bool IsConnected { get; private set; } = true;

    public async Task RunAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        Exception? failure = null;
        try
        {
            var reader = new StreamReader(_stream, new UTF8Encoding(false), false, 16 * 1024, leaveOpen: true);
            while (!linked.Token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(linked.Token).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;

                if (!PcMateJson.TryDeserialize<IpcMessage>(line, out var msg) || msg is null) continue;
                _ = DispatchAsync(msg);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { failure = ex; }
        finally
        {
            IsConnected = false;
            foreach (var kv in _pending)
                kv.Value.TrySetException(new IOException("Канал закрыт."));
            _pending.Clear();
            Closed?.Invoke(failure);
        }
    }

    private async Task DispatchAsync(IpcMessage msg)
    {
        if (!string.IsNullOrEmpty(msg.Re))
        {
            if (_pending.TryRemove(msg.Re!, out var tcs)) tcs.TrySetResult(msg);
            return;
        }

        IpcMessage reply;
        try
        {
            var result = await _handler(msg).ConfigureAwait(false);
            reply = new IpcMessage
            {
                Re = msg.Id,
                Kind = msg.Kind,
                Ok = true,
                Data = result is null ? null : JsonSerializer.SerializeToNode(result, PcMateJson.Options)
            };
        }
        catch (Exception ex)
        {
            reply = new IpcMessage { Re = msg.Id, Kind = msg.Kind, Ok = false, Error = ex.Message };
        }

        try { await WriteAsync(reply).ConfigureAwait(false); }
        catch (Exception) { /* соединение уже закрыто */ }
    }

    public async Task<IpcMessage> RequestAsync(string kind, object? data = null, TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var msg = new IpcMessage
        {
            Kind = kind,
            Data = data is null ? null : JsonSerializer.SerializeToNode(data, PcMateJson.Options)
        };

        var tcs = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[msg.Id] = tcs;

        try
        {
            await WriteAsync(msg, ct).ConfigureAwait(false);
            using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await using var reg = linked.Token.Register(() => tcs.TrySetCanceled(linked.Token)).ConfigureAwait(false);
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(msg.Id, out _);
        }
    }

    public async Task<T?> RequestAsync<T>(string kind, object? data = null, TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var reply = await RequestAsync(kind, data, timeout, ct).ConfigureAwait(false);
        if (!reply.Ok) throw new InvalidOperationException(reply.Error ?? "Ошибка на другой стороне канала.");
        return reply.DataAs<T>();
    }

    public async Task NotifyAsync(string kind, object? data = null, CancellationToken ct = default)
    {
        var msg = new IpcMessage
        {
            Kind = kind,
            Data = data is null ? null : JsonSerializer.SerializeToNode(data, PcMateJson.Options)
        };
        await WriteAsync(msg, ct).ConfigureAwait(false);
    }

    private async Task WriteAsync(IpcMessage msg, CancellationToken ct = default)
    {
        var line = PcMateJson.Serialize(msg) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        _writeLock.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
