using System.Net;
using System.Text.Json;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Desktop.Web;

namespace UniversalMediaDistribution.Desktop.Tests;

public sealed class LinuxWebChallengeResultTests
{
    private static readonly Uri Endpoint = new("https://media.example.org/catalog/list");
    private static readonly DateTime Now = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
    private static LinuxWebChallengeResult Snapshot => new(Endpoint, "WebKit test", new("{}", "application/json"));

    [Fact]
    public void ClearanceIncludesHttpOnlyCookiesButExcludesOtherHostsPathsAndExpiredCookies()
    {
        Cookie[] cookies =
        [
            new("cf_clearance", "allowed", "/", "media.example.org") { HttpOnly = true, Secure = true },
            new("session", "allowed", "/catalog", ".example.org"),
            new("secret", "other-host", "/", "other.example.org"),
            new("secret", "host-only-parent", "/", "example.org"),
            new("secret", "suffix-lookalike", "/", ".ample.org"),
            new("secret", "other-path", "/catalogue", "media.example.org"),
            new("secret", "expired", "/", "media.example.org") { Expires = Now.AddYears(-10) }
        ];
        var result = Snapshot.GetClearance(Endpoint, cookies, Now);
        Assert.NotNull(result);
        Assert.Equal("session=allowed; cf_clearance=allowed", result.CookieHeader);
        Assert.Equal(Endpoint.Host, result.Host);
        Assert.Equal("WebKit test", result.UserAgent);
    }

    [Theory]
    [InlineData("not_cf_clearance", "value")]
    [InlineData("cf_clearance", "")]
    public void RequiresTheExactNonemptyClearanceCookie(string name, string value) =>
        Assert.Null(Snapshot.GetClearance(Endpoint, [new Cookie(name, value, "/", Endpoint.Host)], Now));

    [Fact]
    public void DoesNotSendSecureCookieOverHttp() =>
        Assert.Null(Snapshot.GetClearance(new Uri("http://media.example.org/catalog/list"),
            [new Cookie("cf_clearance", "secret", "/", Endpoint.Host) { Secure = true }], Now));

    [Fact]
    public void CookiePathMustEndAtASegmentBoundary() =>
        Assert.Null(Snapshot.GetClearance(Endpoint,
            [new Cookie("cf_clearance", "secret", "/cat", Endpoint.Host)], Now));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsRawAndJsonEncodedWebKitResults(bool encoded)
    {
        const string report = """{"url":"https://media.example.org/catalog/list","userAgent":"WebKit","document":{"contentType":"application/json","body":"{\"titles\":[]}"}}""";
        var result = LinuxWebChallengeResult.Parse(encoded ? JsonSerializer.Serialize(report) : report, Endpoint);
        Assert.NotNull(result);
        Assert.True(result.HasDocument);
        Assert.Equal("{\"titles\":[]}", result.Document.Body);
    }

    [Theory]
    [InlineData("about:blank")]
    [InlineData("https://other.example.org/catalog/list")]
    [InlineData("http://media.example.org/catalog/list")]
    public void RejectsBlankPagesOtherOriginsAndHttpsDowngrades(string url)
    {
        var report = $$$"""{"url":"{{{url}}}","userAgent":"WebKit","document":{"contentType":"text/plain","body":"content"}}""";
        Assert.Null(LinuxWebChallengeResult.Parse(report, Endpoint));
    }

    [Theory]
    [InlineData("Just a moment", "text/html", false)]
    [InlineData("cf_chl", "text/html", false)]
    [InlineData("short stub", "text/html", false)]
    [InlineData("{\"titles\":[]}", "application/json", true)]
    public void SeparatesChallengePagesFromShortApiResponses(string body, string type, bool expected) =>
        Assert.Equal(expected, new LinuxWebChallengeResult(Endpoint, "WebKit", new(body, type)).HasDocument);

    [Fact]
    public void AcceptsBoundedImageReport()
    {
        const string report = """{"url":"https://media.example.org/cover.png","userAgent":"WebKit","document":{"contentType":"image/png","body":"YWJj","isBase64":true}}""";
        var result = LinuxWebChallengeResult.Parse(report, Endpoint);
        Assert.NotNull(result);
        Assert.True(result.HasDocument);
        Assert.True(result.Document.IsBase64);
    }
}
