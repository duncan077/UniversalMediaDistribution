using MonoTorrent.Client;
using MonoTorrent.Connections;

namespace UniversalMediaDistribution.Torrenting.MonoTorrent.Tests;

public sealed class MonoTorrentEngineOptionsTests
{
    [Fact]
    public void MonoTorrentParserAcceptsReportedMagnet()
    {
        var magnet = global::MonoTorrent.MagnetLink.Parse(
            "magnet:?xt=urn:btih:6a76ae13ab71a5e7a70f2b5a94ba4648165db82f&dn=14");

        Assert.NotNull(magnet);
    }

    [Fact]
    public void MonoTorrentParserAcceptsCanonicalReportedMagnet()
    {
        var magnet = global::MonoTorrent.MagnetLink.Parse(
            "magnet:?xt=urn:btih:6A76AE13AB71A5E7A70F2B5A94BA4648165DB82F&dn=14");

        Assert.NotNull(magnet);
    }

    [Theory]
    [InlineData(null, MonoTorrentEncryptionPolicy.Preferred)]
    [InlineData("preferred", MonoTorrentEncryptionPolicy.Preferred)]
    [InlineData("required", MonoTorrentEncryptionPolicy.Required)]
    [InlineData("disabled", MonoTorrentEncryptionPolicy.Disabled)]
    public void ParseAcceptsSupportedEncryptionPolicies(
        string? value,
        MonoTorrentEncryptionPolicy expected)
    {
        var options = MonoTorrentEngineOptions.Parse(value, null);

        Assert.Equal(expected, options.EncryptionPolicy);
    }

    [Fact]
    public void ParseRejectsUnknownEncryptionPolicy()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            MonoTorrentEngineOptions.Parse("sometimes", null));

        Assert.Contains("UMD_MSE_POLICY", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("best-effort", MonoTorrentFastResumePolicy.BestEffort)]
    [InlineData("accurate", MonoTorrentFastResumePolicy.Accurate)]
    public void ParseAcceptsSupportedFastResumePolicies(
        string value,
        MonoTorrentFastResumePolicy expected)
    {
        var options = MonoTorrentEngineOptions.Parse(null, value);

        Assert.Equal(expected, options.FastResumePolicy);
    }

    [Fact]
    public void PreferredEncryptionTriesMseBeforePlainText()
    {
        var settings = CreateSettings(new MonoTorrentEngineOptions
        {
            EncryptionPolicy = MonoTorrentEncryptionPolicy.Preferred
        });

        Assert.Equal(
            [EncryptionType.RC4Header, EncryptionType.RC4Full, EncryptionType.PlainText],
            settings.AllowedEncryption);
    }

    [Fact]
    public void RequiredEncryptionExcludesPlainText()
    {
        var settings = CreateSettings(new MonoTorrentEngineOptions
        {
            EncryptionPolicy = MonoTorrentEncryptionPolicy.Required
        });

        Assert.Equal(
            [EncryptionType.RC4Header, EncryptionType.RC4Full],
            settings.AllowedEncryption);
    }

    [Fact]
    public void EngineSettingsPersistResumeDataAndDisableLocalDiscovery()
    {
        var settings = CreateSettings(new MonoTorrentEngineOptions
        {
            FastResumePolicy = MonoTorrentFastResumePolicy.Accurate
        });

        Assert.True(settings.AutoSaveLoadFastResume);
        Assert.True(settings.AutoSaveLoadMagnetLinkMetadata);
        Assert.True(settings.AutoSaveLoadDhtCache);
        Assert.False(settings.AllowLocalPeerDiscovery);
        Assert.Equal("http://127.0.0.1:5555/", settings.HttpStreamingPrefix);
        Assert.Equal(FastResumeMode.Accurate, settings.FastResumeMode);
    }

    [Fact]
    public void PrivateTorrentSettingsDisableDhtAndPeerExchange()
    {
        var settings = MonoTorrentEngine.CreateTorrentSettingsBuilder(
            new TorrentAddOptions(
                IsPrivate: true,
                PreferSequentialDownload: true,
                Trackers:
                [
                    new TrackerEndpoint(
                        new Uri("https://tracker.example.test/announce"),
                        TrackerTransport.Https)
                ])).ToSettings();

        Assert.False(settings.AllowDht);
        Assert.False(settings.AllowPeerExchange);
    }

    [Fact]
    public void PublicTorrentSettingsAllowDhtAndPeerExchange()
    {
        var settings = MonoTorrentEngine.CreateTorrentSettingsBuilder(
            TorrentAddOptions.PublicDefault).ToSettings();

        Assert.True(settings.AllowDht);
        Assert.True(settings.AllowPeerExchange);
    }

    private static EngineSettings CreateSettings(MonoTorrentEngineOptions options) =>
        MonoTorrentEngine.CreateEngineSettings(
            Path.Combine(Path.GetTempPath(), "umd-monotorrent-tests", "cache"),
            options);
}
