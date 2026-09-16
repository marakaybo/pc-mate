using Microsoft.Extensions.Logging;
using PcMate.Core.Protocol;

namespace PcMate.Agent.Service.Services;

/// <summary>
/// Шина событий агента. Источники (питание, сценарии, мастер) публикуют сюда,
/// транспорты (сервер-ретранслятор, локальный сервер) — подписываются.
/// </summary>
public sealed class AgentEventBus
{
    private readonly ILogger<AgentEventBus> _log;
    private readonly List<Func<EvtPayload, CancellationToken, Task>> _subscribers = new();
    private readonly object _gate = new();

    public AgentEventBus(ILogger<AgentEventBus> log) => _log = log;

    public IDisposable Subscribe(Func<EvtPayload, CancellationToken, Task> handler)
    {
        lock (_gate) _subscribers.Add(handler);
        return new Subscription(this, handler);
    }

    public void Publish(string evt, object? data = null) => _ = PublishAsync(evt, data);

    public async Task PublishAsync(string evt, object? data = null, CancellationToken ct = default)
    {
        var payload = EvtPayload.Of(evt, data);
        Func<EvtPayload, CancellationToken, Task>[] handlers;
        lock (_gate) handlers = _subscribers.ToArray();

        foreach (var handler in handlers)
        {
            try
            {
                await handler(payload, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Подписчик шины событий отклонил {Evt}", evt);
            }
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly AgentEventBus _bus;
        private readonly Func<EvtPayload, CancellationToken, Task> _handler;

        public Subscription(AgentEventBus bus, Func<EvtPayload, CancellationToken, Task> handler)
        {
            _bus = bus;
            _handler = handler;
        }

        public void Dispose()
        {
            lock (_bus._gate) _bus._subscribers.Remove(_handler);
        }
    }
}
