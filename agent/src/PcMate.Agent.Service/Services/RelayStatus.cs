using PcMate.Core.Util;

namespace PcMate.Agent.Service.Services;

/// <summary>Состояние связи с сервером-ретранслятором — читают мастер готовности и статус.</summary>
public sealed class RelayStatus
{
    public bool Connected { get; private set; }
    public string? RelayUrl { get; private set; }
    public string? LastError { get; private set; }
    public string? LastConnectedAt { get; private set; }
    public int ReconnectAttempts { get; private set; }

    public void MarkConnected(string relayUrl)
    {
        Connected = true;
        RelayUrl = relayUrl;
        LastError = null;
        LastConnectedAt = TimeUtil.IsoNow();
        ReconnectAttempts = 0;
    }

    public void MarkDisconnected(string? error = null)
    {
        Connected = false;
        if (!string.IsNullOrWhiteSpace(error)) LastError = error;
        ReconnectAttempts++;
    }

    public void MarkDisabled()
    {
        Connected = false;
        RelayUrl = null;
        LastError = "Сервер-ретранслятор не настроен (работает только локальный режим).";
    }
}
