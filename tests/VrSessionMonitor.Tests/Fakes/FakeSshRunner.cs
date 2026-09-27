using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VrSessionMonitor.Modules.Frame;

namespace VrSessionMonitor.Tests.Fakes;

public sealed class FakeSshRunner : ISshRunner
{
    /// <summary>Response chosen by the first key that the command contains; default = exit 0, empty.</summary>
    public readonly Dictionary<string, SshResult> Responses = new();
    public readonly List<(string Host, string Command)> Calls = new();
    /// <summary>Optional: awaited inside RunAsync, lets a test hold a call "in flight".</summary>
    public Func<Task>? Gate;

    public async Task<SshResult> RunAsync(string host, string command, TimeSpan timeout)
    {
        Calls.Add((host, command));
        if (Gate is not null) await Gate();
        return Responses.FirstOrDefault(kv => command.Contains(kv.Key)).Value ?? new SshResult(0, "", "", false);
    }
}
