using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Reading;
using UniversalMediaDistribution.Torrenting;

namespace UniversalMediaDistribution.Torrenting.Ingestion;

public sealed class TorrentLocalCatalogImporter(CatalogJsonTransferService catalogTransferService)
{
    public const string ManifestFileName = "info.json";
    public const int MaximumManifestBytes = 4 * 1024 * 1024;

    public async ValueTask<TorrentManifestImportResult> TryImportAsync(
        ITorrentEngine engine,
        TorrentSessionId sessionId,
        MagnetLink magnetLink,
        bool privateMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(magnetLink);

        var files = await engine.GetFilesAsync(sessionId, cancellationToken);
        var normalizedFiles = ValidateAndNormalizeFiles(files);
        var manifestFiles = normalizedFiles
            .Where(static file => string.Equals(
                GetFileName(file.NormalizedPath),
                ManifestFileName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (manifestFiles.Length == 0)
        {
            return TorrentManifestImportResult.NotFound;
        }

        if (manifestFiles.Length > 1)
        {
            throw new TorrentManifestValidationException(
                ["Torrent contains more than one info.json file; the catalog manifest is ambiguous."]);
        }

        var manifestFile = manifestFiles[0];
        if (manifestFile.Entry.Length > MaximumManifestBytes)
        {
            throw new TorrentManifestValidationException(
                [$"info.json exceeds the {MaximumManifestBytes / 1024 / 1024} MiB limit."]);
        }

        await using var readHandle = await engine.OpenReadAsync(
            sessionId,
            manifestFile.Entry.Index,
            cancellationToken);
        if (readHandle.Stream is null)
        {
            throw new TorrentManifestValidationException(
                ["The torrent engine cannot expose info.json as a readable stream."]);
        }

        await using var manifestBuffer = await ReadManifestAsync(readHandle.Stream, cancellationToken);
        var document = await catalogTransferService.ReadUnboundAsync(
            manifestBuffer,
            cancellationToken);
        var boundDocument = BindToCurrentTorrent(
            document,
            magnetLink,
            privateMode,
            normalizedFiles,
            manifestFile.Entry.Index);
        var import = await catalogTransferService.ImportAsync(
            boundDocument,
            $"torrent:{magnetLink.StableIdentity}/{ManifestFileName}",
            cancellationToken);

        return new TorrentManifestImportResult(
            TorrentManifestImportStatus.Imported,
            manifestFile.Entry.Path,
            import);
    }

    private static CatalogTransferDocument BindToCurrentTorrent(
        CatalogTransferDocument document,
        MagnetLink magnetLink,
        bool privateMode,
        IReadOnlyList<NormalizedTorrentFile> files,
        int manifestFileIndex)
    {
        var errors = new List<string>();
        var sourceCount = 0;
        var items = new List<CatalogTransferItem>(document.Items.Count);

        for (var itemIndex = 0; itemIndex < document.Items.Count; itemIndex++)
        {
            var item = document.Items[itemIndex];
            var itemPath = $"items[{itemIndex}]";
            var itemSources = BindSources(
                item.Sources,
                $"{itemPath}.sources",
                magnetLink,
                privateMode,
                files,
                manifestFileIndex,
                errors,
                ref sourceCount);
            var contributors = item.Contributors
                .Select((contributor, contributorIndex) => contributor with
                {
                    ThumbnailRef = BindFileReference(
                        contributor.ThumbnailRef,
                        $"{itemPath}.contributors[{contributorIndex}].thumbnailRef",
                        files,
                        manifestFileIndex,
                        errors)
                })
                .ToArray();
            var units = item.Units
                .Select((unit, unitIndex) => BindUnit(
                    unit,
                    $"{itemPath}.units[{unitIndex}]",
                    magnetLink,
                    privateMode,
                    files,
                    manifestFileIndex,
                    errors,
                    ref sourceCount))
                .ToArray();
            var seasons = item.Seasons
                .Select((season, seasonIndex) => season with
                {
                    ThumbnailRef = BindFileReference(
                        season.ThumbnailRef,
                        $"{itemPath}.seasons[{seasonIndex}].thumbnailRef",
                        files,
                        manifestFileIndex,
                        errors),
                    Units = season.Units
                        .Select((unit, unitIndex) => BindUnit(
                            unit,
                            $"{itemPath}.seasons[{seasonIndex}].units[{unitIndex}]",
                            magnetLink,
                            privateMode,
                            files,
                            manifestFileIndex,
                            errors,
                            ref sourceCount))
                        .ToArray()
                })
                .ToArray();

            items.Add(item with
            {
                ThumbnailRef = BindFileReference(
                    item.ThumbnailRef,
                    $"{itemPath}.thumbnailRef",
                    files,
                    manifestFileIndex,
                    errors),
                BackdropRef = BindFileReference(
                    item.BackdropRef,
                    $"{itemPath}.backdropRef",
                    files,
                    manifestFileIndex,
                    errors),
                Contributors = contributors,
                Sources = itemSources,
                Units = units,
                Seasons = seasons
            });
        }

        if (sourceCount == 0)
        {
            errors.Add("info.json must map at least one catalog item or content unit to this torrent.");
        }

        if (errors.Count > 0)
        {
            throw new TorrentManifestValidationException(errors);
        }

        return document with { Items = items };
    }

    private static CatalogTransferUnit BindUnit(
        CatalogTransferUnit unit,
        string path,
        MagnetLink magnetLink,
        bool privateMode,
        IReadOnlyList<NormalizedTorrentFile> files,
        int manifestFileIndex,
        List<string> errors,
        ref int sourceCount)
    {
        return unit with
        {
            ThumbnailRef = BindFileReference(
                unit.ThumbnailRef,
                $"{path}.thumbnailRef",
                files,
                manifestFileIndex,
                errors),
            Sources = BindSources(
                unit.Sources,
                $"{path}.sources",
                magnetLink,
                privateMode,
                files,
                manifestFileIndex,
                errors,
                ref sourceCount)
        };
    }

    private static IReadOnlyList<CatalogTransferMediaSource> BindSources(
        IReadOnlyList<CatalogTransferMediaSource> sources,
        string path,
        MagnetLink magnetLink,
        bool privateMode,
        IReadOnlyList<NormalizedTorrentFile> files,
        int manifestFileIndex,
        List<string> errors,
        ref int sourceCount)
    {
        var boundSources = new List<CatalogTransferMediaSource>(sources.Count);

        for (var sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
        {
            var source = sources[sourceIndex];
            var sourcePath = $"{path}[{sourceIndex}]";
            sourceCount++;

            ValidateTorrentIdentity(source.Torrent, magnetLink, sourcePath, errors);
            ValidateFileMapping(source, sourcePath, files, manifestFileIndex, errors);

            boundSources.Add(source with
            {
                Torrent = source.Torrent with
                {
                    MagnetUri = magnetLink.CatalogIdentityUri,
                    TorrentMetadataBase64 = null,
                    InfoHashV1 = magnetLink.InfoHashV1,
                    InfoHashV2 = magnetLink.InfoHashV2,
                    IsPrivate = privateMode || magnetLink.DeclaresPrivate || source.Torrent.IsPrivate,
                    Trackers = []
                }
            });
        }

        return boundSources;
    }

    private static string? BindFileReference(
        string? reference,
        string path,
        IReadOnlyList<NormalizedTorrentFile> files,
        int manifestFileIndex,
        List<string> errors)
    {
        if (reference is null)
        {
            return null;
        }

        try
        {
            var normalizedReference = NormalizeRelativePath(reference);
            var matches = files
                .Where(file => PathMatches(file.NormalizedPath, normalizedReference))
                .ToArray();
            if (matches.Length != 1)
            {
                errors.Add($"{path} must identify exactly one file in the active torrent.");
                return reference;
            }

            if (matches[0].Entry.Index == manifestFileIndex)
            {
                errors.Add($"{path} cannot reference info.json.");
                return reference;
            }

            return matches[0].NormalizedPath;
        }
        catch (ArgumentException exception)
        {
            errors.Add($"{path}: {exception.Message}");
            return reference;
        }
    }

    private static void ValidateTorrentIdentity(
        CatalogTransferTorrent torrent,
        MagnetLink activeMagnet,
        string path,
        List<string> errors)
    {
        var declaresIdentity =
            !string.IsNullOrWhiteSpace(torrent.InfoHashV1) ||
            !string.IsNullOrWhiteSpace(torrent.InfoHashV2) ||
            !string.IsNullOrWhiteSpace(torrent.MagnetUri);
        if (!declaresIdentity)
        {
            return;
        }

        var matched = false;

        if (!string.IsNullOrWhiteSpace(torrent.InfoHashV1))
        {
            try
            {
                var hash = MagnetLink.NormalizeV1Hash(torrent.InfoHashV1);
                if (activeMagnet.InfoHashV1 is null)
                {
                    errors.Add($"{path}.torrent.infoHashV1 is not present in the active magnet.");
                }
                else if (string.Equals(hash, activeMagnet.InfoHashV1, StringComparison.Ordinal))
                {
                    matched = true;
                }
                else
                {
                    errors.Add($"{path}.torrent.infoHashV1 does not identify the active torrent.");
                }
            }
            catch (ArgumentException exception)
            {
                errors.Add($"{path}.torrent.infoHashV1: {exception.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(torrent.InfoHashV2))
        {
            try
            {
                var hash = MagnetLink.NormalizeV2Hash(torrent.InfoHashV2);
                if (activeMagnet.InfoHashV2 is null)
                {
                    errors.Add($"{path}.torrent.infoHashV2 is not present in the active magnet.");
                }
                else if (string.Equals(hash, activeMagnet.InfoHashV2, StringComparison.Ordinal))
                {
                    matched = true;
                }
                else
                {
                    errors.Add($"{path}.torrent.infoHashV2 does not identify the active torrent.");
                }
            }
            catch (ArgumentException exception)
            {
                errors.Add($"{path}.torrent.infoHashV2: {exception.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(torrent.MagnetUri))
        {
            try
            {
                var declaredMagnet = MagnetLink.Parse(torrent.MagnetUri);
                if (declaredMagnet.InfoHashV1 is not null)
                {
                    if (string.Equals(
                        declaredMagnet.InfoHashV1,
                        activeMagnet.InfoHashV1,
                        StringComparison.Ordinal))
                    {
                        matched = true;
                    }
                    else
                    {
                        errors.Add($"{path}.torrent.magnetUri contains a btih identity outside the active torrent.");
                    }
                }

                if (declaredMagnet.InfoHashV2 is not null)
                {
                    if (string.Equals(
                        declaredMagnet.InfoHashV2,
                        activeMagnet.InfoHashV2,
                        StringComparison.Ordinal))
                    {
                        matched = true;
                    }
                    else
                    {
                        errors.Add($"{path}.torrent.magnetUri contains a btmh identity outside the active torrent.");
                    }
                }
            }
            catch (ArgumentException exception)
            {
                errors.Add($"{path}.torrent.magnetUri: {exception.Message}");
            }
        }

        if (!matched)
        {
            errors.Add($"{path}.torrent explicitly declares an identity but does not match the active torrent.");
        }
    }

    private static void ValidateFileMapping(
        CatalogTransferMediaSource source,
        string path,
        IReadOnlyList<NormalizedTorrentFile> files,
        int manifestFileIndex,
        List<string> errors)
    {
        if (string.Equals(source.SourceLayout, "image_sequence", StringComparison.OrdinalIgnoreCase))
        {
            ValidateImageSequenceMapping(source, path, files, manifestFileIndex, errors);
            return;
        }

        NormalizedTorrentFile? indexedFile = null;
        if (source.FileIndex is not null)
        {
            indexedFile = files.SingleOrDefault(file => file.Entry.Index == source.FileIndex.Value);
            if (indexedFile is null)
            {
                errors.Add($"{path}.fileIndex does not exist in the active torrent.");
            }
        }

        NormalizedTorrentFile? pathFile = null;
        if (!string.IsNullOrWhiteSpace(source.FilePath))
        {
            try
            {
                var normalizedPath = NormalizeRelativePath(source.FilePath);
                var matches = files.Where(file => PathMatches(file.NormalizedPath, normalizedPath)).ToArray();
                if (matches.Length != 1)
                {
                    errors.Add($"{path}.filePath must identify exactly one file in the active torrent.");
                }
                else
                {
                    pathFile = matches[0];
                }
            }
            catch (ArgumentException exception)
            {
                errors.Add($"{path}.filePath: {exception.Message}");
            }
        }

        var resolved = indexedFile ?? pathFile;
        if (resolved is null)
        {
            if (source.FileIndex is null && string.IsNullOrWhiteSpace(source.FilePath))
            {
                errors.Add($"{path} requires fileIndex or filePath for a torrent-local manifest.");
            }

            return;
        }

        if (indexedFile is not null && pathFile is not null && indexedFile.Entry.Index != pathFile.Entry.Index)
        {
            errors.Add($"{path}.fileIndex and filePath identify different torrent files.");
        }

        if (resolved.Entry.Index == manifestFileIndex)
        {
            errors.Add($"{path} cannot map info.json as playable or readable media.");
        }

        if (source.SizeBytes is not null && source.SizeBytes != resolved.Entry.Length)
        {
            errors.Add($"{path}.sizeBytes does not match the torrent metadata.");
        }
    }

    private static void ValidateImageSequenceMapping(
        CatalogTransferMediaSource source,
        string path,
        IReadOnlyList<NormalizedTorrentFile> files,
        int manifestFileIndex,
        List<string> errors)
    {
        if (source.FileIndex is not null)
        {
            errors.Add($"{path}.fileIndex must be omitted for an image sequence.");
        }

        if (string.IsNullOrWhiteSpace(source.FilePath))
        {
            return;
        }

        try
        {
            var directory = NormalizeRelativePath(source.FilePath).TrimEnd('/');
            var sequenceFiles = files
                .Where(file => file.Entry.Index != manifestFileIndex)
                .Where(file => DirectoryMatches(file.NormalizedPath, directory))
                .ToArray();

            if (sequenceFiles.Length == 0)
            {
                errors.Add($"{path}.filePath does not identify an image directory in the active torrent.");
                return;
            }

            _ = ImageSequenceParser.Parse(sequenceFiles.Select(static file =>
                new ImageSequenceSourceFile(
                    file.Entry.Index,
                    file.NormalizedPath,
                    file.Entry.Length)));

            if (source.SizeBytes is not null && source.SizeBytes != sequenceFiles.Sum(static file => file.Entry.Length))
            {
                errors.Add($"{path}.sizeBytes does not match the image sequence total.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or ImageSequenceValidationException)
        {
            errors.Add($"{path}.filePath: {exception.Message}");
        }
    }

    private static IReadOnlyList<NormalizedTorrentFile> ValidateAndNormalizeFiles(
        IReadOnlyList<TorrentFileEntry> files)
    {
        var errors = new List<string>();
        var result = new List<NormalizedTorrentFile>(files.Count);
        var indexes = new HashSet<int>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            if (file.Index < 0 || !indexes.Add(file.Index))
            {
                errors.Add($"Torrent file index {file.Index} is invalid or duplicated.");
            }

            if (file.Length < 0)
            {
                errors.Add($"Torrent file '{file.Path}' has a negative length.");
            }

            try
            {
                var normalizedPath = NormalizeRelativePath(file.Path);
                if (!paths.Add(normalizedPath))
                {
                    errors.Add($"Torrent file path '{file.Path}' is duplicated.");
                }

                result.Add(new NormalizedTorrentFile(file, normalizedPath));
            }
            catch (ArgumentException exception)
            {
                errors.Add($"Torrent file '{file.Path}': {exception.Message}");
            }
        }

        if (errors.Count > 0)
        {
            throw new TorrentManifestValidationException(errors);
        }

        return result;
    }

    private static async ValueTask<MemoryStream> ReadManifestAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        var destination = new MemoryStream();
        var buffer = new byte[64 * 1024];

        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (destination.Length + read > MaximumManifestBytes)
                {
                    throw new TorrentManifestValidationException(
                        [$"info.json exceeds the {MaximumManifestBytes / 1024 / 1024} MiB limit."]);
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            destination.Position = 0;
            return destination;
        }
        catch
        {
            await destination.DisposeAsync();
            throw;
        }
    }

    private static string NormalizeRelativePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var trimmed = value.Trim();
        if (trimmed.StartsWith('/') ||
            trimmed.StartsWith('\\') ||
            Path.IsPathRooted(trimmed) ||
            trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("Path must be a safe relative torrent path.", nameof(value));
        }

        var normalized = trimmed.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.None);

        if (segments.Length == 0 ||
            segments.Any(static segment =>
                segment.Length == 0 || segment is "." or ".." || segment.Contains(':')))
        {
            throw new ArgumentException("Path must be a safe relative torrent path.", nameof(value));
        }

        return string.Join('/', segments);
    }

    private static bool PathMatches(string torrentPath, string reference) =>
        string.Equals(torrentPath, reference, StringComparison.OrdinalIgnoreCase) ||
        torrentPath.EndsWith('/' + reference, StringComparison.OrdinalIgnoreCase);

    private static bool DirectoryMatches(string torrentPath, string directory) =>
        torrentPath.StartsWith(directory + '/', StringComparison.OrdinalIgnoreCase) ||
        torrentPath.Contains('/' + directory + '/', StringComparison.OrdinalIgnoreCase);

    private static string GetFileName(string normalizedPath)
    {
        var separator = normalizedPath.LastIndexOf('/');
        return separator < 0 ? normalizedPath : normalizedPath[(separator + 1)..];
    }

    private sealed record NormalizedTorrentFile(TorrentFileEntry Entry, string NormalizedPath);
}
