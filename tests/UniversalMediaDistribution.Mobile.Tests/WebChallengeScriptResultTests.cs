namespace UniversalMediaDistribution.Mobile.Tests;

public sealed class WebChallengeScriptResultTests
{
    [Fact]
    public void PreservesImageEncodingFromAndroidCallback()
    {
        var envelope = System.Text.Json.JsonSerializer.Serialize(
            "{\"body\":\"iVBORw0KGgo=\",\"contentType\":\"image/png\",\"isBase64\":true}");
        var document = WebChallengeScriptResult.Parse(envelope);
        Assert.NotNull(document);
        Assert.True(document.IsBase64);
        Assert.Equal("image/png", document.ContentType);
        Assert.Equal("iVBORw0KGgo=", document.Body);
    }

    [Fact]
    public void UnwrapsEscapedProviderContent()
    {
        var document = WebChallengeScriptResult.Parse(
            "\"{\\\"body\\\":\\\"<p>Capítulo \\\\\\\"uno\\\\\\\"</p>\\\",\\\"contentType\\\":\\\"text/html\\\"}\"");
        Assert.NotNull(document);
        Assert.Equal("<p>Capítulo \"uno\"</p>", document.Body);
        Assert.Equal("text/html", document.ContentType);
    }

    [Theory]
    [InlineData("\"{\\\"body\\\":\\\"{}\\\",\\\"contentType\\\":\\\"application/json\\\"}\"", "application/json")]
    [InlineData("\"{\\\"body\\\":\\\"page\\\"}\"", "text/html")]
    [InlineData("\"{\\\"body\\\":\\\"page\\\",\\\"contentType\\\":\\\" \\\"}\"", "text/html")]
    public void KeepsContentTypeOrUsesHtmlDefault(string encoded, string expectedType)
    {
        Assert.Equal(expectedType, WebChallengeScriptResult.Parse(encoded)?.ContentType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("\"\"")]
    [InlineData("broken")]
    [InlineData("\"broken\"")]
    [InlineData("\"[]\"")]
    [InlineData("\"{\\\"body\\\":null}\"")]
    [InlineData("\"{\\\"body\\\":42}\"")]
    public void InvalidCallbacksDoNotProduceADocument(string? encoded) =>
        Assert.Null(WebChallengeScriptResult.Parse(encoded));
}
