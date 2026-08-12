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

    private sealed class JsonHandler : HttpMessageHandler
    {
        public Func<string, (HttpStatusCode, string)> Respond = _ => (HttpStatusCode.OK, "");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (status, body) = Respond(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task GetMyGroups_parses_id_name_and_code_and_skips_entries_without_a_grp_id()
    {
        var handler = new JsonHandler
        {
            Respond = url =>
                url.Contains("auth/user") ? (HttpStatusCode.OK, "{\"id\":\"usr_me\"}")
                : url.Contains("users/usr_me/groups") ? (HttpStatusCode.OK,
                    "[{\"id\":\"grp_a\",\"name\":\"Alpha\",\"shortCode\":\"ALPH\"}," +
                     "{\"groupId\":\"grp_b\",\"name\":\"Beta\",\"shortCode\":\"BETA\"}," +
                     "{\"name\":\"NoId\"}]")
                : (HttpStatusCode.NotFound, ""),
        };
        using var client = new VrcRepresentClient(NoSession(), handler);

        var groups = await client.GetMyGroupsAsync();

        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, g => g.Id == "grp_a" && g.Name == "Alpha" && g.ShortCode == "ALPH");
        Assert.Contains(groups, g => g.Id == "grp_b" && g.Name == "Beta" && g.ShortCode == "BETA");
    }
}
