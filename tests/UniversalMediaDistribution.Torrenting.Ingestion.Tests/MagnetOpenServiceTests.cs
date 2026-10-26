using System.Text.Json;
using System.Text.Json.Serialization;
using UniversalMediaDistribution.Catalog;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Torrenting;

namespace UniversalMediaDistribution.Torrenting.Ingestion.Tests;

public sealed class MagnetOpenServiceTests
{
    private const string InfoHash = "0123456789ABCDEF0123456789ABCDEF01234567";
    private const string Magnet =
        "magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567" +
        "&dn=Manifest%20Movie" +
        "&tr=wss%3A%2F%2Ftracker.example.test%2Fannounce" +
        "&tr=udp%3A%2F%2Ftracker.example.test%3A6969";
    private const string ReportedTrackerlessMagnet =
        "magnet:?xt=urn:btih:6a76ae13ab71a5e7a70f2b5a94ba4648165db82f&dn=14";

    [Fact]
    public async Task Receive_OpensReportedTrackerlessMagnetWithNativeDht()
    {
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.MonoTorrentBaseline,
            files: []);
        var repository = new InMemoryCatalogRepository(
            new CatalogTransferDocument());
        var service = CreateService(
            engine,
            repository,
            new MagnetOpenPreferences(
                OpenAutomatically: true,
                ImportInfoJson: false));

        var result = await service.ReceiveAsync(
            ReportedTrackerlessMagnet);

