using System.IO.Compression;
using System.Text;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Catalog;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Reading;

namespace UniversalMediaDistribution.Torrenting.Ingestion.Tests;

public sealed class TorrentMediaSessionServiceTests
{
    private const string InfoHash = "0123456789ABCDEF0123456789ABCDEF01234567";

    [Fact]
    public async Task PrepareSingleFile_UsesPrivateBrowserTrackerAndSelectsOnlyMappedFile()
    {
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.WebTorrentBaseline,
            [
                new TorrentFileEntry(0, "Release/info.json", 100, "application/json"),
                new TorrentFileEntry(1, "Release/video/movie.webm", 900, "video/webm")
            ]);
        var service = new TorrentMediaSessionService(engine);
        var source = CreateSource(
            CatalogMediaSourceLayout.SingleFile,
            fileIndex: 1,
            filePath: "video/movie.webm");

        var prepared = await service.PrepareSingleFileAsync(source);

        Assert.Equal(1, prepared.FileIndex);
        Assert.Equal("Release/video/movie.webm", prepared.Path);
        Assert.True(engine.AddedOptions?.IsPrivate);
        Assert.False(engine.AddedOptions?.SelectAllFiles);
        var tracker = Assert.Single(engine.AddedOptions?.Trackers ?? []);
        Assert.Equal("wss://browser.example.test/announce", tracker.Endpoint.AbsoluteUri);
        Assert.Contains((1, TorrentFilePriority.High), engine.PriorityChanges);
        Assert.DoesNotContain(
            engine.AddedOptions?.Trackers ?? [],
            value => value.Endpoint.Scheme is "https" or "udp");
    }

    [Fact]
    public async Task ResolveSingleFile_KeepsOptionalRenditionLazyUntilItIsRead()
    {
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.WebTorrentBaseline,
            [new TorrentFileEntry(0, "Release/video/movie-es.webm", 4, "video/webm")],
            new Dictionary<int, byte[]> { [0] = [1, 2, 3, 4] });
        var service = new TorrentMediaSessionService(engine);
        var source = CreateSource(
            CatalogMediaSourceLayout.SingleFile,
            fileIndex: 0,
            filePath: "video/movie-es.webm");

        var resolved = await service.ResolveSingleFileAsync(source);
        _ = await service.GetPlaybackUriWithoutSelectingAsync(resolved);

        Assert.DoesNotContain(
            engine.PriorityChanges,
            static change => change.Priority is TorrentFilePriority.High);

        await using var read = await service.OpenReadAsync(resolved);

        Assert.Contains((0, TorrentFilePriority.High), engine.PriorityChanges);
    }

    [Fact]
    public async Task PrepareImageSequence_OrdersNumericSuffixFromZeroAndSelectsLazily()
    {
        var contents = new Dictionary<int, byte[]>
        {
            [2] = [2],
            [3] = [3],
            [4] = [4]
        };
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.WebTorrentBaseline,
            [
                new TorrentFileEntry(4, "Book/chapters/1/page_2.webp", 1, "image/webp"),
                new TorrentFileEntry(2, "Book/chapters/1/page_0.webp", 1, "image/webp"),
                new TorrentFileEntry(3, "Book/chapters/1/page_1.webp", 1, "image/webp"),
                new TorrentFileEntry(5, "Book/chapters/1/notes.txt", 1, "text/plain")
            ],
            contents);
        var service = new TorrentMediaSessionService(engine);
        var source = CreateSource(
            CatalogMediaSourceLayout.ImageSequence,
            fileIndex: null,
            filePath: "chapters/1");

        var sequence = await service.PrepareImageSequenceAsync(source);

        Assert.Equal([0, 1, 2], sequence.Pages.Select(static page => page.Position));
        Assert.Equal([2, 3, 4], sequence.Pages.Select(static page => page.File.FileIndex));
        Assert.DoesNotContain(engine.PriorityChanges, change => change.Priority is TorrentFilePriority.High);

        await using var read = await service.OpenReadAsync(sequence.Pages[1].File);
        Assert.NotNull(read.Stream);
        Assert.Contains((3, TorrentFilePriority.High), engine.PriorityChanges);
    }

    [Fact]
    public async Task PrepareImageSequence_AcceptsAFirstPageReferenceAndReusesTorrentSession()
    {
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.WebTorrentBaseline,
            [
                new TorrentFileEntry(0, "Book/chapter/page_0.jpg", 10, "image/jpeg"),
                new TorrentFileEntry(1, "Book/chapter/page_1.jpg", 10, "image/jpeg"),
                new TorrentFileEntry(2, "Book/audio.ogg", 20, "audio/ogg")
            ]);
        var service = new TorrentMediaSessionService(engine);
        var imageSource = CreateSource(
            CatalogMediaSourceLayout.ImageSequence,
            fileIndex: null,
            filePath: "chapter/page_0.jpg");
        var audioSource = CreateSource(
            CatalogMediaSourceLayout.SingleFile,
            fileIndex: 2,
            filePath: "audio.ogg");

        var pages = await service.PrepareImageSequenceAsync(imageSource);
        _ = await service.PrepareSingleFileAsync(audioSource);

        Assert.Equal(2, pages.Pages.Count);
        Assert.Equal(1, engine.AddCount);
    }

    [Fact]
    public async Task PrepareImageSequence_RejectsMissingZeroBasedPosition()
    {
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.WebTorrentBaseline,
            [new TorrentFileEntry(0, "Book/chapter/page_1.jpg", 10, "image/jpeg")]);
        var service = new TorrentMediaSessionService(engine);
        var source = CreateSource(
            CatalogMediaSourceLayout.ImageSequence,
            fileIndex: null,
            filePath: "chapter");

        await Assert.ThrowsAsync<ImageSequenceValidationException>(async () =>
            await service.PrepareImageSequenceAsync(source));
    }

    [Fact]
    public async Task PrepareSingleImage_OpensJpegPngOrWebpAsOneReaderPage()
    {
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.WebTorrentBaseline,
            [new TorrentFileEntry(0, "Book/cover.webp", 4, "image/webp")],
            new Dictionary<int, byte[]> { [0] = [1, 2, 3, 4] });
        var service = new TorrentMediaSessionService(engine);
        var source = CreateSource(
            CatalogMediaSourceLayout.SingleFile,
            fileIndex: 0,
            filePath: "cover.webp") with
        {
            MimeType = "image/webp",
            Container = "WEBP",
            SizeBytes = 4
        };

        var sequence = await service.PrepareSingleImageAsync(source);

        var page = Assert.Single(sequence.Pages);
        Assert.Equal(0, page.Position);
        Assert.Equal("Book/cover.webp", page.File.Path);
        await using var read = await service.OpenReadAsync(page.File);
        Assert.Equal(4, read.Stream?.Length);
    }

    [Fact]
    public async Task PrepareImageArchive_ValidatesNaturallyOrdersAndReadsCbzPages()
    {
        var cbz = CreateCbz(
            ("pages/page10.jpg", new byte[] { 10 }),
            ("pages/page2.jpg", new byte[] { 2 }),
            ("pages/page1.jpg", new byte[] { 1 }));
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.WebTorrentBaseline,
            [new TorrentFileEntry(0, "Book/chapter.cbz", cbz.Length, "application/vnd.comicbook+zip")],
            new Dictionary<int, byte[]> { [0] = cbz });
        var service = new TorrentMediaSessionService(engine);
        var source = CreateSource(
            CatalogMediaSourceLayout.SingleFile,
            fileIndex: 0,
            filePath: "chapter.cbz") with
        {
            MimeType = "application/vnd.comicbook+zip",
            Container = "CBZ",
            SizeBytes = cbz.Length
        };

        using var archive = await service.PrepareImageArchiveAsync(source);

        Assert.Equal(
            ["pages/page1.jpg", "pages/page2.jpg", "pages/page10.jpg"],
            archive.Pages.Select(static page => page.Path));
        Assert.Equal([0, 1, 2], archive.Pages.Select(static page => page.Position));
        await using var read = await archive.OpenReadAsync(archive.Pages[1]);
        var content = new byte[1];
        Assert.Equal(1, await read.Stream!.ReadAsync(content));
        Assert.Equal(2, content[0]);
        Assert.Contains((0, TorrentFilePriority.High), engine.PriorityChanges);
    }

    [Fact]
    public async Task PrepareImageArchive_RejectsUnsafePagePath()
    {
        var cbz = CreateCbz(("../page.jpg", new byte[] { 1 }));
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.WebTorrentBaseline,
            [new TorrentFileEntry(0, "Book/chapter.cbz", cbz.Length, "application/vnd.comicbook+zip")],
            new Dictionary<int, byte[]> { [0] = cbz });
        var service = new TorrentMediaSessionService(engine);
        var source = CreateSource(
            CatalogMediaSourceLayout.SingleFile,
            fileIndex: 0,
            filePath: "chapter.cbz") with
        {
            MimeType = "application/vnd.comicbook+zip",
            Container = "CBZ",
            SizeBytes = cbz.Length
        };

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await service.PrepareImageArchiveAsync(source));
    }

    [Fact]
    public async Task PrepareEpubBook_UsesPackageSpineAndExtractsReadableChapters()
    {
        var epub = CreateEpub(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>Open Skies</dc:title>
                <dc:language>en</dc:language>
              </metadata>
              <manifest>
                <item id="chapter-one" href="Text/chapter1.xhtml" media-type="application/xhtml+xml" />
                <item id="chapter-two" href="Text/chapter%202.xhtml" media-type="application/xhtml+xml" />
              </manifest>
              <spine>
                <itemref idref="chapter-two" />
                <itemref idref="chapter-one" />
              </spine>
            </package>
            """,
            (
                "OEBPS/Text/chapter1.xhtml",
                """
                <html xmlns="http://www.w3.org/1999/xhtml">
                  <head><title>First</title></head>
                  <body><h1>First</h1><p>First chapter text.</p></body>
                </html>
                """),
            (
                "OEBPS/Text/chapter 2.xhtml",
                """
                <html xmlns="http://www.w3.org/1999/xhtml">
                  <head><title>Second</title></head>
                  <body>
                    <h1>Second</h1>
                    <p>Second chapter text.<script>hidden script text</script></p>
                    <ul><li>A list item</li></ul>
                  </body>
                </html>
                """));
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.WebTorrentBaseline,
            [new TorrentFileEntry(0, "Book/open-skies.epub", epub.Length, "application/epub+zip")],
            new Dictionary<int, byte[]> { [0] = epub });
        var service = new TorrentMediaSessionService(engine);
        var source = CreateSource(
            CatalogMediaSourceLayout.SingleFile,
            fileIndex: 0,
            filePath: "open-skies.epub") with
        {
            MimeType = "application/epub+zip",
            Container = "EPUB",
            SizeBytes = epub.Length
        };

        using var book = await service.PrepareEpubBookAsync(source);

        Assert.Equal("Open Skies", book.PackageTitle);
        Assert.Equal("en", book.Language);
        Assert.Equal(
            ["OEBPS/Text/chapter 2.xhtml", "OEBPS/Text/chapter1.xhtml"],
            book.Chapters.Select(static chapter => chapter.Path));
        Assert.Equal(["Second", "First"], book.Chapters.Select(static chapter => chapter.Title));
        Assert.Contains("Second chapter text.", book.Chapters[0].Text);
        Assert.Contains("• A list item", book.Chapters[0].Text);
        Assert.DoesNotContain("hidden script text", book.Chapters[0].Text);
        Assert.Contains((0, TorrentFilePriority.High), engine.PriorityChanges);
    }

    [Fact]
    public async Task PrepareEpubBook_StreamsJpegPngAndWebpBlocksInDocumentOrder()
    {
        var epub = CreateEpubArchive(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>Illustrated book</dc:title>
              </metadata>
              <manifest>
                <item id="mixed" href="Text/mixed.xhtml" media-type="application/xhtml+xml" />
                <item id="image-only" href="Text/image-only.xhtml" media-type="application/xhtml+xml" />
                <item id="jpeg" href="Images/page.jpg" media-type="image/jpeg" />
                <item id="png" href="Images/diagram.png" media-type="image/png" />
                <item id="webp" href="Images/finale.webp" media-type="image/webp" />
              </manifest>
              <spine>
                <itemref idref="mixed" />
                <itemref idref="image-only" />
              </spine>
            </package>
            """,
            (
                "OEBPS/Text/mixed.xhtml",
                Encoding.UTF8.GetBytes(
                    """
                    <html xmlns="http://www.w3.org/1999/xhtml">
                      <head><title>Mixed chapter</title></head>
                      <body>
                        <p>Before the first image.</p>
                        <img src="../Images/page.jpg" alt="JPEG page" />
                        <p>Between the images.</p>
                        <picture>
                          <source srcset="../Images/finale.webp" type="image/webp" />
                          <img src="../Images/diagram.png" alt="PNG diagram" />
                        </picture>
                      </body>
                    </html>
                    """)),
            (
                "OEBPS/Text/image-only.xhtml",
                Encoding.UTF8.GetBytes(
                    """
                    <html xmlns="http://www.w3.org/1999/xhtml">
                      <head><title>Image-only chapter</title></head>
                      <body><img src="../Images/finale.webp" alt="WebP finale" /></body>
                    </html>
                    """)),
            (
                "OEBPS/Images/page.jpg",
                new byte[]
                {
                    0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46,
                    0x49, 0x46, 0x00, 0x01, 0xFF, 0xD9
                }),
            (
                "OEBPS/Images/diagram.png",
                new byte[]
                {
                    0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                    0x00, 0x00, 0x00, 0x00
                }),
            (
                "OEBPS/Images/finale.webp",
                new byte[]
                {
                    0x52, 0x49, 0x46, 0x46, 0x04, 0x00, 0x00, 0x00,
                    0x57, 0x45, 0x42, 0x50, 0x56, 0x50, 0x38, 0x20
                }));
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.WebTorrentBaseline,
            [new TorrentFileEntry(0, "Book/illustrated.epub", epub.Length, "application/epub+zip")],
            new Dictionary<int, byte[]> { [0] = epub });
        var service = new TorrentMediaSessionService(engine);
        var source = CreateSource(
            CatalogMediaSourceLayout.SingleFile,
            fileIndex: 0,
            filePath: "illustrated.epub") with
        {
            MimeType = "application/epub+zip",
            Container = "EPUB",
            SizeBytes = epub.Length
        };

        using var book = await service.PrepareEpubBookAsync(source);

        Assert.Equal(2, book.Chapters.Count);
        Assert.Equal(
            [
                EpubContentBlockKind.Paragraph,
                EpubContentBlockKind.Image,
                EpubContentBlockKind.Paragraph,
                EpubContentBlockKind.Image
            ],
            book.Chapters[0].Blocks.Select(static block => block.Kind));
        var imageOnlyBlock = Assert.Single(book.Chapters[1].Blocks);
        Assert.Equal(EpubContentBlockKind.Image, imageOnlyBlock.Kind);
        var images = book.Chapters.SelectMany(static chapter => chapter.Images).ToArray();
        Assert.Equal(
            ["image/jpeg", "image/png", "image/webp"],
            images.Select(static image => image.MimeType));
        Assert.Equal(
            ["JPEG page", "PNG diagram", "WebP finale"],
            images.Select(static image => image.AlternativeText));

        foreach (var image in images)
        {
            await using var read = await book.OpenImageReadAsync(image);
            Assert.NotNull(read.Stream);
            Assert.Equal(image.Length, read.Stream.Length);
        }
    }

    [Fact]
    public async Task PrepareEpubBook_RejectsImageBytesThatDoNotMatchManifest()
    {
        var epub = CreateEpubArchive(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>Spoofed image</dc:title>
              </metadata>
              <manifest>
                <item id="chapter" href="Text/chapter.xhtml" media-type="application/xhtml+xml" />
                <item id="image" href="Images/page.png" media-type="image/png" />
              </manifest>
              <spine><itemref idref="chapter" /></spine>
            </package>
            """,
            (
                "OEBPS/Text/chapter.xhtml",
                Encoding.UTF8.GetBytes(
                    """
                    <html xmlns="http://www.w3.org/1999/xhtml">
                      <body><img src="../Images/page.png" alt="Spoofed image" /></body>
                    </html>
                    """)),
            ("OEBPS/Images/page.png", "not a png"u8.ToArray()));
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.WebTorrentBaseline,
            [new TorrentFileEntry(0, "Book/spoofed.epub", epub.Length, "application/epub+zip")],
            new Dictionary<int, byte[]> { [0] = epub });
        var service = new TorrentMediaSessionService(engine);
        var source = CreateSource(
            CatalogMediaSourceLayout.SingleFile,
            fileIndex: 0,
            filePath: "spoofed.epub") with
        {
            MimeType = "application/epub+zip",
            Container = "EPUB",
            SizeBytes = epub.Length
        };

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await service.PrepareEpubBookAsync(source));

        Assert.Contains("does not match", exception.Message);
    }

    [Fact]
    public async Task PrepareEpubBook_RejectsManifestPathOutsideArchiveRoot()
    {
        var epub = CreateEpub(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>Unsafe book</dc:title>
              </metadata>
              <manifest>
                <item id="chapter" href="../../outside.xhtml" media-type="application/xhtml+xml" />
              </manifest>
              <spine><itemref idref="chapter" /></spine>
            </package>
            """,
            (
                "outside.xhtml",
                """
                <html xmlns="http://www.w3.org/1999/xhtml">
                  <body><p>This must not be resolved.</p></body>
                </html>
                """));
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.WebTorrentBaseline,
            [new TorrentFileEntry(0, "Book/unsafe.epub", epub.Length, "application/epub+zip")],
            new Dictionary<int, byte[]> { [0] = epub });
        var service = new TorrentMediaSessionService(engine);
        var source = CreateSource(
            CatalogMediaSourceLayout.SingleFile,
            fileIndex: 0,
            filePath: "unsafe.epub") with
        {
            MimeType = "application/epub+zip",
            Container = "EPUB",
            SizeBytes = epub.Length
        };

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await service.PrepareEpubBookAsync(source));

        Assert.Contains("escapes the archive root", exception.Message);
    }

    [Fact]
    public async Task PrepareSource_ReusesSessionPreviouslyOpenedByMagnetIntake()
    {
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.WebTorrentBaseline,
            [new TorrentFileEntry(1, "Release/video/movie.webm", 900, "video/webm")]);
        var registry = new TorrentSessionRegistry();
        var repository = new InMemoryCatalogRepository(new CatalogTransferDocument());
        var intake = new MagnetOpenService(
            engine,
            new CatalogJsonTransferService(repository),
            new InMemoryMagnetOpenPreferencesStore(new MagnetOpenPreferences(
                OpenAutomatically: true,
                ImportInfoJson: false,
                TreatExternalMagnetsAsPrivate: true)),
            registry);
        var media = new TorrentMediaSessionService(engine, registry);
        var magnet = $"magnet:?xt=urn:btih:{InfoHash}" +
            "&tr=wss%3A%2F%2Fbrowser.example.test%2Fannounce";

        var opened = await intake.ReceiveAsync(magnet);
        var prepared = await media.PrepareSingleFileAsync(CreateSource(
            CatalogMediaSourceLayout.SingleFile,
            fileIndex: 1,
            filePath: "video/movie.webm"));

        Assert.Equal(MagnetOpenStatus.Opened, opened.Status);
        Assert.Equal(1, prepared.FileIndex);
        Assert.Equal(1, engine.AddCount);
    }

    [Fact]
    public async Task PreparePrivateSource_RejectsExistingPublicSession()
    {
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.WebTorrentBaseline,
            [new TorrentFileEntry(1, "Release/video/movie.webm", 900, "video/webm")]);
        var registry = new TorrentSessionRegistry();
        var repository = new InMemoryCatalogRepository(new CatalogTransferDocument());
        var intake = new MagnetOpenService(
            engine,
            new CatalogJsonTransferService(repository),
            new InMemoryMagnetOpenPreferencesStore(new MagnetOpenPreferences(
                OpenAutomatically: true,
                ImportInfoJson: false,
                TreatExternalMagnetsAsPrivate: false)),
            registry);
        var media = new TorrentMediaSessionService(engine, registry);
        var magnet = $"magnet:?xt=urn:btih:{InfoHash}" +
            "&tr=wss%3A%2F%2Fbrowser.example.test%2Fannounce";
        _ = await intake.ReceiveAsync(magnet);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await media.PrepareSingleFileAsync(CreateSource(
                CatalogMediaSourceLayout.SingleFile,
                fileIndex: 1,
                filePath: "video/movie.webm")));

        Assert.Contains("public torrent session", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, engine.AddCount);
    }

    [Fact]
    public async Task PrepareSource_ReusesContextAcrossV1AndHybridIdentityAliases()
    {
        const string infoHashV2 =
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var engine = new FakeTorrentEngine(
            TorrentEngineCapabilities.WebTorrentBaseline,
            [new TorrentFileEntry(1, "Release/video/movie.webm", 900, "video/webm")]);
        var service = new TorrentMediaSessionService(engine);
        var v1Source = CreateSource(
            CatalogMediaSourceLayout.SingleFile,
            fileIndex: 1,
            filePath: "video/movie.webm");
        _ = await service.PrepareSingleFileAsync(v1Source);
        var priorityChangeCount = engine.PriorityChanges.Count;
        var hybridSource = v1Source with
        {
            Torrent = v1Source.Torrent with
            {
                InfoHashV2 = infoHashV2,
                MagnetUri = $"magnet:?xt=urn:btih:{InfoHash}" +
                    $"&xt=urn:btmh:1220{infoHashV2}"
            }
        };

        _ = await service.PrepareSingleFileAsync(hybridSource);

        Assert.Equal(1, engine.AddCount);
        Assert.Equal(priorityChangeCount, engine.PriorityChanges.Count);
    }

    private static CatalogMediaSourceDetails CreateSource(
        CatalogMediaSourceLayout layout,
        int? fileIndex,
        string filePath) =>
        new(
            "primary",
            layout,
            fileIndex,
            filePath,
            layout is CatalogMediaSourceLayout.ImageSequence ? "image/webp" : "video/webm",
            null,
            null,
            null,
            null,
            0,
            new CatalogTorrentDetails(
                "Test release",
                $"magnet:?xt=urn:btih:{InfoHash}",
                ReadOnlyMemory<byte>.Empty,
                InfoHash,
                null,
                true,
                null,
                null,
                [
                    new CatalogTrackerDetails(
                        "Browser",
                        new Uri("wss://browser.example.test/announce"),
                        "wss",
                        "browser",
                        false,
                        0,
                        0),
                    new CatalogTrackerDetails(
                        "Native",
                        new Uri("https://native.example.test/announce"),
                        "https",
                        "native",
                        false,
                        0,
                        1),
                    new CatalogTrackerDetails(
                        "UDP",
                        new Uri("udp://public.example.test:80/announce"),
                        "udp",
                        "native",
                        false,
                        0,
                        2)
                ]));

    private static byte[] CreateCbz(params (string Path, byte[] Content)[] files)
    {
        using var destination = new MemoryStream();
        using (var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Path, CompressionLevel.Fastest);
                using var stream = entry.Open();
                stream.Write(file.Content);
            }
        }

        return destination.ToArray();
    }

    private static byte[] CreateEpub(
        string packageDocument,
        params (string Path, string Content)[] files)
    {
        return CreateEpubArchive(
            packageDocument,
            files
                .Select(static file => (file.Path, Encoding.UTF8.GetBytes(file.Content)))
                .ToArray());
    }

    private static byte[] CreateEpubArchive(
        string packageDocument,
        params (string Path, byte[] Content)[] files)
    {
        const string containerDocument = """
            <?xml version="1.0" encoding="utf-8"?>
            <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
              <rootfiles>
                <rootfile full-path="OEBPS/content.opf"
                          media-type="application/oebps-package+xml" />
              </rootfiles>
            </container>
            """;
        using var destination = new MemoryStream();
        using (var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteTextEntry(archive, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            WriteTextEntry(archive, "META-INF/container.xml", containerDocument);
            WriteTextEntry(archive, "OEBPS/content.opf", packageDocument);
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Path, CompressionLevel.Fastest);
                using var stream = entry.Open();
                stream.Write(file.Content);
            }
        }

        return destination.ToArray();
    }

    private static void WriteTextEntry(
        ZipArchive archive,
        string path,
        string content,
        CompressionLevel compressionLevel = CompressionLevel.Fastest)
    {
        var entry = archive.CreateEntry(path, compressionLevel);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.Write(content);
    }
}
