using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules.HomeAssistant;

#if INCLUDE_HOME_ASSISTANT
/// <summary>
/// Persistent authenticated WebSocket connection to Home Assistant's /api/websocket API.
/// Reconnects with exponential backoff on any failure (matching this codebase's existing
/// resilience style — see SteamVrMonitor's stuck-session recovery). Requests are correlated to
/// responses by HA's own incrementing "id" field; "result" messages resolve a pending
/// TaskCompletionSource, "event" messages are dispatched to whichever subscription registered
/// that event's id. See docs/superpowers/specs/2026-07-26-home-assistant-lights-design.md.
/// </summary>
public sealed class HomeAssistantClient : IDisposable
{
    private readonly MonitorConfig _config;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private ClientWebSocket? _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private readonly ConcurrentDictionary<int, Action<JsonElement>> _eventSubscriptions = new();
    private int _nextId;

    public bool IsConnected { get; private set; }

    public HomeAssistantClient(MonitorConfig config)
    {
        _config = config;
    }

    public void Start()
    {
        if (!_config.HomeAssistant.Enabled)
        {
            Log.Info("HomeAssistant", "Disabled in config — not connecting.");
            return;
        }

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => ConnectionLoopAsync(_cts.Token));
        Log.Info("HomeAssistant", $"Started connection loop for {_config.HomeAssistant.BaseUrl}.");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loopTask?.Wait(2000); } catch { /* ignore */ }
    }

    private async Task ConnectionLoopAsync(CancellationToken token)
    {
        var backoffMs = 2000;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await ConnectAndRunAsync(token).ConfigureAwait(false);
                backoffMs = 2000; // reset after a clean connected session
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn("HomeAssistant", $"Connection loop error: {ex.Message}");
            }

            SetConnected(false);
            _pendingRequests.Clear();
            _eventSubscriptions.Clear();
            if (token.IsCancellationRequested) break;

            Log.Info("HomeAssistant", $"Reconnecting in {backoffMs}ms...");
            try { await Task.Delay(backoffMs, token).ConfigureAwait(false); } catch (TaskCanceledException) { break; }
            backoffMs = Math.Min(backoffMs * 2, 30000);
        }
    }

    private async Task ConnectAndRunAsync(CancellationToken token)
    {
        var httpUrl = _config.HomeAssistant.BaseUrl.TrimEnd('/');
        var wsUrl = (httpUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? "wss://" + httpUrl["https://".Length..]
            : "ws://" + httpUrl.Replace("http://", "")) + "/api/websocket";

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(wsUrl), token).ConfigureAwait(false);
        _socket = socket;

        // Auth handshake: HA sends auth_required first, we reply with our token, HA replies auth_ok/auth_invalid.
        await ReceiveRawAsync(socket, token).ConfigureAwait(false); // auth_required — content not needed
        await SendRawAsync(socket, JsonSerializer.Serialize(new { type = "auth", access_token = _config.HomeAssistant.AccessToken }), token).ConfigureAwait(false);
        var authResult = await ReceiveRawAsync(socket, token).ConfigureAwait(false);
        if (authResult.GetProperty("type").GetString() != "auth_ok")
        {
            var message = authResult.TryGetProperty("message", out var m) ? m.GetString() : "unknown error";
            Log.Error("HomeAssistant", $"Authentication failed: {message}. Regenerate a long-lived access token in your Home Assistant user profile and update HomeAssistant.AccessToken.");
            throw new InvalidOperationException("Home Assistant authentication failed");
        }

        Log.Info("HomeAssistant", "Connected and authenticated.");
        SetConnected(true);

        while (!token.IsCancellationRequested)
        {
            var msg = await ReceiveRawAsync(socket, token).ConfigureAwait(false);
            HandleIncoming(msg);
        }
    }

    private void HandleIncoming(JsonElement msg)
    {
        var type = msg.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "result":
                if (msg.TryGetProperty("id", out var idEl) && _pendingRequests.TryRemove(idEl.GetInt32(), out var tcs))
                    tcs.TrySetResult(msg);
                break;
            case "event":
                if (msg.TryGetProperty("id", out var eventIdEl) && _eventSubscriptions.TryGetValue(eventIdEl.GetInt32(), out var handler) &&
                    msg.TryGetProperty("event", out var ev))
                    handler(ev);
                break;
        }
    }

    public async Task<bool> CallServiceAsync(string domain, string service, string entityId, object? serviceData = null, CancellationToken token = default)
    {
        try
        {
            var id = Interlocked.Increment(ref _nextId);
            object command = serviceData is null
                ? new { id, type = "call_service", domain, service, target = new { entity_id = entityId } }
                : new { id, type = "call_service", domain, service, service_data = serviceData, target = new { entity_id = entityId } };
            var result = await SendCommandAsync(id, command, token).ConfigureAwait(false);
            var success = result.TryGetProperty("success", out var s) && s.GetBoolean();
            if (!success)
                Log.Warn("HomeAssistant", $"call_service {domain}.{service} for {entityId} reported failure: {result}");
            return success;
        }
        catch (Exception ex)
        {
            Log.Warn("HomeAssistant", $"call_service {domain}.{service} for {entityId} failed: {ex.Message}");
            return false;
        }
    }

    public async Task SubscribeStateChangedAsync(Action<string, string?> onStateChanged, CancellationToken token = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        _eventSubscriptions[id] = ev =>
        {
            try
            {
                var data = ev.GetProperty("data");
                var entityId = data.GetProperty("entity_id").GetString();
                string? newState = null;
                if (data.TryGetProperty("new_state", out var ns) && ns.ValueKind != JsonValueKind.Null && ns.TryGetProperty("state", out var st))
                    newState = st.GetString();
                if (entityId is not null) onStateChanged(entityId, newState);
            }
            catch (Exception ex)
            {
                Log.Debug("HomeAssistant", $"Failed to parse state_changed event: {ex.Message}");
            }
        };

        var command = new { id, type = "subscribe_events", event_type = "state_changed" };
        await SendCommandAsync(id, command, token).ConfigureAwait(false);
        Log.Info("HomeAssistant", "Subscribed to state_changed events.");
    }

    public async Task<JsonElement> SendRegistryCommandAsync(string commandType, CancellationToken token = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var command = new { id, type = commandType };
        var result = await SendCommandAsync(id, command, token).ConfigureAwait(false);
        return result.GetProperty("result");
    }

    private async Task<JsonElement> SendCommandAsync(int id, object command, CancellationToken token)
    {
        if (_socket is not { State: WebSocketState.Open } socket)
            throw new InvalidOperationException("Not connected to Home Assistant.");

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = tcs;
        try
        {
            await _sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await SendRawAsync(socket, JsonSerializer.Serialize(command), token).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
            await using var registration = linked.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pendingRequests.TryRemove(id, out _);
        }
    }

    private static async Task SendRawAsync(ClientWebSocket socket, string json, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token).ConfigureAwait(false);
    }

    private static async Task<JsonElement> ReceiveRawAsync(ClientWebSocket socket, CancellationToken token)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new IOException("Home Assistant closed the WebSocket connection.");
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        stream.Position = 0;
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        return doc.RootElement.Clone(); // Clone survives the JsonDocument's own disposal below.
    }

    private void SetConnected(bool connected)
    {
        if (IsConnected == connected) return;
        IsConnected = connected;
        Log.Info("HomeAssistant", connected ? "Connection established." : "Connection lost.");
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _socket?.Dispose();
        _sendLock.Dispose();
    }
}
#endif
