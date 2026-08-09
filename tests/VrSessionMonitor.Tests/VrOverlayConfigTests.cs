using System.IO;
using VrSessionMonitor.Config;
using Xunit;

namespace VrSessionMonitor.Tests;

public class VrOverlayConfigTests
{
    [Fact]
    public void VrOverlay_defaults_to_XSOverlay()
    {
        Assert.Equal(VrOverlayChoice.XSOverlay, new MonitorConfig().SessionFlow.VrOverlay);
    }

    [Fact]
    public void VrOverlay_round_trips_as_a_string_name()
    {
        var path = Path.Combine(Path.GetTempPath(), "vsm_overlay_" + Path.GetRandomFileName() + ".json");
        try
        {
            var cfg = new MonitorConfig();
            cfg.SessionFlow.VrOverlay = VrOverlayChoice.OvrToolkit;
            cfg.Save(path);

            var json = File.ReadAllText(path);
            Assert.Contains("\"OvrToolkit\"", json);       // saved by name, not integer
            Assert.DoesNotContain("\"VrOverlay\": 1", json);

            var reloaded = MonitorConfig.LoadOrCreateDefault(path);
            Assert.Equal(VrOverlayChoice.OvrToolkit, reloaded.SessionFlow.VrOverlay);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
