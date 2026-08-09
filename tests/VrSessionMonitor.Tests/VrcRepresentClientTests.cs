using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VrSessionMonitor.Modules;
using Xunit;

namespace VrSessionMonitor.Tests;

public class VrcRepresentClientTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpMethod? LastMethod;
        public string? LastUrl;
        public string? LastBody;
        public HttpStatusCode Status = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastMethod = request.Method;
            LastUrl = request.RequestUri?.ToString();
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(Status);
        }
    }

    // A provider whose DB path points nowhere, so HasSession is false but the client still constructs.
    private static VrcxSessionProvider NoSession() => new(dbPathOverride: "Z:/nonexistent/vrcx.sqlite3");

    [Fact]
    public async Task SetRepresented_puts_to_representation_endpoint_with_body()
    {
        var handler = new RecordingHandler();
        using var client = new VrcRepresentClient(NoSession(), handler);

        var ok = await client.SetRepresentedAsync("grp_eden", true);

        Assert.True(ok);
        Assert.Equal(HttpMethod.Put, handler.LastMethod);
        Assert.Contains("groups/grp_eden/representation", handler.LastUrl);
        Assert.Contains("\"isRepresenting\":true", handler.LastBody!.Replace(" ", ""));
    }

    [Fact]
    public async Task SetRepresented_non_2xx_returns_false()
    {
        var handler = new RecordingHandler { Status = HttpStatusCode.Unauthorized };
        using var client = new VrcRepresentClient(NoSession(), handler);
        Assert.False(await client.SetRepresentedAsync("grp_eden", true));
    }
}
