using System.Net;
using System.Text.Json;
using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.Desktop.Web;

internal sealed record LinuxWebChallengeResult(Uri Location, string UserAgent, WebChallengeDocument Document)
{
    internal static readonly string SnapshotScript = $$"""
        JSON.stringify({ url: location.href, userAgent: navigator.userAgent,
                         document: {{WebChallengeDocument.ReportExpression}} });
        """;

    // WebKit returns strings directly; some adapters JSON-encode the result.
    internal static string? DecodeString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!value.StartsWith('"')) return value;
        try
        {
            using var json = JsonDocument.Parse(value);
            return json.RootElement.ValueKind == JsonValueKind.String ? json.RootElement.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    internal static LinuxWebChallengeResult? Parse(string? value, Uri endpoint)
    {
        if (value is null || value.Length > 12 * 1024 * 1024) return null;
        try
        {
            var decoded = DecodeString(value);
            if (decoded is null) return null;
            using var json = JsonDocument.Parse(decoded);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var location) ||
                !AllowsNavigation(endpoint, location) ||
                !root.TryGetProperty("userAgent", out var agent) || agent.ValueKind != JsonValueKind.String ||
                agent.GetString() is not { Length: > 0 and <= 4096 } userAgent ||
                userAgent.Contains('\r') || userAgent.Contains('\n') ||
                !root.TryGetProperty("document", out var report) ||
                WebChallengeDocument.ParseReport(report.GetRawText()) is not { } document)
                return null;
            return new(location, userAgent, document);
        }
        catch (JsonException) { return null; }
    }

    internal static bool AllowsNavigation(Uri endpoint, Uri target) =>
        target.IsAbsoluteUri && target.Scheme is "http" or "https" &&
        string.Equals(endpoint.IdnHost, target.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        (endpoint.Scheme != "https" || target.Scheme == "https");

    internal bool HasDocument => Document.Body.Length > 0 &&
        (Document.IsBase64 ||
         (!Document.Body.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase) &&
          !Document.Body.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase) &&
          !Document.Body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) &&
          !Document.Body.Contains("cf_chl", StringComparison.OrdinalIgnoreCase))) &&
        (!Document.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase) || Document.Body.Length >= 1000);

    internal WebChallengeClearance? GetClearance(Uri endpoint, IEnumerable<Cookie> cookies, DateTime utcNow)
    {
        // GetCookiesAsync returns the entire jar. Never forward cookies for
        // another host/path, expired cookies, or Secure cookies over HTTP.
        var matching = cookies.Where(cookie => Matches(cookie, endpoint, utcNow))
            .OrderByDescending(cookie => cookie.Path.Length).ToArray();
        if (!matching.Any(cookie => cookie.Name == "cf_clearance" && cookie.Value.Length > 0)) return null;
        var header = string.Join("; ", matching.Select(cookie => $"{cookie.Name}={cookie.Value}"));
        return header.Length <= 16384 ? new(endpoint.IdnHost, header, UserAgent) : null;
    }

    private static bool Matches(Cookie cookie, Uri endpoint, DateTime utcNow)
    {
        var domain = cookie.Domain.TrimStart('.');
        var hostMatches = endpoint.IdnHost.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            (cookie.Domain.StartsWith('.') && endpoint.IdnHost.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
        var path = string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path;
        var requestPath = endpoint.AbsolutePath;
        var pathMatches = requestPath == path ||
            (requestPath.StartsWith(path, StringComparison.Ordinal) &&
             (path.EndsWith('/') || (requestPath.Length > path.Length && requestPath[path.Length] == '/')));
        return domain.Length > 0 && hostMatches && pathMatches && !cookie.Expired &&
            (cookie.Expires == DateTime.MinValue || cookie.Expires.ToUniversalTime() > utcNow) &&
            (!cookie.Secure || endpoint.Scheme == "https") && cookie.Name.Length > 0 &&
            cookie.Name.IndexOfAny([';', '=', '\r', '\n']) < 0 && cookie.Value.IndexOfAny([';', '\r', '\n']) < 0;
    }
}
