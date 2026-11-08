using VrSessionMonitor.Modules.Suspend;
using Xunit;

namespace VrSessionMonitor.Tests;

public class FreezeTableFormatTests
{
    private const long Mb = 1024L * 1024;

    private static FreezeProcessRow Row(int pid, string title, FreezeState state, long mb = 100) =>
        new("Unity", pid, "Unity", title, mb * Mb, state);

    [Theory]
    [InlineData(0, "0 MB")]
    [InlineData(640, "640 MB")]
    [InlineData(1023, "1023 MB")]
    [InlineData(1280, "1.3 GB")]
    [InlineData(20480, "20.0 GB")]
    public void Memory_reads_in_MB_below_a_gigabyte_and_GB_above(long mb, string expected) =>
        Assert.Equal(expected, FreezeTableFormat.Memory(mb * Mb));

    [Fact]
    public void Process_labels_show_only_the_differing_part_of_the_window_titles()
    {
        var labels = FreezeTableFormat.ProcessLabels(new[]
        {
            Row(1, "ProjectA - Main - Unity 2022.3.22f1 <DX11>", FreezeState.Running),
            Row(2, "ProjectB - Lobby - Unity 2022.3.22f1 <DX11>", FreezeState.Running),
            Row(3, "", FreezeState.Running),
        });

        Assert.Equal(new[] { "ProjectA - Main", "ProjectB - Lobby", "Unity.exe" }, labels);
    }

    [Fact]
    public void App_state_summarises_its_processes()
    {
        Assert.Equal("running", FreezeTableFormat.AppState(new[] { Row(1, "", FreezeState.Running) }));
        Assert.Equal("frozen", FreezeTableFormat.AppState(new[] { Row(1, "", FreezeState.Frozen), Row(2, "", FreezeState.Frozen) }));
        Assert.Equal("1 of 3 frozen", FreezeTableFormat.AppState(new[]
            { Row(1, "", FreezeState.Frozen), Row(2, "", FreezeState.Running), Row(3, "", FreezeState.KeptRunning) }));
        Assert.Equal("kept running", FreezeTableFormat.AppState(new[] { Row(1, "", FreezeState.KeptRunning) }));
    }

    [Fact]
    public void Process_state_reads_as_plain_words()
    {
        Assert.Equal("running", FreezeTableFormat.State(FreezeState.Running));
        Assert.Equal("frozen", FreezeTableFormat.State(FreezeState.Frozen));
        Assert.Equal("kept running", FreezeTableFormat.State(FreezeState.KeptRunning));
    }
}