        Assert.Equal(MagnetOpenStatus.Opened, result.Status);
        Assert.Equal(
            "6A76AE13AB71A5E7A70F2B5A94BA4648165DB82F",
            result.MagnetLink.InfoHashV1);
        Assert.Equal("14", result.MagnetLink.DisplayName);
        Assert.Equal(1, engine.AddCount);
        Assert.Empty(engine.AddedOptions!.Trackers);
    }

    [Fact]
    public async Task Receive_ExplainsTrackerlessMagnetIsIncompatibleWithBrowser()
    {
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.WebTorrentBaseline,
            files: []);
        var repository = new InMemoryCatalogRepository(
            new CatalogTransferDocument());
        var service = CreateService(
            engine,
            repository,
            new MagnetOpenPreferences(
                OpenAutomatically: true,
                ImportInfoJson: false));

        var exception =
            await Assert.ThrowsAsync<MagnetCompatibilityException>(
                () => service.ReceiveAsync(
                    ReportedTrackerlessMagnet).AsTask());

        Assert.Contains("DHT", exception.Message, StringComparison.Ordinal);
        Assert.Contains("WSS", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, engine.AddCount);
    }

    [Fact]
    public async Task Receive_UsesFormatExceptionOnlyForMalformedMagnets()
    {
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.MonoTorrentBaseline,
            files: []);
        var repository = new InMemoryCatalogRepository(
            new CatalogTransferDocument());
        var service = CreateService(
            engine,
            repository,
            new MagnetOpenPreferences(OpenAutomatically: true));

        await Assert.ThrowsAsync<MagnetLinkFormatException>(
            () => service.ReceiveAsync(
                "magnet:?dn=missing-info-hash").AsTask());
    }

    [Fact]
    public async Task Receive_AutomaticallyOpensAndImportsBoundInfoJson()
    {
        var manifest = CreateManifest(infoHash: null, filePath: "media/movie.mp4");
        var manifestBytes = Serialize(manifest);
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.WebTorrentBaseline,
            [
                new TorrentFileEntry(0, "Manifest Movie/info.json", manifestBytes.Length, "application/json"),
                new TorrentFileEntry(1, "Manifest Movie/media/movie.mp4", 1234, "video/mp4"),
                new TorrentFileEntry(2, "Manifest Movie/art/poster.webp", 200, "image/webp")
            ],
            new Dictionary<int, byte[]> { [0] = manifestBytes });
        var repository = new InMemoryCatalogRepository(new CatalogTransferDocument());
        var service = CreateService(
            engine,
            repository,
            new MagnetOpenPreferences(
                OpenAutomatically: true,
                ImportInfoJson: true,
                TreatExternalMagnetsAsPrivate: true));

        var result = await service.ReceiveAsync(Magnet);

        Assert.Equal(MagnetOpenStatus.Opened, result.Status);
        Assert.Null(result.Warning);
        Assert.Equal(TorrentManifestImportStatus.Imported, result.ManifestImport?.Status);
        Assert.Equal(1, engine.AddCount);
        Assert.True(engine.AddedOptions?.IsPrivate);
        Assert.Equal(TrackerTransport.Wss, Assert.Single(engine.AddedOptions!.Trackers).Transport);
        Assert.DoesNotContain("udp", engine.AddedDescriptor!.MagnetUri!, StringComparison.OrdinalIgnoreCase);

        var item = Assert.Single(await repository.ListAsync());
        Assert.Equal("Manifest Movie", item.Name);
        var exported = await repository.ExportCatalogAsync();
        var torrent = Assert.Single(Assert.Single(exported.Items).Sources).Torrent;
        Assert.Equal(InfoHash, torrent.InfoHashV1);
        Assert.True(torrent.IsPrivate);
        Assert.Empty(torrent.Trackers);
        Assert.DoesNotContain("tr=", torrent.MagnetUri!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Manifest Movie/art/poster.webp", Assert.Single(exported.Items).ThumbnailRef);
    }

    [Fact]
    public async Task Receive_DoesNotWaitForTorrentMetadataBeforeOpening()
    {
        var files = new TaskCompletionSource<IReadOnlyList<TorrentFileEntry>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.WebTorrentBaseline,
            pendingFiles: files.Task);
        var repository = new InMemoryCatalogRepository(
            new CatalogTransferDocument());
        var service = CreateService(
            engine,
            repository,
            new MagnetOpenPreferences(
                OpenAutomatically: true,
                ImportInfoJson: true));
        var importCompleted =
            new TaskCompletionSource<TorrentManifestImportCompletedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        service.ManifestImportCompleted += (_, eventArgs) =>
            importCompleted.TrySetResult(eventArgs);

        var result = await service.ReceiveAsync(Magnet)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(MagnetOpenStatus.Opened, result.Status);
        Assert.Equal(
            TorrentManifestImportStatus.Pending,
            result.ManifestImport?.Status);
        Assert.False(files.Task.IsCompleted);

        files.SetResult([]);
        var completed = await importCompleted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        Assert.Equal(result.SessionId, completed.SessionId);
        Assert.Equal(
            TorrentManifestImportStatus.NotFound,
            completed.Result?.Status);
        Assert.Null(completed.Warning);
    }

    [Fact]
    public async Task Receive_RequiresConfirmationWhenAutomaticOpeningIsOff()
    {
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.WebTorrentBaseline,
            files: []);
        var repository = new InMemoryCatalogRepository(new CatalogTransferDocument());
        var service = CreateService(
            engine,
            repository,
            new MagnetOpenPreferences(OpenAutomatically: false));

        var pending = await service.ReceiveAsync(Magnet);
        var opened = await service.ReceiveAsync(Magnet, userConfirmed: true);

        Assert.Equal(MagnetOpenStatus.ConfirmationRequired, pending.Status);
        Assert.Equal(MagnetOpenStatus.Opened, opened.Status);
        Assert.Equal(TorrentManifestImportStatus.NotFound, opened.ManifestImport?.Status);
        Assert.Equal(1, engine.AddCount);
    }

    [Fact]
    public async Task Receive_LeavesTorrentOpenButRejectsManifestForAnotherSwarm()
    {
        var manifest = CreateManifest(
            "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF",
            "media/movie.mp4");
        var manifestBytes = Serialize(manifest);
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.WebTorrentBaseline,
            [
                new TorrentFileEntry(0, "info.json", manifestBytes.Length, "application/json"),
                new TorrentFileEntry(1, "media/movie.mp4", 1234, "video/mp4")
            ],
            new Dictionary<int, byte[]> { [0] = manifestBytes });
        var repository = new InMemoryCatalogRepository(new CatalogTransferDocument());
        var service = CreateService(
            engine,
            repository,
            new MagnetOpenPreferences(OpenAutomatically: true));

        var result = await service.ReceiveAsync(Magnet);

        Assert.Equal(MagnetOpenStatus.Opened, result.Status);
        Assert.NotNull(result.Warning);
        Assert.Contains("active torrent", result.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await repository.ListAsync());
    }

    [Fact]
    public void StandardCatalogValidation_StillRejectsSourceWithoutTorrentIdentity()
    {
        var manifest = CreateManifest(infoHash: null, filePath: "media/movie.mp4");

        var exception = Assert.Throws<CatalogTransferValidationException>(
            () => CatalogTransferValidator.Validate(manifest));

        Assert.Contains(
            exception.Errors,
            static error => error.Contains(
                "requires an info hash",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Receive_LeavesTorrentOpenButRejectsAbsoluteManifestFilePath()
    {
        var manifest = CreateManifest(InfoHash, "/media/movie.mp4");
        var manifestBytes = Serialize(manifest);
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.WebTorrentBaseline,
            [
                new TorrentFileEntry(0, "info.json", manifestBytes.Length, "application/json"),
                new TorrentFileEntry(1, "media/movie.mp4", 1234, "video/mp4")
            ],
            new Dictionary<int, byte[]> { [0] = manifestBytes });
        var repository = new InMemoryCatalogRepository(new CatalogTransferDocument());
        var service = CreateService(
            engine,
            repository,
            new MagnetOpenPreferences(OpenAutomatically: true));

        var result = await service.ReceiveAsync(Magnet);

        Assert.Equal(MagnetOpenStatus.Opened, result.Status);
        Assert.Contains("safe relative torrent path", result.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await repository.ListAsync());
    }

    [Fact]
    public async Task Receive_DoesNotOpenWhenEngineIsUnavailable()
    {
        var repository = new InMemoryCatalogRepository(new CatalogTransferDocument());
        var service = CreateService(
            engine: null,
            repository,
            new MagnetOpenPreferences(OpenAutomatically: true));

        var result = await service.ReceiveAsync(Magnet);

        Assert.Equal(MagnetOpenStatus.EngineUnavailable, result.Status);
    }

    [Fact]
    public async Task DocumentedTorrentInfoExample_ImportsFromItsOwnTorrent()
    {
        var manifestBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "torrent-info.json"));
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.WebTorrentBaseline,
            [
                new TorrentFileEntry(0, "Open Skies/info.json", manifestBytes.Length, "application/json"),
                new TorrentFileEntry(1, "Open Skies/art/cover.webp", 100, "image/webp"),
                new TorrentFileEntry(2, "Open Skies/art/chapter-1.webp", 100, "image/webp"),
                new TorrentFileEntry(3, "Open Skies/chapters/1/page_0.webp", 100, "image/webp"),
                new TorrentFileEntry(4, "Open Skies/chapters/1/page_1.webp", 100, "image/webp"),
                new TorrentFileEntry(5, "Open Skies/chapters/1/page_2.webp", 100, "image/webp")
            ],
            new Dictionary<int, byte[]> { [0] = manifestBytes });
        var repository = new InMemoryCatalogRepository(new CatalogTransferDocument());
        var service = CreateService(
            engine,
            repository,
            new MagnetOpenPreferences(OpenAutomatically: true));

        var result = await service.ReceiveAsync(Magnet);

        Assert.Equal(MagnetOpenStatus.Opened, result.Status);
        Assert.True((await service.LoadPreferencesAsync()).ImportInfoJson);
        Assert.Null(result.Warning);
        Assert.Equal(TorrentManifestImportStatus.Imported, result.ManifestImport?.Status);
        var item = Assert.Single(await repository.ListAsync());
        Assert.Equal("Open Skies", item.Name);
        Assert.Equal("book.manga", item.SubtypeCode);
        Assert.Equal("Open Skies/art/cover.webp", item.ThumbnailRef);
        Assert.Equal(["Independent", "Manga"], item.Categories);
        Assert.Equal(
            ["Cielos abiertos", "Open Skies Manga"],
            item.AlternativeNames.Select(static alternativeName => alternativeName.Name));
    }

    private static MagnetOpenService CreateService(
        ITorrentEngine? engine,
        InMemoryCatalogRepository repository,
        MagnetOpenPreferences preferences) =>
        new(
            engine,
            new CatalogJsonTransferService(repository),
            new InMemoryMagnetOpenPreferencesStore(preferences));

    private static CatalogTransferDocument CreateManifest(string? infoHash, string filePath) => new()
    {
        SourceLabel = "Torrent-local info.json",
        Items =
        [
            new CatalogTransferItem
            {
                MediaType = "movie",
                Subtype = "movie",
                Name = "Manifest Movie",
                Description = "Metadata imported from the torrent itself.",
                ThumbnailRef = "art/poster.webp",
                Sources =
                [
                    new CatalogTransferMediaSource
                    {
                        AssetRole = "primary",
                        SourceLayout = "single_file",
                        FileIndex = 1,
                        FilePath = filePath,
                        MimeType = "video/mp4",
                        SizeBytes = 1234,
                        Torrent = new CatalogTransferTorrent
                        {
                            InfoHashV1 = infoHash,
                            IsPrivate = true,
                            Trackers =
                            [
                                new CatalogTransferTracker
                                {
                                    Endpoint = "https://manifest.example.test/announce",
                                    Transport = "https",
                                    ClientFamily = "both"
                                }
                            ]
                        }
                    }
                ]
            }
        ]
    };

    private static byte[] Serialize(CatalogTransferDocument document) =>
        JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
}
