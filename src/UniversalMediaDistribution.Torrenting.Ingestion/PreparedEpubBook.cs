using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace UniversalMediaDistribution.Torrenting.Ingestion;

public enum EpubContentBlockKind
{
    Paragraph = 1,
    Heading = 2,
    ListItem = 3,
    Image = 4
}

public sealed record PreparedEpubImage(
    int EntryIndex,
    string Path,
    string MimeType,
    long Length,
    string? AlternativeText);

public sealed record PreparedEpubContentBlock(
    int Position,
    EpubContentBlockKind Kind,
    string? Text,
    PreparedEpubImage? Image);

public sealed record PreparedEpubChapter(
    int Position,
    string Path,
    string Title,
    IReadOnlyList<PreparedEpubContentBlock> Blocks)
{
    /// <summary>
    /// Built once at construction. A chapter can carry thousands of blocks,
    /// and the loader already pays for this string to enforce the book-wide
    /// text budget; rebuilding it on every read allocated the whole chapter
    /// again. Eager rather than lazy so the record's value equality is not
    /// made to depend on whether a cache field has been populated.
    /// </summary>
    public string Text { get; } = string.Join(
        Environment.NewLine + Environment.NewLine,
        Blocks
            .Where(static block => block.Kind is not EpubContentBlockKind.Image)
            .Select(static block => block.Kind is EpubContentBlockKind.ListItem
                ? "• " + block.Text
                : block.Text)
            .Where(static text => !string.IsNullOrWhiteSpace(text)));

    public IReadOnlyList<PreparedEpubImage> Images => Blocks
        .Where(static block => block.Image is not null)
        .Select(static block => block.Image!)
        .ToArray();
}

public sealed class PreparedEpubBook : IDisposable
{
    private const long MaximumArchiveBytes = 256L * 1024 * 1024;
    private const long MaximumExpandedBytes = 1024L * 1024 * 1024;
    private const long MaximumEntryBytes = 128L * 1024 * 1024;
    private const long MaximumDocumentBytes = 4L * 1024 * 1024;
    private const long MaximumImageBytes = 96L * 1024 * 1024;
    private const long MaximumBookImageBytes = 512L * 1024 * 1024;
    private const long MaximumCompressionRatio = 200;
    private const int MaximumEntryCount = 8192;
    private const int MaximumChapterCount = 2048;
    private const int MaximumImageCount = 4096;
    private const int MaximumBlocksPerChapter = 8192;
    private const int MaximumDocumentCharacters = 4 * 1024 * 1024;
    private const int MaximumBookCharacters = 32 * 1024 * 1024;

