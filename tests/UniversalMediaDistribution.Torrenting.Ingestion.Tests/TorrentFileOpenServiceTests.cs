using System.Text.Json;
using System.Text.Json.Serialization;
using UniversalMediaDistribution.Catalog;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Torrenting;

namespace UniversalMediaDistribution.Torrenting.Ingestion.Tests;

public sealed class TorrentFileOpenServiceTests
{
    [Fact]
    public async Task ReceiveTorrent_OpensPublicFileWithNativeEngine()
    {
        var torrent = TorrentMetadataTestData.CreateV1(
            "Native release",
            "https://tracker.example.test/announce");
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.MonoTorrentBaseline,
            files: []);
        var service = CreateService(
            engine,
            new MagnetOpenPreferences(ImportInfoJson: false));

        var result = await service.ReceiveTorrentAsync(
            new MemoryStream(torrent.Metadata, writable: false));

        Assert.Equal(TorrentFileOpenStatus.Opened, result.Status);
        Assert.Equal(1, engine.AddCount);
        Assert.Null(engine.AddedDescriptor!.MagnetUri);
        Assert.Equal(
            torrent.Metadata,
            engine.AddedDescriptor.TorrentMetadata.ToArray());
        Assert.False(engine.AddedOptions!.IsPrivate);
        Assert.Equal(
            "https://tracker.example.test/announce",
            Assert.Single(engine.AddedOptions.Trackers).Endpoint.AbsoluteUri);
    }

    [Fact]
    public async Task ReceiveTorrent_UsesPrivateFlagFromMetadata()
    {
        var torrent = TorrentMetadataTestData.CreateV1(
            "Private release",
            "https://tracker.example.test/announce",
            isPrivate: true);
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.MonoTorrentBaseline,
            files: []);
        var service = CreateService(
            engine,
            new MagnetOpenPreferences(ImportInfoJson: false));

        var result = await service.ReceiveTorrentAsync(
            new MemoryStream(torrent.Metadata, writable: false));

        Assert.Equal(TorrentFileOpenStatus.Opened, result.Status);
        Assert.True(engine.AddedOptions!.IsPrivate);
        Assert.Equal(
            TrackerTransport.Https,
            Assert.Single(engine.AddedOptions.Trackers).Transport);
    }

    [Fact]
    public async Task ReceiveTorrent_ExplainsTrackerlessFileIsIncompatibleWithBrowser()
    {
        var torrent = TorrentMetadataTestData.CreateV1("Trackerless release");
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.WebTorrentBaseline,
            files: []);
        var service = CreateService(
            engine,
            new MagnetOpenPreferences(ImportInfoJson: false));

        var exception =
            await Assert.ThrowsAsync<MagnetCompatibilityException>(
                () => service.ReceiveTorrentAsync(
                    new MemoryStream(
                        torrent.Metadata,
                        writable: false)).AsTask());

        Assert.Contains("torrent file", exception.Message, StringComparison.Ordinal);
        Assert.Contains("WSS", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, engine.AddCount);
    }

    [Fact]
    public async Task ReceiveTorrent_DeduplicatesAnAlreadyOpenMagnet()
    {
        var torrent = TorrentMetadataTestData.CreateV1("Shared release");
        var metadata = TorrentMetadataParser.Parse(torrent.Metadata);
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.MonoTorrentBaseline,
            files: []);
        var service = CreateService(
            engine,
            new MagnetOpenPreferences(ImportInfoJson: false));
        _ = await service.ReceiveAsync(
            metadata.CreateIdentityMagnet().OriginalUri,
            userConfirmed: true);

        var result = await service.ReceiveTorrentAsync(
            new MemoryStream(torrent.Metadata, writable: false));

        Assert.Equal(TorrentFileOpenStatus.AlreadyOpen, result.Status);
        Assert.Equal(1, engine.AddCount);
    }

    [Fact]
    public async Task ReceiveTorrent_RejectsInvalidBencodingBeforeEngineAdd()
    {
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.MonoTorrentBaseline,
            files: []);
        var service = CreateService(
            engine,
            new MagnetOpenPreferences(ImportInfoJson: false));

        await Assert.ThrowsAsync<TorrentMetadataFormatException>(
            () => service.ReceiveTorrentAsync(
                new MemoryStream("invalid"u8.ToArray(), writable: false))
                .AsTask());

        Assert.Equal(0, engine.AddCount);
    }

    [Fact]
    public async Task ReceiveTorrent_ImportsInfoJsonFromSelectedTorrent()
    {
        var torrent = TorrentMetadataTestData.CreateV1(
            "File manifest release",
            "https://tracker.example.test/announce");
        var manifest = new CatalogTransferDocument
        {
            SourceLabel = "Selected torrent info.json",
            Items =
            [
                new CatalogTransferItem
                {
                    MediaType = "movie",
                    Subtype = "movie",
                    Name = "File Manifest Movie",
                    Sources =
                    [
                        new CatalogTransferMediaSource
                        {
                            AssetRole = "primary",
                            SourceLayout = "single_file",
                            FileIndex = 1,
                            FilePath = "media/movie.mp4",
                            MimeType = "video/mp4",
                            SizeBytes = 1234,
                            Torrent = new CatalogTransferTorrent()
                        }
                    ]
                }
            ]
        };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition =
                    JsonIgnoreCondition.WhenWritingNull
            });
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.MonoTorrentBaseline,
            [
                new TorrentFileEntry(
                    0,
                    "File manifest release/info.json",
                    manifestBytes.Length,
                    "application/json"),
                new TorrentFileEntry(
                    1,
                    "File manifest release/media/movie.mp4",
                    1234,
                    "video/mp4")
            ],
            new Dictionary<int, byte[]> { [0] = manifestBytes });
        var repository = new InMemoryCatalogRepository(
            new CatalogTransferDocument());
        var service = new MagnetOpenService(
            engine,
            new CatalogJsonTransferService(repository),
            new InMemoryMagnetOpenPreferencesStore(
                new MagnetOpenPreferences(ImportInfoJson: true)));

        var result = await service.ReceiveTorrentAsync(
            new MemoryStream(torrent.Metadata, writable: false));

        Assert.Equal(TorrentFileOpenStatus.Opened, result.Status);
        Assert.Null(result.Warning);
        Assert.Equal(
            TorrentManifestImportStatus.Imported,
            result.ManifestImport?.Status);
        Assert.Equal(
            "File Manifest Movie",
            Assert.Single(await repository.ListAsync()).Name);

        var exported = await repository.ExportCatalogAsync();
        var source = Assert.Single(
            Assert.Single(exported.Items).Sources);
        Assert.Equal(
            TorrentMetadataParser.Parse(torrent.Metadata).InfoHashV1,
            source.Torrent.InfoHashV1);
    }

    private static MagnetOpenService CreateService(
        ITorrentEngine engine,
        MagnetOpenPreferences preferences)
    {
        var repository = new InMemoryCatalogRepository(
            new CatalogTransferDocument());
        return new MagnetOpenService(
            engine,
            new CatalogJsonTransferService(repository),
            new InMemoryMagnetOpenPreferencesStore(preferences));
    }
}
