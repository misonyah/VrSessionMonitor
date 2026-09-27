using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System;
using VrSessionMonitor.Modules.Frame;
using Xunit;

namespace VrSessionMonitor.Tests;

public class HotspotKeeperClientTests
{
    [Fact]
    public void LastLine_skips_trailing_blank_lines()
        => Assert.Equal("b", HotspotKeeperClient.LastLine("a\r\nb\r\n\r\n"));

    [Theory]
    [InlineData("2026-09-27 20:10:00 OK - HomeHotspot is up on the target adapter, 5 GHz", true)]
    [InlineData("2026-09-27 20:04:25 adapter still lacks Wi-Fi Direct support after reset", false)]
    [InlineData("", false)]
    public void IsOk_recognises_the_keeper_ok_line(string line, bool ok)
        => Assert.Equal(ok, HotspotKeeperClient.IsOk(line));

    [Fact]
    public void Missing_log_reports_missing()
        => Assert.Equal("keeper log missing", new HotspotKeeperClient().ReadStatus(Path.Combine(Path.GetTempPath(), "nope-" + System.Guid.NewGuid() + ".log")));

    [Fact]
    public void ReadStatus_strips_timestamp()
    {
        var p = Path.GetTempFileName();
        try
        {
            File.WriteAllText(p, "2026-09-27 20:10:00 OK - HomeHotspot is up on the target adapter, 5 GHz\r\n");
            Assert.Equal("OK - HomeHotspot is up on the target adapter, 5 GHz", new HotspotKeeperClient().ReadStatus(p));
        }
        finally { File.Delete(p); }
    }

    private sealed class FakeScheduler : IScheduledTaskApi
    {
        public int RunningPollsLeft;
        public int Polls;
        public List<string> Started = new();
        public bool StartOk = true;
        public bool TryRun(string taskName, out string error) { Started.Add(taskName); error = StartOk ? "" : "denied"; return StartOk; }
        public bool IsRunning(string taskName) { Polls++; return RunningPollsLeft-- > 0; }
    }

    [Fact]
    public async Task TriggerAndWait_waits_until_the_task_stops_running()
    {
        var ts = new FakeScheduler { RunningPollsLeft = 3 };
        await new HotspotKeeperClient(ts, pollInterval: TimeSpan.FromMilliseconds(1)).TriggerAndWaitAsync("Keeper", TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "Keeper" }, ts.Started);
        Assert.Equal(4, ts.Polls);                   // 3x running, then stopped
    }

    [Fact]
    public async Task TriggerAndWait_does_not_poll_when_start_fails()
    {
        var ts = new FakeScheduler { StartOk = false, RunningPollsLeft = 3 };
        await new HotspotKeeperClient(ts, pollInterval: TimeSpan.FromMilliseconds(1)).TriggerAndWaitAsync("Keeper", TimeSpan.FromSeconds(5));
        Assert.Equal(0, ts.Polls);
    }

    [Fact]
    public async Task TriggerAndWait_gives_up_at_timeout()
    {
        var ts = new FakeScheduler { RunningPollsLeft = int.MaxValue };
        await new HotspotKeeperClient(ts, pollInterval: TimeSpan.FromMilliseconds(5)).TriggerAndWaitAsync("Keeper", TimeSpan.FromMilliseconds(60));
        Assert.InRange(ts.Polls, 1, 50);
    }
}
