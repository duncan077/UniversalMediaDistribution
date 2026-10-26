using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Torrenting.Ingestion.Tests;

public sealed class TorrentPrivacyIntegrationTests
{
    private const string InfoHash = "0123456789ABCDEF0123456789ABCDEF01234567";

    [Fact]
    public void IncomingMagnetPrivacyIsOptInByDefault()
    {
        var preferences = new MagnetOpenPreferences();

        Assert.False(preferences.OpenAutomatically);
        Assert.True(preferences.ImportInfoJson);
        Assert.False(preferences.TreatExternalMagnetsAsPrivate);
    }

    [Fact]
    public async Task PublicNativeSourceCanUseDhtWithoutTrackers()
    {
        var engine = CreateEngine(TorrentEngineCapabilities.MonoTorrentBaseline);
        var service = new TorrentMediaSessionService(engine);

        _ = await service.PrepareSingleFileAsync(CreateSource(
            isPrivate: false,
            trackers: []));

        Assert.NotNull(engine.AddedOptions);
        Assert.False(engine.AddedOptions.IsPrivate);
        Assert.Empty(engine.AddedOptions.Trackers);
    }

    [Fact]
    public async Task AuthenticatedPrivateRouteIsResolvedWithoutLeakingThroughDiagnostics()
    {
        var engine = CreateEngine(TorrentEngineCapabilities.WebTorrentBaseline);
        var secretEndpoint = new Uri(
            "wss://browser.example.test/announce?token=super-secret");
        var provider = new FixedCredentialProvider(secretEndpoint);
        var service = new TorrentMediaSessionService(
            engine,
            trackerCredentialProvider: provider);
        var tracker = new CatalogTrackerDetails(
            "Authenticated browser tracker",
            new Uri("wss://browser.example.test/announce"),
            "wss",
            "browser",
            true,
            0,
            0,
            "indie-community");

        _ = await service.PrepareSingleFileAsync(CreateSource(
            isPrivate: true,
            trackers: [tracker]));

        var resolvedTracker = Assert.Single(engine.AddedOptions!.Trackers);
        Assert.Equal(secretEndpoint, resolvedTracker.Endpoint);
        Assert.True(resolvedTracker.UsesCredential);
        Assert.Equal("indie-community", provider.LastRequest?.CredentialKey);
        Assert.DoesNotContain(
            "indie-community",
            tracker.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "super-secret",
            resolvedTracker.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "super-secret",
            engine.AddedOptions.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "super-secret",
            Uri.UnescapeDataString(engine.AddedDescriptor!.MagnetUri!),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticatedPrivateRouteFailsClosedWhenCredentialIsMissing()
    {
        var engine = CreateEngine(TorrentEngineCapabilities.WebTorrentBaseline);
        var service = new TorrentMediaSessionService(engine);
        var tracker = new CatalogTrackerDetails(
            "Authenticated browser tracker",
            new Uri("wss://browser.example.test/announce"),
            "wss",
            "browser",
            true,
            0,
            0,
            "indie-community");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.PrepareSingleFileAsync(CreateSource(
                isPrivate: true,
                trackers: [tracker])));

        Assert.Contains(
            "no local credential",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, engine.AddCount);
    }

    [Fact]
    public async Task DualPrivateEndpointsRouteOneIdentityToNativeAndBrowser()
    {
        var nativeEngine = CreateEngine(TorrentEngineCapabilities.MonoTorrentBaseline);
        var browserEngine = CreateEngine(TorrentEngineCapabilities.WebTorrentBaseline);
        var trackers = new CatalogTrackerDetails[]
        {
            new(
                "Native",
                new Uri("https://native.example.test/announce"),
                "https",
                "native",
                false,
                0,
                0),
            new(
                "Browser",
                new Uri("wss://browser.example.test/announce"),
                "wss",
                "browser",
                false,
                0,
                1),
            new(
                "Public UDP",
                new Uri("udp://public.example.test:6969/announce"),
                "udp",
                "native",
                false,
                1,
                0)
        };
        var source = CreateSource(isPrivate: true, trackers);

        _ = await new TorrentMediaSessionService(nativeEngine)
            .PrepareSingleFileAsync(source);
        _ = await new TorrentMediaSessionService(browserEngine)
            .PrepareSingleFileAsync(source);

        var nativeTracker = Assert.Single(nativeEngine.AddedOptions!.Trackers);
        var browserTracker = Assert.Single(browserEngine.AddedOptions!.Trackers);
        Assert.Equal(TrackerTransport.Https, nativeTracker.Transport);
        Assert.Equal(TrackerTransport.Wss, browserTracker.Transport);
        Assert.Equal(
            MagnetLink.Parse(nativeEngine.AddedDescriptor!.MagnetUri!).StableIdentity,
            MagnetLink.Parse(browserEngine.AddedDescriptor!.MagnetUri!).StableIdentity);
        Assert.DoesNotContain(
            nativeEngine.AddedOptions.Trackers,
            static tracker => tracker.Transport is TrackerTransport.Udp);
        Assert.DoesNotContain(
            browserEngine.AddedOptions.Trackers,
            static tracker => tracker.Transport is not TrackerTransport.Wss);
    }

    [Fact]
    public async Task EnvironmentProviderUsesAHostScopedOverrideAndRejectsRedirects()
    {
        var request = new TrackerCredentialRequest(
            new Uri("https://tracker.example.test/announce"),
            TrackerTransport.Https);
        var variableName =
            EnvironmentTrackerCredentialProvider.GetEnvironmentVariableName(request);
        var provider = new EnvironmentTrackerCredentialProvider(name =>
            name == variableName
                ? "https://tracker.example.test/member/passkey/announce"
                : null);

        var resolved = await provider.ResolveAsync(request);

        Assert.Equal(
            "UMD_TRACKER_ENDPOINT_HTTPS_TRACKER_EXAMPLE_TEST",
            variableName);
        Assert.Equal(
            "https://tracker.example.test/member/passkey/announce",
            resolved?.Endpoint.AbsoluteUri);
        Assert.DoesNotContain(
            "passkey",
            resolved?.ToString(),
            StringComparison.OrdinalIgnoreCase);

        var redirectingProvider = new EnvironmentTrackerCredentialProvider(_ =>
            "https://other.example.test/member/passkey/announce");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await redirectingProvider.ResolveAsync(request));
    }

