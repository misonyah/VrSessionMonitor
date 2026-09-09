using System.Diagnostics;
using System.Text.Json;
using VrSessionMonitor.Config;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules.Battery;

/// <summary>
/// Samples tracked-device battery levels through a session and, once it ends, reports anything
/// worth charging.
///
/// Sampling during the session rather than at the end is the whole trick: once SteamVR stops, the
/// devices are gone and their levels are unreadable — the moment you want the answer is exactly
/// the moment you can no longer ask. So the last reading taken while the session was alive is
/// what gets reported. (Idea from Tomaae, who built the same thing for his own profile tool.)
///
/// The OpenVR read runs in a CHILD PROCESS. That interop crashed this whole application with an
/// unhandled access violation on 2026-07-29 — the reason HmdActivityMonitor is still disabled —
/// and a native AV cannot be caught, so the only real protection is for it to land somewhere
/// harmless. A crashed or silent child is treated as "no reading this time", which costs one
/// sample rather than the session.
/// </summary>
public sealed class BatteryReminderService : IDisposable
{
    private readonly MonitorConfig _config;
    private readonly Func<IReadOnlyList<DeviceBattery>> _sample;
    private readonly Dictionary<string, DeviceBattery> _lastKnown = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private System.Threading.Timer? _timer;

    /// <summary>Raised when a session ends with devices worth charging. The UI subscribes; nothing
    /// is shown from here, so this class stays testable.</summary>
    public event Action<IReadOnlyList<DeviceBattery>>? ChargeReminder;

    public BatteryReminderService(MonitorConfig config, Func<IReadOnlyList<DeviceBattery>>? sample = null)
    {
        _config = config;
        _sample = sample ?? DefaultSample;
    }

    public void HandleSteamVrRunningChanged(bool isRunning)
    {
        if (isRunning) Start();
        else Stop();
    }

    private void Start()
    {
        if (!_config.BatteryReminder.Enabled) return;

        lock (_lock)
        {
            _lastKnown.Clear(); // readings from a previous session say nothing about this one
            _timer?.Dispose();
            var interval = Math.Max(1, _config.BatteryReminder.SampleIntervalMinutes) * 60_000;
            // Samples immediately as well as on the interval: a short session would otherwise end
            // with nothing recorded at all.
            _timer = new System.Threading.Timer(_ => SafeSample(), null, 5_000, interval);
        }

        Log.Info("Battery", $"Sampling device batteries every {_config.BatteryReminder.SampleIntervalMinutes} minute(s) this session.");
    }

    private void Stop()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }

        var low = LowDevices();
        if (low.Count == 0) return;

        Log.Info("Battery", $"Session ended with {low.Count} device(s) worth charging: {string.Join(", ", low.Select(d => $"{d.Assignment} {d.Percent:0}%"))}.");
        ChargeReminder?.Invoke(low);
    }

    /// <summary>Test seam: take one sample now, without waiting for the timer.</summary>
    internal void SampleNowForTests() => SafeSample();

    private void SafeSample()
    {
        try
        {
            var readings = _sample();
            if (readings.Count == 0) return; // SteamVR gone, or the child failed — keep what we had

            lock (_lock)
                foreach (var r in readings)
                    if (!string.IsNullOrWhiteSpace(r.Serial))
                        _lastKnown[r.Serial] = r;
        }
        catch (Exception ex)
        {
            Log.Debug("Battery", $"Battery sample failed: {ex.Message}");
        }
    }

    /// <summary>Devices below the threshold and not on charge. A device that is charging needs no
    /// reminder however low it is — that is the state being asked for.</summary>
    public IReadOnlyList<DeviceBattery> LowDevices()
    {
        lock (_lock)
            return _lastKnown.Values
                .Where(d => !d.Charging && d.Percent <= _config.BatteryReminder.WarnBelowPercent)
                .OrderBy(d => d.Percent)
                .ToList();
    }

    /// <summary>Every device seen this session, for the status view.</summary>
    public IReadOnlyList<DeviceBattery> LastKnown()
    {
        lock (_lock) return _lastKnown.Values.OrderBy(d => d.Percent).ToList();
    }

    /// <summary>
    /// Spawns this same executable with --dump-battery and parses its stdout.
    ///
    /// Same binary rather than a separate helper project: it keeps the interop beside the code
    /// that uses it and cannot drift out of sync, while still getting a process boundary between
    /// an access violation and the tray application.
    /// </summary>
    private IReadOnlyList<DeviceBattery> DefaultSample()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return Array.Empty<DeviceBattery>();

            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--dump-battery \"{_config.Paths.OpenVrApiDllPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return Array.Empty<DeviceBattery>();

            var json = proc.StandardOutput.ReadToEnd();

            // Bounded wait: a hung child must not stall sampling forever, and killing it costs
            // only this one reading.
            if (!proc.WaitForExit(15_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                Log.Debug("Battery", "Battery probe did not finish in time; skipping this sample.");
                return Array.Empty<DeviceBattery>();
            }

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
                return Array.Empty<DeviceBattery>();

            return JsonSerializer.Deserialize<List<DeviceBattery>>(json) ?? new List<DeviceBattery>();
        }
        catch (Exception ex)
        {
            Log.Debug("Battery", $"Could not run the battery probe: {ex.Message}");
            return Array.Empty<DeviceBattery>();
        }
    }

    public void Dispose() => Stop();
}
