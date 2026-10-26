using System.Security.Cryptography;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Domain.Reading;

namespace UniversalMediaDistribution.Torrenting.Ingestion;

public sealed record PreparedTorrentFile(
    TorrentSessionId SessionId,
    int FileIndex,
    string Path,
    long Length,
    string? MimeType,
    CatalogMediaSourceDetails Source);

public sealed record PreparedImagePage(int Position, PreparedTorrentFile File);

public sealed record PreparedImageSequence(
    CatalogMediaSourceDetails Source,
    IReadOnlyList<PreparedImagePage> Pages);

public sealed class TorrentMediaSessionService(
    ITorrentEngine? engine,
    TorrentSessionRegistry? sessionRegistry = null,
    ITrackerCredentialProvider? trackerCredentialProvider = null,
    TorrentActivityCoordinator? activityCoordinator = null)
{
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly Dictionary<string, SessionContext> _sessions = new(StringComparer.Ordinal);
    private readonly TorrentSessionRegistry _sessionRegistry = sessionRegistry ?? new TorrentSessionRegistry();
    private readonly ITrackerCredentialProvider _trackerCredentialProvider =
        trackerCredentialProvider ?? UnavailableTrackerCredentialProvider.Instance;

    public bool IsAvailable => engine is not null;

    public ValueTask<PreparedTorrentFile> PrepareSingleFileAsync(
        CatalogMediaSourceDetails source,
        CancellationToken cancellationToken = default,
        TorrentActivityId? activityId = null) =>
        ResolveSingleFileCoreAsync(
            source,
            selectFile: true,
            cancellationToken,
            activityId);

    public ValueTask<PreparedTorrentFile> ResolveSingleFileAsync(
        CatalogMediaSourceDetails source,
        CancellationToken cancellationToken = default,
        TorrentActivityId? activityId = null) =>
        ResolveSingleFileCoreAsync(
            source,
            selectFile: false,
            cancellationToken,
            activityId);

    private async ValueTask<PreparedTorrentFile> ResolveSingleFileCoreAsync(
        CatalogMediaSourceDetails source,
        bool selectFile,
        CancellationToken cancellationToken,
        TorrentActivityId? activityId)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Layout is not CatalogMediaSourceLayout.SingleFile)
        {
            throw new ArgumentException("The selected catalog source is not a single file.", nameof(source));
        }

        var context = await GetOrCreateSessionAsync(
            source.Torrent,
            cancellationToken,
            activityId);
        var indexedFile = source.FileIndex is null
            ? null
            : context.Files.SingleOrDefault(file => file.Index == source.FileIndex.Value);
        var pathFile = string.IsNullOrWhiteSpace(source.FilePath)
            ? null
            : ResolvePath(context.Files, source.FilePath);
        var file = indexedFile ?? pathFile ??
            throw new InvalidOperationException("The media source does not identify a torrent file.");

        if (indexedFile is not null && pathFile is not null && indexedFile.Index != pathFile.Index)
        {
            throw new InvalidOperationException(
                "The media source file index and path identify different torrent files.");
        }

        if (selectFile)
        {
            await SelectContextFileAsync(context, file.Index, cancellationToken);
        }

        return ToPreparedFile(context, file, source);
    }

    public async ValueTask<PreparedImageSequence> PrepareImageSequenceAsync(
        CatalogMediaSourceDetails source,
        CancellationToken cancellationToken = default,
        TorrentActivityId? activityId = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Layout is not CatalogMediaSourceLayout.ImageSequence)
        {
            throw new ArgumentException("The selected catalog source is not an image sequence.", nameof(source));
        }

        if (string.IsNullOrWhiteSpace(source.FilePath))
        {
            throw new InvalidOperationException("The image sequence does not identify a torrent directory.");
        }

        var context = await GetOrCreateSessionAsync(
            source.Torrent,
            cancellationToken,
            activityId);
        var reference = NormalizeRelativePath(source.FilePath).TrimEnd('/');
        var exactFile = context.Files.SingleOrDefault(file => PathMatches(file.Path, reference));
        var directory = exactFile is null ? reference : GetDirectory(reference);
        var sequenceFiles = context.Files
            .Where(file => DirectoryMatches(file.Path, directory))
            .Select(static file => new ImageSequenceSourceFile(file.Index, file.Path, file.Length))
            .ToArray();
        var pages = ImageSequenceParser.Parse(sequenceFiles)
            .Select(page => new PreparedImagePage(
                page.Position,
                ToPreparedFile(
                    context,
                    context.Files.Single(file => file.Index == page.FileIndex),
                    source)))
            .ToArray();

        return new PreparedImageSequence(source, pages);
    }

    public async ValueTask<PreparedImageSequence> PrepareSingleImageAsync(
        CatalogMediaSourceDetails source,
        CancellationToken cancellationToken = default,
        TorrentActivityId? activityId = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Layout is not CatalogMediaSourceLayout.SingleFile)
        {
            throw new ArgumentException(
                "A single-image source must map to one torrent file.",
                nameof(source));
        }

        var prepared = await PrepareSingleFileAsync(
            source,
            cancellationToken,
            activityId);
        var extension = Path.GetExtension(prepared.Path);
        var supportedExtension =
            extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
        var supportedMimeType =
            prepared.MimeType?.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) is true ||
            prepared.MimeType?.Equals("image/png", StringComparison.OrdinalIgnoreCase) is true ||
            prepared.MimeType?.Equals("image/webp", StringComparison.OrdinalIgnoreCase) is true;
        if (!supportedExtension && !supportedMimeType)
        {
            throw new InvalidDataException(
                "A single-image book source must be a JPG/JPEG, PNG, or WebP file.");
        }

        return new PreparedImageSequence(
            source,
            [new PreparedImagePage(0, prepared)]);
    }

    public async ValueTask<PreparedImageArchive> PrepareImageArchiveAsync(
        CatalogMediaSourceDetails source,
        CancellationToken cancellationToken = default,
        TorrentActivityId? activityId = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Layout is not CatalogMediaSourceLayout.SingleFile)
        {
            throw new ArgumentException("A CBZ source must map to one torrent file.", nameof(source));
        }

        var prepared = await PrepareSingleFileAsync(
            source,
            cancellationToken,
            activityId);
        await using var readHandle = await OpenReadAsync(prepared, cancellationToken);
        return await PreparedImageArchive.CreateAsync(prepared, readHandle, cancellationToken);
    }

    public async ValueTask<PreparedEpubBook> PrepareEpubBookAsync(
        CatalogMediaSourceDetails source,
        CancellationToken cancellationToken = default,
        TorrentActivityId? activityId = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Layout is not CatalogMediaSourceLayout.SingleFile)
        {
            throw new ArgumentException("An EPUB source must map to one torrent file.", nameof(source));
        }

        var prepared = await PrepareSingleFileAsync(
            source,
            cancellationToken,
            activityId);
        await using var readHandle = await OpenReadAsync(prepared, cancellationToken);
        return await PreparedEpubBook.CreateAsync(prepared, readHandle, cancellationToken);
    }

    public ValueTask<PreparedTorrentFile> PrepareReferencedFileAsync(
        CatalogMediaSourceDetails torrentContext,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(torrentContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return PrepareSingleFileAsync(
            torrentContext with
            {
                AssetRole = "artwork",
                Layout = CatalogMediaSourceLayout.SingleFile,
                FileIndex = null,
                FilePath = filePath,
                MimeType = null,
                SizeBytes = null
            },
            cancellationToken);
    }

    public async ValueTask<IMediaReadHandle> OpenReadAsync(
        PreparedTorrentFile file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var activeEngine = RequireEngine();
        await SelectPreparedFileAsync(file, cancellationToken);
        return await activeEngine.OpenReadAsync(file.SessionId, file.FileIndex, cancellationToken);
    }

    public async ValueTask<Uri?> GetPlaybackUriAsync(
        PreparedTorrentFile file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var activeEngine = RequireEngine();
        await SelectPreparedFileAsync(file, cancellationToken);
        return await activeEngine.GetPlaybackUriAsync(file.SessionId, file.FileIndex, cancellationToken);
    }

    public async ValueTask<Uri?> GetPlaybackUriWithoutSelectingAsync(
        PreparedTorrentFile file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return await RequireEngine().GetPlaybackUriAsync(
            file.SessionId,
            file.FileIndex,
            cancellationToken);
    }

    private async ValueTask<SessionContext> GetOrCreateSessionAsync(
        CatalogTorrentDetails torrent,
        CancellationToken cancellationToken,
        TorrentActivityId? activityId)
    {
        ArgumentNullException.ThrowIfNull(torrent);
        var activeEngine = RequireEngine();
        var identities = CreateTorrentIdentities(torrent);

        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            var existing = identities
                .Select(identity => _sessions.GetValueOrDefault(identity))
                .FirstOrDefault(static context => context is not null);
            if (existing is not null)
            {
                AttachActivity(activityId, existing.SessionId);
                return existing;
            }

            if (_sessionRegistry.TryGet(identities, out var registeredSession))
            {
                AttachActivity(activityId, registeredSession.SessionId);
                if (torrent.IsPrivate && !registeredSession.IsPrivate)
                {
                    throw new InvalidOperationException(
                        "This private catalog source is already open as a public torrent session. " +
                        "Restart the application before opening it in private mode.");
                }

                var registeredFiles = await activeEngine.GetFilesAsync(
                    registeredSession.SessionId,
                    cancellationToken);
                await DeselectAllFilesAsync(
                    activeEngine,
                    registeredSession.SessionId,
                    registeredFiles,
                    cancellationToken);
                var registered = new SessionContext(registeredSession.SessionId, registeredFiles);
                RegisterContext(identities, registered);
                return registered;
            }

            if (torrent.IsPrivate && !activeEngine.Capabilities.SupportsPrivateTorrents)
            {
                throw new InvalidOperationException(
                    $"{activeEngine.Capabilities.DisplayName} does not support private torrents.");
            }

            var descriptorAndTrackers = await CreateDescriptorAsync(
                activeEngine.Capabilities,
                torrent,
                cancellationToken);
            var trackers = descriptorAndTrackers.Trackers;
            if (trackers.Count == 0 && (torrent.IsPrivate || !activeEngine.Capabilities.SupportsDht))
            {
                throw new InvalidOperationException(
                    descriptorAndTrackers.MissingRequiredCredential
                        ? "This source requires tracker authentication, but no local " +
                          "credential is configured for the current platform."
                        : torrent.IsPrivate
                            ? "This private source has no approved encrypted tracker " +
                              "for the current platform."
                            : "This source has no compatible tracker for the current platform.");
            }

            var addOptions = new TorrentAddOptions(
                torrent.IsPrivate,
                PreferSequentialDownload: true,
                trackers,
                SelectAllFiles: false);
            TorrentPrivacyPolicy.Validate(activeEngine.Capabilities, addOptions);
            var sessionId = await activeEngine.AddAsync(
                descriptorAndTrackers.Descriptor,
                addOptions,
                cancellationToken);
            AttachActivity(activityId, sessionId);

            try
            {
                var files = await activeEngine.GetFilesAsync(sessionId, cancellationToken);
                await DeselectAllFilesAsync(
                    activeEngine,
                    sessionId,
                    files,
                    cancellationToken);

                var created = new SessionContext(sessionId, files);
                RegisterContext(identities, created);
                _sessionRegistry.Register(identities, sessionId, torrent.IsPrivate);
                return created;
            }
            catch
            {
                try
                {
                    await activeEngine.RemoveAsync(
                        sessionId,
                        deleteData: false,
                        CancellationToken.None);
                }
                catch
                {
                    // Preserve the source preparation failure that triggered cleanup.
                }

                throw;
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async ValueTask SelectPreparedFileAsync(
        PreparedTorrentFile file,
        CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            var context = _sessions.Values.FirstOrDefault(value => value.SessionId == file.SessionId) ??
                throw new InvalidOperationException("The prepared torrent session is no longer available.");
            await SelectFileAsync(context, file.FileIndex, cancellationToken);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async ValueTask SelectContextFileAsync(
        SessionContext context,
        int fileIndex,
        CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            await SelectFileAsync(context, fileIndex, cancellationToken);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private static PreparedTorrentFile ToPreparedFile(
        SessionContext context,
        TorrentFileEntry file,
        CatalogMediaSourceDetails source) =>
        new(
            context.SessionId,
            file.Index,
            file.Path,
            file.Length,
            source.MimeType ?? file.MimeType,
            source);

    private async ValueTask SelectFileAsync(
        SessionContext context,
        int fileIndex,
        CancellationToken cancellationToken)
    {
        if (!context.SelectedFileIndexes.Add(fileIndex))
        {
            return;
        }

        try
        {
            await RequireEngine().SetFilePriorityAsync(
                context.SessionId,
                fileIndex,
                TorrentFilePriority.High,
                cancellationToken);
        }
        catch
        {
            context.SelectedFileIndexes.Remove(fileIndex);
            throw;
        }
    }

    private async ValueTask<DescriptorAndTrackers> CreateDescriptorAsync(
        TorrentEngineCapabilities capabilities,
        CatalogTorrentDetails torrent,
        CancellationToken cancellationToken)
    {
        var catalogTrackerResolution = await CreateCatalogTrackerEndpointsAsync(
            capabilities,
            torrent,
            cancellationToken);
        var catalogTrackers = catalogTrackerResolution.Trackers;
        MagnetLink? magnet = null;
        if (!string.IsNullOrWhiteSpace(torrent.MagnetUri))
        {
            magnet = MagnetLink.Parse(torrent.MagnetUri);
        }
        else if (!string.IsNullOrWhiteSpace(torrent.InfoHashV1) ||
                 !string.IsNullOrWhiteSpace(torrent.InfoHashV2))
        {
            magnet = MagnetLink.Parse(CreateIdentityMagnet(torrent));
        }

        var magnetTrackers = torrent.IsPrivate
            ? []
            : magnet?.CreateTrackerEndpoints(capabilities, privateMode: false) ?? [];
        var trackers = catalogTrackers
            .Concat(magnetTrackers)
            .GroupBy(static tracker => tracker.Endpoint.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderBy(static tracker => tracker.Tier)
                .ThenBy(static tracker => tracker.SortOrder)
                .First())
            .OrderBy(static tracker => tracker.Tier)
            .ThenBy(static tracker => tracker.SortOrder)
            .ToArray();

        var useMagnet = magnet is not null &&
            (torrent.TorrentMetadata.IsEmpty || trackers.Length > 0);
        var descriptor = useMagnet
            ? new TorrentDescriptor(magnet!.CreateSessionUri(trackers))
            : new TorrentDescriptor(torrentMetadata: torrent.TorrentMetadata.Span);
        return new DescriptorAndTrackers(
            descriptor,
            trackers,
            catalogTrackerResolution.MissingRequiredCredential);
    }

    private async ValueTask<CatalogTrackerResolution> CreateCatalogTrackerEndpointsAsync(
        TorrentEngineCapabilities capabilities,
        CatalogTorrentDetails torrent,
        CancellationToken cancellationToken)
    {
        var family = capabilities.EngineKind is TorrentEngineKind.WebTorrent ? "browser" : "native";
        var result = new List<TrackerEndpoint>();
        var missingRequiredCredential = false;
        foreach (var tracker in torrent.Trackers
                     .OrderBy(static tracker => tracker.Tier)
                     .ThenBy(static tracker => tracker.SortOrder))
        {
            if (!string.Equals(tracker.ClientFamily, "both", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(tracker.ClientFamily, family, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var transport = tracker.Endpoint.Scheme.ToLowerInvariant() switch
            {
                "https" => TrackerTransport.Https,
                "wss" => TrackerTransport.Wss,
                "udp" => TrackerTransport.Udp,
                _ => (TrackerTransport?)null
            };
            if (transport is null ||
                !capabilities.TrackerTransports.Contains(transport.Value) ||
                torrent.IsPrivate && transport is TrackerTransport.Udp)
            {
                continue;
            }

            var endpoint = tracker.Endpoint;
            var usesCredential = false;
            if (tracker.RequiresAuthentication)
            {
                var credential = await _trackerCredentialProvider.ResolveAsync(
                    new TrackerCredentialRequest(
                        tracker.Endpoint,
                        transport.Value,
                        tracker.CredentialKey),
                    cancellationToken);
                if (credential is null)
                {
                    missingRequiredCredential = true;
                    continue;
                }

                if (!string.Equals(
                        credential.Endpoint.Scheme,
                        tracker.Endpoint.Scheme,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "A tracker credential provider returned an endpoint with an " +
                        "unexpected transport.");
                }

                endpoint = credential.Endpoint;
                usesCredential = true;
            }

            result.Add(new TrackerEndpoint(
                endpoint,
                transport.Value,
                tracker.Tier,
                tracker.SortOrder,
                usesCredential));
        }

        return new CatalogTrackerResolution(result, missingRequiredCredential);
    }

    private static IReadOnlyList<string> CreateTorrentIdentities(CatalogTorrentDetails torrent)
    {
        var identities = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(torrent.InfoHashV2))
        {
            identities.Add("v2:" + torrent.InfoHashV2.Trim().ToUpperInvariant());
        }

        if (!string.IsNullOrWhiteSpace(torrent.InfoHashV1))
        {
            identities.Add("v1:" + torrent.InfoHashV1.Trim().ToUpperInvariant());
        }

        if (!string.IsNullOrWhiteSpace(torrent.MagnetUri))
        {
            var magnet = MagnetLink.Parse(torrent.MagnetUri);
            if (magnet.InfoHashV2 is not null)
            {
                identities.Add("v2:" + magnet.InfoHashV2);
            }

            if (magnet.InfoHashV1 is not null)
            {
                identities.Add("v1:" + magnet.InfoHashV1);
            }
        }

        if (identities.Count == 0 && !torrent.TorrentMetadata.IsEmpty)
        {
            identities.Add(
                "metadata:" + Convert.ToHexString(SHA256.HashData(torrent.TorrentMetadata.Span)));
        }

        return identities.Distinct(StringComparer.Ordinal).ToArray() switch
        {
            { Length: > 0 } result => result,
            _ => throw new InvalidOperationException(
                "The catalog torrent has no usable identity or metadata.")
        };
    }

    private static async ValueTask DeselectAllFilesAsync(
        ITorrentEngine activeEngine,
        TorrentSessionId sessionId,
        IReadOnlyList<TorrentFileEntry> files,
        CancellationToken cancellationToken)
    {
        foreach (var file in files)
        {
            await activeEngine.SetFilePriorityAsync(
                sessionId,
                file.Index,
                TorrentFilePriority.DoNotDownload,
                cancellationToken);
        }
    }

    private void RegisterContext(
        IEnumerable<string> identities,
        SessionContext context)
    {
        foreach (var identity in identities)
        {
            _sessions[identity] = context;
        }
    }

    private static string CreateIdentityMagnet(CatalogTorrentDetails torrent)
    {
        var parameters = new List<string>();
        if (!string.IsNullOrWhiteSpace(torrent.InfoHashV1))
        {
            parameters.Add("xt=urn:btih:" + torrent.InfoHashV1.Trim());
        }

        if (!string.IsNullOrWhiteSpace(torrent.InfoHashV2))
        {
            parameters.Add("xt=urn:btmh:1220" + torrent.InfoHashV2.Trim());
        }

        if (!string.IsNullOrWhiteSpace(torrent.DisplayName))
        {
            parameters.Add("dn=" + Uri.EscapeDataString(torrent.DisplayName.Trim()));
        }

        if (parameters.Count == 0)
        {
            throw new InvalidOperationException("The catalog torrent has no usable identity or metadata.");
        }

        return "magnet:?" + string.Join('&', parameters);
    }

    private static TorrentFileEntry? ResolvePath(
        IReadOnlyList<TorrentFileEntry> files,
        string value)
    {
        var reference = NormalizeRelativePath(value);
        var matches = files.Where(file => PathMatches(file.Path, reference)).ToArray();
        return matches.Length switch
        {
            0 => throw new InvalidOperationException(
                $"Torrent file '{reference}' was not found in the selected source."),
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"Torrent file path '{reference}' matches more than one file.")
        };
    }

    private static string NormalizeRelativePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().Replace('\\', '/').Trim('/');
        var segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Length == 0 ||
            segments.Any(static segment =>
                segment.Length == 0 || segment is "." or ".." || segment.Contains(':') ||
                segment.Any(char.IsControl)))
        {
            throw new ArgumentException("Path must be a safe relative torrent path.", nameof(value));
        }

        return string.Join('/', segments);
    }

    private static bool PathMatches(string torrentPath, string reference)
    {
        var normalizedTorrentPath = torrentPath.Replace('\\', '/').Trim('/');
        return string.Equals(normalizedTorrentPath, reference, StringComparison.OrdinalIgnoreCase) ||
            normalizedTorrentPath.EndsWith('/' + reference, StringComparison.OrdinalIgnoreCase);
    }

    private static bool DirectoryMatches(string torrentPath, string directory)
    {
        var normalizedTorrentPath = torrentPath.Replace('\\', '/').Trim('/');
        return directory.Length == 0 ||
            normalizedTorrentPath.StartsWith(directory + '/', StringComparison.OrdinalIgnoreCase) ||
            normalizedTorrentPath.Contains('/' + directory + '/', StringComparison.OrdinalIgnoreCase);
    }

    private static string GetDirectory(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private ITorrentEngine RequireEngine() => engine ??
        throw new InvalidOperationException("This platform has no torrent engine configured.");

    private void AttachActivity(
        TorrentActivityId? activityId,
        TorrentSessionId sessionId)
    {
        if (activityId is { } id && activityCoordinator is not null)
        {
            activityCoordinator.AttachSession(id, sessionId);
        }
    }

    private sealed record DescriptorAndTrackers(
        TorrentDescriptor Descriptor,
        IReadOnlyList<TrackerEndpoint> Trackers,
        bool MissingRequiredCredential);

    private sealed record CatalogTrackerResolution(
        IReadOnlyList<TrackerEndpoint> Trackers,
        bool MissingRequiredCredential);

    private sealed class SessionContext(
        TorrentSessionId sessionId,
        IReadOnlyList<TorrentFileEntry> files)
    {
        public TorrentSessionId SessionId { get; } = sessionId;

        public IReadOnlyList<TorrentFileEntry> Files { get; } = files;

        public HashSet<int> SelectedFileIndexes { get; } = [];
    }
}