    private static readonly HashSet<string> TextBlockElements = new(
        [
            "address", "dd", "dt", "figcaption", "h1", "h2", "h3", "h4", "h5", "h6",
            "li", "p", "pre", "td", "th"
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly MemoryStream? _archiveBuffer;
    private readonly ZipArchive? _archive;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly IReadOnlyDictionary<int, ZipArchiveEntry> _imageEntriesByIndex;
    private bool _disposed;

    private PreparedEpubBook(
        PreparedTorrentFile file,
        string? packageTitle,
        string? language,
        IReadOnlyList<PreparedEpubChapter> chapters,
        MemoryStream? archiveBuffer,
        ZipArchive? archive,
        IReadOnlyDictionary<int, ZipArchiveEntry> imageEntriesByIndex)
    {
        File = file;
        PackageTitle = packageTitle;
        Language = language;
        Chapters = chapters;
        _archiveBuffer = archiveBuffer;
        _archive = archive;
        _imageEntriesByIndex = imageEntriesByIndex;
    }

    public PreparedTorrentFile File { get; }

    public string? PackageTitle { get; }

    public string? Language { get; }

    public IReadOnlyList<PreparedEpubChapter> Chapters { get; }

    internal static async ValueTask<PreparedEpubBook> CreateAsync(
        PreparedTorrentFile file,
        IMediaReadHandle readHandle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(readHandle);
        if (file.Length > MaximumArchiveBytes)
        {
            throw new InvalidOperationException("The EPUB exceeds the 256 MiB reader limit.");
        }

        var source = readHandle.Stream ??
            throw new InvalidOperationException("The torrent engine did not provide an EPUB stream.");
        var capacity = file.Length is > 0 and <= int.MaxValue ? (int)file.Length : 0;
        var buffer = new MemoryStream(capacity);
        try
        {
            await CopyBoundedAsync(source, buffer, MaximumArchiveBytes, cancellationToken);
            buffer.Position = 0;
            var archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
            try
            {
                var entries = ValidateEntries(archive);
                await ValidateMimeTypeAsync(entries, cancellationToken);

                var containerEntry = GetRequiredEntry(entries, "META-INF/container.xml");
                var container = await ReadXmlAsync(containerEntry, cancellationToken);
                var packagePath = container
                    .Descendants()
                    .FirstOrDefault(static element =>
                        element.Name.LocalName.Equals("rootfile", StringComparison.OrdinalIgnoreCase))
                    ?.Attributes()
                    .FirstOrDefault(static attribute =>
                        attribute.Name.LocalName.Equals("full-path", StringComparison.OrdinalIgnoreCase))
                    ?.Value;
                if (string.IsNullOrWhiteSpace(packagePath))
                {
                    throw new InvalidDataException(
                        "The EPUB container does not identify its package document.");
                }

                packagePath = NormalizeEntryPath(packagePath);
                var packageEntry = GetRequiredEntry(entries, packagePath);
                var package = await ReadXmlAsync(packageEntry, cancellationToken);
                var packageTitle = FindMetadataValue(package, "title");
                var language = FindMetadataValue(package, "language");
                var manifestItems = ReadManifest(package);
                var imageResources = await ReadImageResourcesAsync(
                    packagePath,
                    manifestItems,
                    entries,
                    archive,
                    cancellationToken);
                var spineIds = ReadSpine(package);
                if (spineIds.Count == 0)
                {
                    throw new InvalidDataException(
                        "The EPUB package has no reading-order entries.");
                }

                if (spineIds.Count > MaximumChapterCount)
                {
                    throw new InvalidDataException(
                        $"The EPUB exceeds {MaximumChapterCount} reading-order entries.");
                }

                var chapters = new List<PreparedEpubChapter>(spineIds.Count);
                var chapterPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var totalCharacters = 0;
                foreach (var id in spineIds)
                {
                    if (!manifestItems.TryGetValue(id, out var item))
                    {
                        throw new InvalidDataException(
                            $"The EPUB spine references missing manifest item '{id}'.");
                    }

                    if (!IsSupportedContentType(item.MediaType))
                    {
                        continue;
                    }

                    var chapterPath = ResolveArchivePath(packagePath, item.Href);
                    if (!chapterPaths.Add(chapterPath))
                    {
                        throw new InvalidDataException(
                            $"The EPUB reading order contains duplicate document '{chapterPath}'.");
                    }

                    var chapterEntry = GetRequiredEntry(entries, chapterPath);
                    var document = await ReadXmlAsync(chapterEntry, cancellationToken);
                    var chapter = ReadChapter(
                        document,
                        chapters.Count,
                        chapterPath,
                        imageResources);
                    totalCharacters = checked(totalCharacters + chapter.Text.Length);
                    if (totalCharacters > MaximumBookCharacters)
                    {
                        throw new InvalidDataException(
                            "The EPUB exceeds the 32 MiB extracted-text reader limit.");
                    }

                    chapters.Add(chapter);
                }

                if (chapters.Count == 0)
                {
                    throw new InvalidDataException(
                        "The EPUB reading order contains no supported XHTML documents.");
                }

                var referencedImages = chapters
                    .SelectMany(static chapter => chapter.Images)
                    .DistinctBy(static image => image.EntryIndex)
                    .ToArray();
                var imageEntriesByIndex = referencedImages
                    .ToDictionary(
                        static image => image.EntryIndex,
                        image => archive.Entries[image.EntryIndex]);
                if (referencedImages.Length == 0)
                {
                    archive.Dispose();
                    buffer.Dispose();
                    return new PreparedEpubBook(
                        file,
                        packageTitle,
                        language,
                        chapters,
                        archiveBuffer: null,
                        archive: null,
                        imageEntriesByIndex);
                }

                return new PreparedEpubBook(
                    file,
                    packageTitle,
                    language,
                    chapters,
                    buffer,
                    archive,
                    imageEntriesByIndex);
            }
            catch
            {
                archive.Dispose();
                throw;
            }
        }
        catch
        {
            await buffer.DisposeAsync();
            throw;
        }
    }

    public async ValueTask<IMediaReadHandle> OpenImageReadAsync(
        PreparedEpubImage image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_imageEntriesByIndex.TryGetValue(image.EntryIndex, out var entry) ||
            !string.Equals(
                NormalizeEntryPath(entry.FullName),
                image.Path,
                StringComparison.OrdinalIgnoreCase) ||
            entry.Length != image.Length)
        {
            throw new ArgumentException(
                "The image does not belong to this EPUB.",
                nameof(image));
        }

        await _readGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var capacity = entry.Length is > 0 and <= int.MaxValue ? (int)entry.Length : 0;
            var destination = new MemoryStream(capacity);
            try
            {
                await using var source = entry.Open();
                await CopyBoundedAsync(
                    source,
                    destination,
                    MaximumImageBytes,
                    cancellationToken);
                if (destination.Length != entry.Length)
                {
                    throw new InvalidDataException(
                        $"EPUB image '{image.Path}' did not expand to its declared length.");
                }

                destination.Position = 0;
                return new EpubImageReadHandle(destination);
            }
            catch
            {
                await destination.DisposeAsync();
                throw;
            }
        }
        finally
        {
            _readGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // OpenImageReadAsync holds the read gate while it streams an entry out
        // of the shared buffer. Taking it here drains any read already past its
        // disposed check, instead of pulling the archive out from under it and
        // surfacing ObjectDisposedException on the reader's thread. The gate
        // itself is not disposed: a caller queued in WaitAsync would fault
        // there, which is the same defect one layer down.
        _readGate.Wait();
        try
        {
            _archive?.Dispose();
            _archiveBuffer?.Dispose();
        }
        finally
        {
            _readGate.Release();
        }

        GC.SuppressFinalize(this);
    }

    private static IReadOnlyDictionary<string, ZipArchiveEntry> ValidateEntries(
        ZipArchive archive)
    {
        if (archive.Entries.Count > MaximumEntryCount)
        {
            throw new InvalidDataException(
                $"The EPUB exceeds {MaximumEntryCount} archive entries.");
        }

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;
        foreach (var entry in archive.Entries.Where(static entry => entry.Name.Length > 0))
        {
            var path = NormalizeEntryPath(entry.FullName);
            if (!entries.TryAdd(path, entry))
            {
                throw new InvalidDataException(
                    $"The EPUB contains duplicate archive path '{path}'.");
            }

            if (entry.Length < 0 || entry.Length > MaximumEntryBytes)
            {
                throw new InvalidDataException(
                    $"EPUB entry '{path}' has an invalid or oversized expanded length.");
            }

            try
            {
                expandedBytes = checked(expandedBytes + entry.Length);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException(
                    "The EPUB expanded-content size is invalid.",
                    exception);
            }

            if (expandedBytes > MaximumExpandedBytes)
            {
                throw new InvalidDataException(
                    "The EPUB exceeds the 1 GiB expanded-content limit.");
            }

            if (entry.Length > 0 &&
                (entry.CompressedLength <= 0 ||
                 entry.Length / entry.CompressedLength > MaximumCompressionRatio))
            {
                throw new InvalidDataException(
                    $"EPUB entry '{path}' exceeds the allowed compression ratio.");
            }
        }

        return entries;
    }

    private static async ValueTask ValidateMimeTypeAsync(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        CancellationToken cancellationToken)
    {
        var mimeTypeEntry = GetRequiredEntry(entries, "mimetype");
        if (mimeTypeEntry.Length is <= 0 or > 64)
        {
            throw new InvalidDataException("The EPUB mimetype entry is invalid.");
        }

        var bytes = await ReadEntryAsync(mimeTypeEntry, 64, cancellationToken);
        string mimeType;
        try
        {
            mimeType = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The EPUB mimetype entry is not valid UTF-8.", exception);
        }

        if (!string.Equals(
                mimeType.Trim(),
                "application/epub+zip",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The selected archive is not an application/epub+zip document.");
        }
    }

    private static async ValueTask<XDocument> ReadXmlAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.Length is <= 0 or > MaximumDocumentBytes)
        {
            throw new InvalidDataException(
                $"EPUB document '{entry.FullName}' is empty or exceeds 4 MiB.");
        }

        await using var source = entry.Open();
        using var reader = XmlReader.Create(
            source,
            new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                MaxCharactersFromEntities = 1024,
                MaxCharactersInDocument = MaximumDocumentCharacters,
                XmlResolver = null
            });
        try
        {
            return await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException(
                $"EPUB document '{entry.FullName}' is not safe, valid XML.",
                exception);
        }
    }

    private static IReadOnlyDictionary<string, ManifestItem> ReadManifest(XDocument package)
    {
        var manifest = package
            .Descendants()
            .FirstOrDefault(static element =>
                element.Name.LocalName.Equals("manifest", StringComparison.OrdinalIgnoreCase)) ??
            throw new InvalidDataException("The EPUB package has no manifest.");
        var items = new Dictionary<string, ManifestItem>(StringComparer.Ordinal);
        foreach (var element in manifest.Elements().Where(static element =>
                     element.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase)))
        {
            var id = GetAttribute(element, "id");
            var href = GetAttribute(element, "href");
            var mediaType = GetAttribute(element, "media-type");
            if (string.IsNullOrWhiteSpace(id) ||
                string.IsNullOrWhiteSpace(href) ||
                string.IsNullOrWhiteSpace(mediaType))
            {
                throw new InvalidDataException(
                    "Every EPUB manifest item must define id, href, and media-type.");
            }

            if (!items.TryAdd(id, new ManifestItem(href, mediaType)))
            {
                throw new InvalidDataException(
                    $"The EPUB manifest contains duplicate id '{id}'.");
            }
        }

        return items;
    }

    private static async ValueTask<IReadOnlyDictionary<string, PreparedEpubImage>>
        ReadImageResourcesAsync(
            string packagePath,
            IReadOnlyDictionary<string, ManifestItem> manifestItems,
            IReadOnlyDictionary<string, ZipArchiveEntry> entries,
            ZipArchive archive,
            CancellationToken cancellationToken)
    {
        var resources = new Dictionary<string, PreparedEpubImage>(
            StringComparer.OrdinalIgnoreCase);

        // Number the archive once rather than scanning it for every image;
        // IndexOf per resource is linear over the whole entry collection.
        var entryPositions = new Dictionary<ZipArchiveEntry, int>(
            archive.Entries.Count);
        for (var index = 0; index < archive.Entries.Count; index++)
        {
            entryPositions[archive.Entries[index]] = index;
        }

        long totalImageBytes = 0;
        foreach (var item in manifestItems.Values.Where(static item =>
                     IsSupportedImageMimeType(item.MediaType)))
        {
            var path = ResolveArchivePath(packagePath, item.Href);
            var expectedMimeType = GetImageMimeTypeFromPath(path);
            if (expectedMimeType is null ||
                !string.Equals(expectedMimeType, item.MediaType, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"EPUB image '{path}' must be a JPG/JPEG, PNG, or WebP file " +
                    "whose extension matches its manifest media type.");
            }

            var entry = GetRequiredEntry(entries, path);
            if (entry.Length is <= 0 or > MaximumImageBytes)
            {
                throw new InvalidDataException(
                    $"EPUB image '{path}' is empty or exceeds 96 MiB.");
            }

            if (resources.Count >= MaximumImageCount)
            {
                throw new InvalidDataException(
                    $"The EPUB exceeds {MaximumImageCount} supported images.");
            }

            totalImageBytes = checked(totalImageBytes + entry.Length);
            if (totalImageBytes > MaximumBookImageBytes)
            {
                throw new InvalidDataException(
                    "The EPUB exceeds the 512 MiB supported-image limit.");
            }

            await ValidateImageSignatureAsync(entry, expectedMimeType, cancellationToken);
            var image = new PreparedEpubImage(
                entryPositions[entry],
                path,
                expectedMimeType,
                entry.Length,
                AlternativeText: null);
            if (!resources.TryAdd(path, image))
            {
                throw new InvalidDataException(
                    $"The EPUB manifest maps more than one image to '{path}'.");
            }
        }

        return resources;
    }

    private static IReadOnlyList<string> ReadSpine(XDocument package)
    {
        var spine = package
            .Descendants()
            .FirstOrDefault(static element =>
                element.Name.LocalName.Equals("spine", StringComparison.OrdinalIgnoreCase)) ??
            throw new InvalidDataException("The EPUB package has no spine.");
        var ids = new List<string>();
        foreach (var element in spine.Elements().Where(static element =>
                     element.Name.LocalName.Equals("itemref", StringComparison.OrdinalIgnoreCase)))
        {
            var id = GetAttribute(element, "idref");
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidDataException(
                    "Every EPUB spine item must define an idref.");
            }

            ids.Add(id);
        }

        return ids;
    }

    private static PreparedEpubChapter ReadChapter(
        XDocument document,
        int position,
        string path,
        IReadOnlyDictionary<string, PreparedEpubImage> imageResources)
    {
        var body = document
            .Descendants()
            .FirstOrDefault(static element =>
                element.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase)) ??
            throw new InvalidDataException($"EPUB chapter '{path}' has no body.");
        var blocks = new List<PreparedEpubContentBlock>();
        var inlineText = new StringBuilder();
        AppendFlowNodes(
            body.Nodes(),
            path,
            imageResources,
            blocks,
            inlineText);
        FlushTextBlock(blocks, inlineText, EpubContentBlockKind.Paragraph);

        if (blocks.Count == 0)
        {
            throw new InvalidDataException(
                $"EPUB chapter '{path}' contains no readable text or supported images.");
        }

        var title = body
            .Descendants()
            .FirstOrDefault(static element => IsHeading(element.Name.LocalName))
            is { } heading
            ? ExtractReadableText(heading)
            : string.Empty;
        if (title.Length == 0)
        {
            title = document
                .Descendants()
                .FirstOrDefault(static element =>
                    element.Name.LocalName.Equals("title", StringComparison.OrdinalIgnoreCase))
                is { } titleElement
                ? NormalizeWhitespace(titleElement.Value)
                : string.Empty;
        }

        if (title.Length == 0)
        {
            title = Path.GetFileNameWithoutExtension(path);
        }

        return new PreparedEpubChapter(position, path, title, blocks);
    }

    private static void AppendFlowNodes(
        IEnumerable<XNode> nodes,
        string chapterPath,
        IReadOnlyDictionary<string, PreparedEpubImage> imageResources,
        List<PreparedEpubContentBlock> blocks,
        StringBuilder inlineText)
    {
        foreach (var node in nodes)
        {
            if (node is XText text)
            {
                inlineText.Append(text.Value);
                continue;
            }

            if (node is not XElement element || IsExcludedElement(element.Name.LocalName))
            {
                continue;
            }

            var localName = element.Name.LocalName;
            if (localName.Equals("br", StringComparison.OrdinalIgnoreCase))
            {
                inlineText.Append(' ');
            }
            else if (localName.Equals("img", StringComparison.OrdinalIgnoreCase))
            {
                FlushTextBlock(blocks, inlineText, EpubContentBlockKind.Paragraph);
                AddImageBlock(blocks, element, chapterPath, imageResources);
            }
            else if (TextBlockElements.Contains(localName))
            {
                FlushTextBlock(blocks, inlineText, EpubContentBlockKind.Paragraph);
                AppendTextElement(
                    element,
                    GetTextBlockKind(localName),
                    chapterPath,
                    imageResources,
                    blocks);
            }
            else
            {
                AppendFlowNodes(
                    element.Nodes(),
                    chapterPath,
                    imageResources,
                    blocks,
                    inlineText);
            }
        }
    }

    private static void AppendTextElement(
        XElement element,
        EpubContentBlockKind kind,
        string chapterPath,
        IReadOnlyDictionary<string, PreparedEpubImage> imageResources,
        List<PreparedEpubContentBlock> blocks)
    {
        var text = new StringBuilder();
        foreach (var node in element.Nodes())
        {
            if (node is XText textNode)
            {
                text.Append(textNode.Value);
                continue;
            }

            if (node is not XElement child || IsExcludedElement(child.Name.LocalName))
            {
                continue;
            }

            var localName = child.Name.LocalName;
            if (localName.Equals("br", StringComparison.OrdinalIgnoreCase))
            {
                text.Append(' ');
            }
            else if (localName.Equals("img", StringComparison.OrdinalIgnoreCase))
            {
                FlushTextBlock(blocks, text, kind);
                AddImageBlock(blocks, child, chapterPath, imageResources);
            }
            else
            {
                if (TextBlockElements.Contains(localName) && text.Length > 0)
                {
                    text.Append(' ');
                }

                AppendInlineNodes(
                    child.Nodes(),
                    kind,
                    chapterPath,
                    imageResources,
                    blocks,
                    text);

                if (TextBlockElements.Contains(localName))
                {
                    text.Append(' ');
                }
            }
        }

        FlushTextBlock(blocks, text, kind);
    }

    private static void AppendInlineNodes(
        IEnumerable<XNode> nodes,
        EpubContentBlockKind kind,
        string chapterPath,
        IReadOnlyDictionary<string, PreparedEpubImage> imageResources,
        List<PreparedEpubContentBlock> blocks,
        StringBuilder text)
    {
        foreach (var node in nodes)
        {
            if (node is XText textNode)
            {
                text.Append(textNode.Value);
                continue;
            }

            if (node is not XElement child || IsExcludedElement(child.Name.LocalName))
            {
                continue;
            }

            if (child.Name.LocalName.Equals("br", StringComparison.OrdinalIgnoreCase))
            {
                text.Append(' ');
            }
            else if (child.Name.LocalName.Equals("img", StringComparison.OrdinalIgnoreCase))
            {
                FlushTextBlock(blocks, text, kind);
                AddImageBlock(blocks, child, chapterPath, imageResources);
            }
            else
            {
                AppendInlineNodes(
                    child.Nodes(),
                    kind,
                    chapterPath,
                    imageResources,
                    blocks,
                    text);
            }
        }
    }

    private static void AddImageBlock(
        List<PreparedEpubContentBlock> blocks,
        XElement element,
        string chapterPath,
        IReadOnlyDictionary<string, PreparedEpubImage> imageResources)
    {
        var source = GetAttribute(element, "src");
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new InvalidDataException(
                $"EPUB chapter '{chapterPath}' contains an image without a source.");
        }

        var imagePath = ResolveArchivePath(chapterPath, source);
        if (!imageResources.TryGetValue(imagePath, out var image))
        {
            throw new InvalidDataException(
                $"EPUB image '{imagePath}' is not a manifest-declared JPG/JPEG, PNG, or WebP resource.");
        }

        var alternativeText = NormalizeWhitespace(GetAttribute(element, "alt") ?? string.Empty);
        AddBlock(
            blocks,
            EpubContentBlockKind.Image,
            text: null,
            image with
            {
                AlternativeText = alternativeText.Length == 0 ? null : alternativeText
            });
    }

    private static void FlushTextBlock(
        List<PreparedEpubContentBlock> blocks,
        StringBuilder text,
        EpubContentBlockKind kind)
    {
        var normalized = NormalizeWhitespace(text.ToString());
        text.Clear();
        if (normalized.Length > 0)
        {
            AddBlock(blocks, kind, normalized, image: null);
        }
    }

    private static void AddBlock(
        List<PreparedEpubContentBlock> blocks,
        EpubContentBlockKind kind,
        string? text,
        PreparedEpubImage? image)
    {
        if (blocks.Count >= MaximumBlocksPerChapter)
        {
            throw new InvalidDataException(
                $"An EPUB chapter exceeds {MaximumBlocksPerChapter} content blocks.");
        }

        blocks.Add(new PreparedEpubContentBlock(blocks.Count, kind, text, image));
    }

    private static EpubContentBlockKind GetTextBlockKind(string localName)
    {
        if (IsHeading(localName))
        {
            return EpubContentBlockKind.Heading;
        }

        return localName.Equals("li", StringComparison.OrdinalIgnoreCase)
            ? EpubContentBlockKind.ListItem
            : EpubContentBlockKind.Paragraph;
    }

    private static string ExtractReadableText(XElement element)
    {
        var builder = new StringBuilder();
        AppendReadableNodes(element.Nodes(), builder);
        return NormalizeWhitespace(builder.ToString());
    }

    private static void AppendReadableNodes(IEnumerable<XNode> nodes, StringBuilder builder)
    {
        foreach (var node in nodes)
        {
            if (node is XText text)
            {
                builder.Append(text.Value);
                continue;
            }

            if (node is not XElement element || IsExcludedElement(element.Name.LocalName))
            {
                continue;
            }

            var localName = element.Name.LocalName;
            if (localName.Equals("br", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append(' ');
                continue;
            }

            if (localName.Equals("img", StringComparison.OrdinalIgnoreCase))
            {
                var alternativeText = GetAttribute(element, "alt");
                if (!string.IsNullOrWhiteSpace(alternativeText))
                {
                    builder.Append(' ');
                    builder.Append(alternativeText);
                    builder.Append(' ');
                }

                continue;
            }

            AppendReadableNodes(element.Nodes(), builder);
        }
    }

    private static bool IsExcludedElement(string localName) =>
        localName.Equals("script", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("style", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("template", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("svg", StringComparison.OrdinalIgnoreCase);

    private static bool IsHeading(string localName) =>
        localName.Equals("h1", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("h2", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("h3", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("h4", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("h5", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("h6", StringComparison.OrdinalIgnoreCase);

    private static string? FindMetadataValue(XDocument package, string localName)
    {
        var metadata = package
            .Descendants()
            .FirstOrDefault(static element =>
                element.Name.LocalName.Equals("metadata", StringComparison.OrdinalIgnoreCase));
        var value = metadata?
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            ?.Value;
        var normalized = NormalizeWhitespace(value ?? string.Empty);
        return normalized.Length == 0 ? null : normalized;
    }

    private static string? GetAttribute(XElement element, string localName) =>
        element.Attributes()
            .FirstOrDefault(attribute =>
                attribute.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            ?.Value
            .Trim();

    private static bool IsSupportedContentType(string value) =>
        value.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("text/html", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedImageMimeType(string value) =>
        value.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("image/png", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("image/webp", StringComparison.OrdinalIgnoreCase);

    private static string? GetImageMimeTypeFromPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => null
        };

    private static ZipArchiveEntry GetRequiredEntry(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string path) =>
        entries.TryGetValue(path, out var entry)
            ? entry
            : throw new InvalidDataException($"The EPUB is missing required entry '{path}'.");

    private static string ResolveArchivePath(string documentPath, string href)
    {
        var value = href.Trim().Replace('\\', '/');
        var queryOrFragment = value.IndexOfAny(['?', '#']);
        if (queryOrFragment >= 0)
        {
            value = value[..queryOrFragment];
        }

        try
        {
            value = Uri.UnescapeDataString(value);
        }
        catch (UriFormatException exception)
        {
            throw new InvalidDataException(
                $"The EPUB contains invalid resource path '{href}'.",
                exception);
        }

        if (value.Length == 0 ||
            value.StartsWith('/') ||
            value.StartsWith("//", StringComparison.Ordinal) ||
            Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            throw new InvalidDataException(
                $"The EPUB contains external or unsafe resource path '{href}'.");
        }

        var separator = documentPath.LastIndexOf('/');
        var baseDirectory = separator < 0 ? string.Empty : documentPath[..separator];
        var segments = new List<string>();
        if (baseDirectory.Length > 0)
        {
            segments.AddRange(baseDirectory.Split('/'));
        }

        foreach (var segment in value.Split('/', StringSplitOptions.None))
        {
            if (segment.Length == 0)
            {
                throw new InvalidDataException(
                    $"The EPUB contains unsafe resource path '{href}'.");
            }

            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    throw new InvalidDataException(
                        $"The EPUB resource path '{href}' escapes the archive root.");
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            ValidatePathSegment(segment);
            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            throw new InvalidDataException(
                $"The EPUB contains empty resource path '{href}'.");
        }

        return string.Join('/', segments);
    }

    private static string NormalizeEntryPath(string value)
    {
        var path = value.Trim().Replace('\\', '/').Trim('/');
        if (path.Length == 0 || Path.IsPathRooted(value))
        {
            throw new InvalidDataException("The EPUB contains an unsafe archive path.");
        }

        var segments = path.Split('/', StringSplitOptions.None);
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                throw new InvalidDataException("The EPUB contains an unsafe archive path.");
            }

            ValidatePathSegment(segment);
        }

        return string.Join('/', segments);
    }

    private static void ValidatePathSegment(string segment)
    {
        if (segment.Contains(':') || segment.Any(char.IsControl))
        {
            throw new InvalidDataException("The EPUB contains an unsafe archive path.");
        }
    }

    private static string NormalizeWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString().Trim();
    }

    private static async ValueTask ValidateImageSignatureAsync(
        ZipArchiveEntry entry,
        string mimeType,
        CancellationToken cancellationToken)
    {
        await using var source = entry.Open();
        var header = new byte[12];
        var length = 0;
        while (length < header.Length)
        {
            var read = await source.ReadAsync(
                header.AsMemory(length, header.Length - length),
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        var isValid = mimeType switch
        {
            "image/jpeg" =>
                length >= 3 &&
                header[0] == 0xFF &&
                header[1] == 0xD8 &&
                header[2] == 0xFF,
            "image/png" =>
                length >= 8 &&
                header[0] == 0x89 &&
                header[1] == 0x50 &&
                header[2] == 0x4E &&
                header[3] == 0x47 &&
                header[4] == 0x0D &&
                header[5] == 0x0A &&
                header[6] == 0x1A &&
                header[7] == 0x0A,
            "image/webp" =>
                length >= 12 &&
                header.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
                header.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _ => false
        };
        if (!isValid)
        {
            throw new InvalidDataException(
                $"EPUB image '{entry.FullName}' does not match its declared {mimeType} format.");
        }
    }

    private static async ValueTask<byte[]> ReadEntryAsync(
        ZipArchiveEntry entry,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var capacity = entry.Length is > 0 and <= int.MaxValue ? (int)entry.Length : 0;
        await using var destination = new MemoryStream(capacity);
        await using var source = entry.Open();
        await CopyBoundedAsync(source, destination, maximumBytes, cancellationToken);
        if (destination.Length != entry.Length)
        {
            throw new InvalidDataException(
                $"EPUB entry '{entry.FullName}' did not expand to its declared length.");
        }

        return destination.ToArray();
    }

    private static async ValueTask CopyBoundedAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var copyBuffer = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(copyBuffer, cancellationToken);
            if (read == 0)
            {
                return;
            }

            if (destination.Length + read > maximumBytes)
            {
                throw new InvalidDataException(
                    "The EPUB data exceeds its configured reader limit.");
            }

            await destination.WriteAsync(copyBuffer.AsMemory(0, read), cancellationToken);
        }
    }

    private sealed record ManifestItem(string Href, string MediaType);

    private sealed class EpubImageReadHandle(Stream stream) : IMediaReadHandle
    {
        public Stream? Stream { get; } = stream;

        public Uri? PlaybackUri => null;

        public async ValueTask DisposeAsync()
        {
            if (Stream is not null)
            {
                await Stream.DisposeAsync();
            }
        }
    }
}
