using System;
using System.Net;
using System.Text;
using System.Text.Json;
using VrSessionMonitor.Modules;
using Xunit;

namespace VrSessionMonitor.Tests;

public class VrcxCookieExtractionTests
{
    // Mirrors how VRCX stores cookies: Base64( JSON-serialized CookieCollection ).
    private static string EncodeLikeVrcx(CookieCollection cc)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(cc);
        return Convert.ToBase64String(json);
    }

    [Fact]
    public void ExtractVrchatCookies_pulls_vrchat_auth_cookie()
    {
        var cc = new CookieCollection
        {
            new Cookie("auth", "authcookie-value", "/", "api.vrchat.cloud"),
            new Cookie("twoFactorAuth", "2fa-value", "/", "api.vrchat.cloud"),
            new Cookie("session", "unrelated", "/", "example.com"),
        };

        var result = VrcxSessionProvider.ExtractVrchatCookies(EncodeLikeVrcx(cc));

        Assert.Contains(result.Cast<Cookie>(), c => c.Name == "auth" && c.Value == "authcookie-value");
        Assert.DoesNotContain(result.Cast<Cookie>(), c => c.Domain.Contains("example.com"));
    }

    [Fact]
    public void ExtractVrchatCookies_garbage_returns_empty()
    {
        Assert.Empty(VrcxSessionProvider.ExtractVrchatCookies("not-base64!!"));
        Assert.Empty(VrcxSessionProvider.ExtractVrchatCookies(Convert.ToBase64String(Encoding.UTF8.GetBytes("{not cookies}"))));
    }
}
