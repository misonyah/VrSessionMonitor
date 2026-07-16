using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

/// <summary>
/// Wire format tracker firmware is expected to send (fire-and-forget, no ack) when it
/// autonomously self-heals, e.g. an I2C bus reset on the extension sensor:
/// {"mac":"AA:BB:CC:DD:EE:FF","event":"auto_reset","success":true,"detail":"extension IMU unresponsive"}
///
/// This is a design placeholder: SlimeVR_improved does not send this yet (separate firmware
/// task). The listener is safe to run regardless — it just won't receive anything until that
/// firmware work lands.
/// </summary>
public sealed class FirmwareNotification
{
    public string? Mac { get; set; }
    public string? Event { get; set; }
    public bool Success { get; set; }
    public string? Detail { get; set; }
}

public sealed class FirmwareNotificationListener : IDisposable
{
    private readonly MonitorConfig _config;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public event EventHandler<FirmwareNotification>? NotificationReceived;

    /// <summary>Short human-readable summary of the most recent notification (e.g. "right_foot
    /// self-healed"), for tray display. Null until the first notification ever arrives.</summary>
    public string? LastEventSummary { get; private set; }
    public DateTime? LastEventAtUtc { get; private set; }

    public FirmwareNotificationListener(MonitorConfig config)
    {
        _config = config;
    }

    public void Start()
    {
        var port = _config.Network.FirmwareNotifyUdpPort;
        try
        {
            _udp = new UdpClient(port);
        }
        catch (Exception ex)
        {
            Log.Error("FirmwareNotify", $"Failed to bind UDP port {port} — OOB firmware notifications will not be received.", ex);
            return;
        }

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        Log.Info("FirmwareNotify", $"Listening for OOB firmware self-heal notifications on UDP:{port}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _udp?.Close();
        try { _loopTask?.Wait(2000); } catch { /* ignore */ }
        Log.Info("FirmwareNotify", "Stopped.");
    }

    private async Task LoopAsync(CancellationToken token)
    {
        if (_udp is null) return;

        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Log.Debug("FirmwareNotify", $"Receive error: {ex.Message}");
                continue;
            }

            var raw = Encoding.UTF8.GetString(result.Buffer);
            Log.Trace("FirmwareNotify", $"Raw packet from {result.RemoteEndPoint}: {raw}");

            try
            {
                var note = JsonSerializer.Deserialize<FirmwareNotification>(raw, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });

                if (note is null)
                {
                    Log.Warn("FirmwareNotify", $"Could not parse packet from {result.RemoteEndPoint}: {raw}");
                    continue;
                }

                var trackerName = _config.Trackers.FirstOrDefault(t =>
                    string.Equals(t.Mac, note.Mac, StringComparison.OrdinalIgnoreCase))?.Name ?? note.Mac ?? "unknown";

                LastEventAtUtc = DateTime.UtcNow;
                LastEventSummary = $"{trackerName} {(note.Success ? "self-healed" : "FAILED")}";

                if (note.Success)
                {
                    Log.Info("FirmwareNotify", $"Tracker '{trackerName}' self-healed: {note.Event} — {note.Detail}");
                    SteamVrNotifier.TryNotify(_config, $"Tracker '{trackerName}' self-healed");
                }
                else
                {
                    Log.Warn("FirmwareNotify", $"Tracker '{trackerName}' self-heal FAILED: {note.Event} — {note.Detail}. Manual power-cycle likely needed.");
                    SteamVrNotifier.TryNotify(_config, $"Tracker '{trackerName}' needs a power-cycle");
                }

                NotificationReceived?.Invoke(this, note);
            }
            catch (JsonException ex)
            {
                Log.Warn("FirmwareNotify", $"Malformed JSON from {result.RemoteEndPoint}: {ex.Message} :: raw={raw}");
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _udp?.Dispose();
    }
}
