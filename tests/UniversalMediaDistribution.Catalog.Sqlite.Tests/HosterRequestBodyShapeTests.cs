using System.Text.Json;
using System.Text.Json.Nodes;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

/// <summary>
/// The hoster request bodies were anonymous types serialized by
/// <c>JsonContent.Create</c>, which needs the reflection-based serializer and
/// therefore throws once reflection is disabled. They were rewritten as
/// <see cref="JsonObject"/> trees, which serialize without reflection. A
/// rewrite that changed the JSON even slightly would not throw — the hoster
/// would simply reject the request and the extractor would report that no
/// server could be opened, which is indistinguishable from the site being
/// down. These pin the two forms together.
/// </summary>
public sealed class HosterRequestBodyShapeTests
{
    [Fact]
    public void TheAttestationBodyMatchesTheAnonymousTypeItReplaced()
    {
        const string viewerId = "viewer-1";
        const string deviceId = "device-1";
        const string challengeId = "challenge-1";
        const string nonce = "nonce-1";
        const string signature = "sig";
        const string x = "x-coord";
        const string y = "y-coord";
        const string userAgent = "agent/1.0";

        var expected = JsonSerializer.Serialize<object>(new
        {
            viewer_id = viewerId,
            device_id = deviceId,
            challenge_id = challengeId,
            nonce,
            signature,
            public_key = new
            {
                crv = "P-256",
                ext = true,
                key_ops = new[] { "verify" },
                kty = "EC",
                x,
                y
            },
            client = new
            {
                user_agent = userAgent,
                architecture = "x86",
                bitness = "64",
                platform = "Windows",
                platform_version = "10.0.0",
                pixel_ratio = 1.0,
                screen_width = 1920,
                screen_height = 1080,
                languages = new[] { "en-US" }
            },
            storage = new
            {
                cookie = viewerId,
                local_storage = viewerId,
                indexed_db = $"{viewerId}:{deviceId}",
                cache_storage = $"{viewerId}:{deviceId}"
            },
            attributes = new { entropy = "high" }
        });

        var actual = new JsonObject
        {
            ["viewer_id"] = viewerId,
            ["device_id"] = deviceId,
            ["challenge_id"] = challengeId,
            ["nonce"] = nonce,
            ["signature"] = signature,
            ["public_key"] = new JsonObject
            {
                ["crv"] = "P-256",
                ["ext"] = true,
                ["key_ops"] = new JsonArray("verify"),
                ["kty"] = "EC",
                ["x"] = x,
                ["y"] = y
            },
            ["client"] = new JsonObject
            {
                ["user_agent"] = userAgent,
                ["architecture"] = "x86",
                ["bitness"] = "64",
                ["platform"] = "Windows",
                ["platform_version"] = "10.0.0",
                ["pixel_ratio"] = 1.0,
                ["screen_width"] = 1920,
                ["screen_height"] = 1080,
                ["languages"] = new JsonArray("en-US")
            },
            ["storage"] = new JsonObject
            {
                ["cookie"] = viewerId,
                ["local_storage"] = viewerId,
                ["indexed_db"] = $"{viewerId}:{deviceId}",
                ["cache_storage"] = $"{viewerId}:{deviceId}"
            },
            ["attributes"] = new JsonObject { ["entropy"] = "high" }
        }.ToJsonString();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ThePlaybackFingerprintMatchesTheAnonymousTypeItReplaced()
    {
        const string token = "token-1";
        const string viewerId = "viewer-1";
        const string deviceId = "device-1";
        const double confidence = 0.87;

        var expected = JsonSerializer.Serialize<object>(new
        {
            fingerprint = new
            {
                token,
                viewer_id = viewerId,
                device_id = deviceId,
                confidence
            }
        });

        var actual = new JsonObject
        {
            ["fingerprint"] = new JsonObject
            {
                ["token"] = token,
                ["viewer_id"] = viewerId,
                ["device_id"] = deviceId,
                ["confidence"] = confidence
            }
        }.ToJsonString();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TheGraphQlEnvelopeMatchesTheAnonymousTypeItReplaced()
    {
        const string operationName = "searchAll";
        const string query = "query searchAll($input: String!) { }";
        var variables = new JsonObject { ["input"] = "some title" };

        var expected = JsonSerializer.Serialize<object>(new
        {
            operationName,
            variables = new { input = "some title" },
            query
        });

        var actual = new JsonObject
        {
            ["operationName"] = operationName,
            ["variables"] = variables,
            ["query"] = query
        }.ToJsonString();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// An empty body has to stay an empty object rather than becoming null,
    /// because the hoster distinguishes the two.
    /// </summary>
    [Fact]
    public void AnEmptyBodyStaysAnEmptyObject()
    {
        Assert.Equal(
            JsonSerializer.Serialize<object>(new { }),
            new JsonObject().ToJsonString());
    }
}
