namespace UniversalMediaDistribution.Torrenting.Ingestion.Tests;

public sealed class MagnetLinkTests
{
    private const string ReportedMagnet =
        "magnet:?xt=urn:btih:6a76ae13ab71a5e7a70f2b5a94ba4648165db82f&dn=14";

    [Fact]
    public void Parse_AcceptsReportedHexInfoHashWithNumericDisplayName()
    {
        var magnet = MagnetLink.Parse(ReportedMagnet);

        Assert.Equal(
            "6A76AE13AB71A5E7A70F2B5A94BA4648165DB82F",
            magnet.InfoHashV1);
        Assert.Equal("14", magnet.DisplayName);
        Assert.Equal(ReportedMagnet, magnet.OriginalUri);
        Assert.Equal(
            "magnet:?xt=urn:btih:6A76AE13AB71A5E7A70F2B5A94BA4648165DB82F&dn=14",
            magnet.CatalogIdentityUri);
    }

    [Fact]
    public void Parse_SupportsHexBase32AndV2ExactTopics()
    {
        const string v2 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var magnet = MagnetLink.Parse(
            "magnet:?xt=urn:btih:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            $"&xt=urn:btmh:1220{v2}&dn=Open%20Release");

        Assert.Equal(new string('0', 40), magnet.InfoHashV1);
        Assert.Equal(v2, magnet.InfoHashV2);
        Assert.Equal("Open Release", magnet.DisplayName);
        Assert.DoesNotContain("tr=", magnet.CatalogIdentityUri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsMissingOrConflictingExactTopics()
    {
        Assert.Throws<ArgumentException>(() => MagnetLink.Parse("magnet:?dn=No+Identity"));
        Assert.Throws<ArgumentException>(() => MagnetLink.Parse(
            "magnet:?xt=urn:btih:0000000000000000000000000000000000000000" +
            "&xt=urn:btih:1111111111111111111111111111111111111111"));
    }

    [Fact]
    public void Parse_RejectsUnboundedTrackerListsAndReadsPrivateFlagCaseInsensitively()
    {
        const string prefix =
            "magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567";
        var tooManyTrackers = prefix + string.Concat(Enumerable.Range(0, MagnetLink.MaximumTrackers + 1)
            .Select(index => $"&tr=https%3A%2F%2Ftracker{index}.example.test%2Fannounce"));

        Assert.Throws<ArgumentException>(() => MagnetLink.Parse(tooManyTrackers));
        Assert.True(MagnetLink.Parse(prefix + "&x.umd.private=TRUE").DeclaresPrivate);
    }

    [Fact]
    public void StartupParser_ExtractsAndDeduplicatesDirectAndBrowserActivations()
    {
        const string magnet = "magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567";
        var browserActivation = "https://app.example/?magnet=" + Uri.EscapeDataString(magnet);

        var links = MagnetStartupRequestParser.Extract([magnet, browserActivation, "--unrelated"]);

        Assert.Equal(magnet, Assert.Single(links));
    }
}
