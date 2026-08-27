using System.Net;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

#if INCLUDE_OSC
using LucHeart.CoreOSC;
using VRC.OSCQuery;

/// <summary>
/// Receives VRChat avatar parameters over OSC, and hands them to whoever asked for them.
///
/// Advertises its own OSCQuery service so VRChat's discovery finds it and starts streaming. This
/// takes nothing away from other OSC consumers: OSCQuery exists precisely so several apps can
/// subscribe at once, and VRChat fans parameters out to every service it discovers. Ports are
/// ephemeral rather than the conventional 9000/9001, so this can never squat the port another app
/// is listening on — a mistake that has already cost real debugging time on this machine.
///
/// Read-only. Nothing is ever sent back to VRChat.
///
/// Generalised out of the old VrChatOscAfkListener, which lived behind INCLUDE_HOME_ASSISTANT and
/// so made every other OSC consumer conditional on Home Assistant being compiled in. Heart rate
/// has nothing to do with lighting, so the transport now sits on its own.
/// </summary>
public sealed class VrChatOscListener : IDisposable
{
    private const string ServiceNamePrefix = "VrSessionMonitor";

    private readonly MonitorConfig _config;
    private readonly HashSet<string> _addresses;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private OSCQueryService? _oscQuery;
    private OscDuplex? _osc;

    /// <summary>Raised for every subscribed parameter that arrives, with its raw first argument.
    /// Handlers must not throw; one bad handler must not stop the receive loop.</summary>
    public event Action<string, object?>? ParameterReceived;

    /// <param name="parameterNames">Bare VRChat parameter names, without the
    /// /avatar/parameters/ prefix — the same strings a user sees in VRCOSC's settings.</param>
    public VrChatOscListener(MonitorConfig config, IEnumerable<string> parameterNames)
    {
        _config = config;
        _addresses = parameterNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => $"/avatar/parameters/{n.Trim()}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public void Start()
    {
        if (_addresses.Count == 0)
        {
            Log.Info("VrChatOsc", "No OSC parameters are configured — not advertising a service.");
            return;
        }

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunAsync(_cts.Token));
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

            // The "/avatar" root is registered first, matching the reference implementation:
            // VRChat's discovery expects it before treating this as a valid parameter consumer,
            // and without it the parameters may never arrive at all.
            _oscQuery.AddEndpoint("/avatar", "N", Attributes.AccessValues.WriteOnly);
            foreach (var address in _addresses)
                _oscQuery.AddEndpoint(address, "T", Attributes.AccessValues.WriteOnly);

            // The send endpoint is never used — SendAsync is never called — but OscDuplex's
            // constructor demands one, so it points at VRChat's conventional receive port.
            _osc = new OscDuplex(new IPEndPoint(IPAddress.Loopback, udpPort), new IPEndPoint(IPAddress.Loopback, 9000));
            Log.Info("VrChatOsc", $"Advertising OSCQuery service '{serviceName}' (tcp={tcpPort}, udp={udpPort}) for {_addresses.Count} parameter(s): {string.Join(", ", _addresses.Select(a => a[(a.LastIndexOf('/') + 1)..]))}.");

            while (!token.IsCancellationRequested)
            {
                OscMessage received;
                try
                {
                    received = await _osc.ReceiveMessageAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    Log.Debug("VrChatOsc", $"Receive failed: {ex.Message}");
                    continue;
                }

                if (!_addresses.Contains(received.Address)) continue;

                var value = received.Arguments.ElementAtOrDefault(0);
                try
                {
                    ParameterReceived?.Invoke(received.Address, value);
                }
                catch (Exception ex)
                {
                    Log.Debug("VrChatOsc", $"A handler for {received.Address} threw: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            // Dispose cancels the token and tears down the socket specifically to unblock the
            // receive above, so a fault during shutdown is expected rather than notable.
            if (token.IsCancellationRequested)
                Log.Debug("VrChatOsc", $"Listener stopped during shutdown: {ex.Message}");
            else
                Log.Warn("VrChatOsc", $"Listener stopped unexpectedly: {ex.Message}");
        }
    }

    public void Dispose()
    {
        // Order matters: the loop parks in ReceiveMessageAsync(), which takes no cancellation
        // token, so cancelling alone cannot unblock it and the Wait below would burn its full
        // timeout on every exit. Disposing the socket first faults that pending receive.
        _cts?.Cancel();
        _osc?.Dispose();
        _oscQuery?.Dispose();
        try { _loopTask?.Wait(2000); } catch { /* ignore */ }
        _cts?.Dispose();
    }
}
#endif
