using System.Text.Json;
using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.Mobile;

/// <summary>Reads Android EvaluateJavascript's JSON string without reflection.</summary>
public static class WebChallengeScriptResult
{
    public static WebChallengeDocument? Parse(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        try
        {
            using var outer = JsonDocument.Parse(encoded);
            if (outer.RootElement.ValueKind != JsonValueKind.String) return null;
            var envelope = outer.RootElement.GetString();
            if (string.IsNullOrWhiteSpace(envelope)) return null;
            return WebChallengeDocument.ParseReport(envelope);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