    [Fact]
    public void CatalogValidationRejectsEmbeddedSecretsAndPrivateUdpRoutes()
    {
        var document = CreateTransferDocument(
            new CatalogTransferTracker
            {
                Endpoint = "wss://tracker.example.test/announce?token=secret",
                Transport = "wss",
                ClientFamily = "browser",
                RequiresAuthentication = true
            },
            new CatalogTransferTracker
            {
                Endpoint = "udp://public.example.test:6969/announce",
                Transport = "udp",
                ClientFamily = "native"
            });

        var exception = Assert.Throws<CatalogTransferValidationException>(
            () => CatalogTransferValidator.Validate(document));

        Assert.Contains(
            exception.Errors,
            static error => error.Contains(
                "cannot contain tracker credentials",
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            exception.Errors,
            static error => error.Contains(
                "cannot use UDP",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AdapterPolicyRejectsPublicDiscoveryRoutesInPrivateMode()
    {
        var options = new TorrentAddOptions(
            IsPrivate: true,
            PreferSequentialDownload: true,
            Trackers:
            [
                new TrackerEndpoint(
                    new Uri("udp://public.example.test:6969/announce"),
                    TrackerTransport.Udp)
            ]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            TorrentPrivacyPolicy.Validate(
                TorrentEngineCapabilities.MonoTorrentBaseline,
                options));

        Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdapterPolicyRejectsUnapprovedTrackersEmbeddedInPrivateMagnets()
    {
        var options = new TorrentAddOptions(
            IsPrivate: true,
            PreferSequentialDownload: true,
            Trackers:
            [
                new TrackerEndpoint(
                    new Uri("wss://approved.example.test/announce"),
                    TrackerTransport.Wss)
            ]);
        var descriptor = new TorrentDescriptor(
            $"magnet:?xt=urn:btih:{InfoHash}" +
            "&tr=wss%3A%2F%2Fpublic.example.test%2Fannounce");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            TorrentPrivacyPolicy.Validate(
                TorrentEngineCapabilities.WebTorrentBaseline,
                descriptor,
                options));

        Assert.Contains(
            "unapproved tracker",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static FakeTorrentEngine CreateEngine(TorrentEngineCapabilities capabilities) =>
        new(
            capabilities,
            [new TorrentFileEntry(0, "Release/movie.webm", 10, "video/webm")]);

    private static CatalogMediaSourceDetails CreateSource(
        bool isPrivate,
        IReadOnlyList<CatalogTrackerDetails> trackers) =>
        new(
            "primary",
            CatalogMediaSourceLayout.SingleFile,
            0,
            "movie.webm",
            "video/webm",
            "WEBM",
            "1080p",
            "en",
            10,
            0,
            new CatalogTorrentDetails(
                "Indie release",
                $"magnet:?xt=urn:btih:{InfoHash}" +
                "&tr=wss%3A%2F%2Funtrusted.example.test%2Fannounce",
                ReadOnlyMemory<byte>.Empty,
                InfoHash,
                null,
                isPrivate,
                10,
                null,
                trackers));

    private static CatalogTransferDocument CreateTransferDocument(
        params CatalogTransferTracker[] trackers) =>
        new()
        {
            Items =
            [
                new CatalogTransferItem
                {
                    MediaType = "movie",
                    Subtype = "movie",
                    Name = "Private validation test",
                    Sources =
                    [
                        new CatalogTransferMediaSource
                        {
                            FileIndex = 0,
                            FilePath = "movie.webm",
                            Torrent = new CatalogTransferTorrent
                            {
                                InfoHashV1 = InfoHash,
                                IsPrivate = true,
                                Trackers = trackers
                            }
                        }
                    ]
                }
            ]
        };

    private sealed class FixedCredentialProvider(Uri endpoint) : ITrackerCredentialProvider
    {
        public TrackerCredentialRequest? LastRequest { get; private set; }

        public ValueTask<TrackerCredentialResolution?> ResolveAsync(
            TrackerCredentialRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return ValueTask.FromResult<TrackerCredentialResolution?>(
                new TrackerCredentialResolution(endpoint));
        }
    }
}
