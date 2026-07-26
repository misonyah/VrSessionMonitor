using System.Net;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules.HomeAssistant;

#if INCLUDE_HOME_ASSISTANT
using LucHeart.CoreOSC;
using VRC.OSCQuery;

/// <summary>
/// Listens for VRChat's own /avatar/parameters/AFK OSC parameter — true when AFK is toggled via
/// the in-game quick menu, complementary to HmdActivityMonitor's SteamVR-level proximity signal
/// (that one catches "headset physically off"; this one catches "AFK while still wearing it").
/// Advertises its own OSCQuery service so VRChat's own OSCQuery discovery finds it and starts
/// streaming avatar parameter updates to it — pattern verified working in a real shipped app
/// (C:\Users\<user>\git\GiggleTechRouter\Services\RouterEngine.cs). Only ever reads; never sends
/// anything back to VRChat. See docs/superpowers/specs/2026-07-26-home-assistant-lights-design.md.
///
/// Deviation from that spec: it calls for "consecutive-reads-before-flip" debounce on both AFK
/// sources, which makes sense for HmdActivityMonitor's repeated poll (a single noisy read
/// shouldn't flip state) but not here — VRChat only ever sends this message when the value
/// actually changes, so there's no repeated poll to debounce. The `afk == _lastAfk` check below
/// is the equivalent real protection: it's a push signal, so a de-duplicated genuine change is
/// already the correct trigger, not a false positive to filter out.
/// </summary>
public sealed class VrChatOscAfkListener : IDisposable
{
    private const string ServiceNamePrefix = "VrSessionMonitor-HA";

    private readonly MonitorConfig _config;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private OSCQueryService? _oscQuery;
    private OscDuplex? _osc;
    private bool _lastAfk;

    public event EventHandler<bool>? AfkChanged;

    public VrChatOscAfkListener(MonitorConfig config)
    {
        _config = config;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunAsync(_cts.Token));
        Log.Info("VrChatOscAfk", "Started.");
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            var tcpPort = Extensions.GetAvailableTcpPort();
            var udpPort = Extensions.GetAvailableUdpPort();
            var instanceId = Guid.NewGuid().ToString("N")[..6].ToUpper();
            var serviceName = $"{ServiceNamePrefix}-{instanceId}";

            _oscQuery = new OSCQueryService { TcpPort = tcpPort, OscPort = udpPort, ServerName = serviceName };
            _oscQuery.StartHttpServer();
            _oscQuery.AdvertiseOSCQueryService(serviceName, tcpPort);
            _oscQuery.AdvertiseOSCService(serviceName, udpPort);
            _oscQuery.AddEndpoint("/avatar/parameters/AFK", "T", Attributes.AccessValues.WriteOnly);

            // Receive-only: the "send" endpoint is never actually used (SendAsync is never
            // called), it just points at VRChat's conventional default receive port since
            // OscDuplex's constructor requires one.
            _osc = new OscDuplex(new IPEndPoint(IPAddress.Loopback, udpPort), new IPEndPoint(IPAddress.Loopback, 9000));
            Log.Info("VrChatOscAfk", $"Advertising OSCQuery service '{serviceName}' (tcp={tcpPort}, udp={udpPort}) — waiting for VRChat to discover it and start sending avatar parameters.");

            while (!token.IsCancellationRequested)
            {
                OscMessage received;
                try
                {
                    received = await _osc.ReceiveMessageAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    Log.Debug("VrChatOscAfk", $"Receive failed: {ex.Message}");
                    continue;
                }

                if (received.Address != "/avatar/parameters/AFK") continue;

                var afk = received.Arguments.ElementAtOrDefault(0) is true;
                if (afk == _lastAfk) continue;

                _lastAfk = afk;
                Log.Info("VrChatOscAfk", $"AFK -> {afk}");
                AfkChanged?.Invoke(this, afk);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("VrChatOscAfk", $"Listener stopped unexpectedly: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _loopTask?.Wait(2000); } catch { /* ignore */ }
        _cts?.Dispose();
        _osc?.Dispose();
        _oscQuery?.Dispose();
    }
}
#endif
