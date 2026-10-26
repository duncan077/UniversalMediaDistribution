using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Torrenting;

namespace UniversalMediaDistribution.Torrenting.Ingestion;

public sealed class MagnetOpenService
{
    public static readonly TimeSpan InfoJsonImportTimeout = TimeSpan.FromMinutes(2);

    private readonly ITorrentEngine? _engine;
    private readonly IMagnetOpenPreferencesStore _preferencesStore;
    private readonly TorrentLocalCatalogImporter _manifestImporter;
    private readonly TorrentSessionRegistry _sessionRegistry;
    private readonly ITrackerCredentialProvider _trackerCredentialProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event EventHandler<TorrentManifestImportCompletedEventArgs>?
        ManifestImportCompleted;

    public MagnetOpenService(
        ITorrentEngine? engine,
        CatalogJsonTransferService catalogTransferService,
        IMagnetOpenPreferencesStore preferencesStore,
        TorrentSessionRegistry? sessionRegistry = null,
        ITrackerCredentialProvider? trackerCredentialProvider = null)
    {
        _engine = engine;
        _preferencesStore = preferencesStore ?? throw new ArgumentNullException(nameof(preferencesStore));
        _sessionRegistry = sessionRegistry ?? new TorrentSessionRegistry();
        _trackerCredentialProvider =
            trackerCredentialProvider ?? UnavailableTrackerCredentialProvider.Instance;
        _manifestImporter = new TorrentLocalCatalogImporter(
            catalogTransferService ?? throw new ArgumentNullException(nameof(catalogTransferService)));
    }

    public bool IsEngineAvailable => _engine is not null;

    public ValueTask<MagnetOpenPreferences> LoadPreferencesAsync(
        CancellationToken cancellationToken = default) =>
        _preferencesStore.LoadAsync(cancellationToken);

    public ValueTask SavePreferencesAsync(
        MagnetOpenPreferences preferences,
        CancellationToken cancellationToken = default) =>
        _preferencesStore.SaveAsync(preferences, cancellationToken);

    public async ValueTask<TorrentFileOpenResult> ReceiveTorrentAsync(
        Stream torrentFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(torrentFile);
        var torrentMetadata = await ReadTorrentMetadataAsync(
            torrentFile,
            cancellationToken);
        var metadata = TorrentMetadataParser.Parse(torrentMetadata);
        var identityMagnet = metadata.CreateIdentityMagnet();
        var preferences = await _preferencesStore.LoadAsync(cancellationToken);

        if (_engine is null)
        {
            return new TorrentFileOpenResult(
                TorrentFileOpenStatus.EngineUnavailable,
                metadata);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var identities = CreateIdentities(identityMagnet);
            if (_sessionRegistry.TryGet(identities, out var existingSession))
            {
                return new TorrentFileOpenResult(
                    TorrentFileOpenStatus.AlreadyOpen,
                    metadata,
                    existingSession.SessionId);
            }

            var privateMode =
                metadata.IsPrivate ||
                preferences.TreatExternalMagnetsAsPrivate;
            var trackers = await ResolveCredentialOverridesAsync(
                metadata.CreateTrackerEndpoints(
                    _engine.Capabilities,
                    privateMode),
                cancellationToken);
            if (trackers.Count == 0 &&
                (privateMode || !_engine.Capabilities.SupportsDht))
            {
                throw new MagnetCompatibilityException(
                    CreateMissingTrackerMessage(
                        _engine.Capabilities,
                        privateMode,
                        "torrent file"));
            }

            var descriptor = new TorrentDescriptor(
                torrentMetadata: torrentMetadata.Span);
            var options = new TorrentAddOptions(
                IsPrivate: privateMode,
                PreferSequentialDownload: true,
                Trackers: trackers);
            TorrentPrivacyPolicy.Validate(
                _engine.Capabilities,
                descriptor,
                options);
            var sessionId = await _engine.AddAsync(
                descriptor,
                options,
                cancellationToken);
            _sessionRegistry.Register(identities, sessionId, privateMode);

            var manifestAttempt = await TryImportManifestAsync(
                sessionId,
                identityMagnet,
                privateMode,
                preferences,
                cancellationToken);
            return new TorrentFileOpenResult(
                TorrentFileOpenStatus.Opened,
                metadata,
                sessionId,
                manifestAttempt.Result,
                manifestAttempt.Warning);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<MagnetOpenResult> ReceiveAsync(
        string magnetUri,
        bool userConfirmed = false,
        CancellationToken cancellationToken = default)
    {
        MagnetLink magnetLink;
        try
        {
            magnetLink = MagnetLink.Parse(magnetUri);
        }
        catch (ArgumentException exception)
        {
            throw new MagnetLinkFormatException(
                exception.Message,
                exception);
        }

        var preferences = await _preferencesStore.LoadAsync(cancellationToken);

        if (!userConfirmed && !preferences.OpenAutomatically)
        {
            return new MagnetOpenResult(MagnetOpenStatus.ConfirmationRequired, magnetLink);
        }

        if (_engine is null)
        {
            return new MagnetOpenResult(MagnetOpenStatus.EngineUnavailable, magnetLink);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var identities = CreateIdentities(magnetLink);
            if (_sessionRegistry.TryGet(identities, out var existingSession))
            {
                return new MagnetOpenResult(
                    MagnetOpenStatus.AlreadyOpen,
                    magnetLink,
                    existingSession.SessionId);
            }

            var privateMode = magnetLink.DeclaresPrivate || preferences.TreatExternalMagnetsAsPrivate;
            var trackers = await ResolveCredentialOverridesAsync(
                magnetLink.CreateTrackerEndpoints(_engine.Capabilities, privateMode),
                cancellationToken);
            if (trackers.Count == 0 && (privateMode || !_engine.Capabilities.SupportsDht))
            {
                throw new MagnetCompatibilityException(
                    CreateMissingTrackerMessage(
                        _engine.Capabilities,
                        privateMode,
                        "magnet link"));
            }

            var descriptor = new TorrentDescriptor(magnetLink.CreateSessionUri(trackers));
            var options = new TorrentAddOptions(
                IsPrivate: privateMode,
                PreferSequentialDownload: true,
                Trackers: trackers);
            TorrentPrivacyPolicy.Validate(_engine.Capabilities, options);
            var sessionId = await _engine.AddAsync(descriptor, options, cancellationToken);
            _sessionRegistry.Register(identities, sessionId, privateMode);

            var manifestAttempt = await TryImportManifestAsync(
                sessionId,
                magnetLink,
                privateMode,
                preferences,
                cancellationToken);

            return new MagnetOpenResult(
                MagnetOpenStatus.Opened,
                magnetLink,
                sessionId,
                manifestAttempt.Result,
                manifestAttempt.Warning);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string CreateMissingTrackerMessage(
        TorrentEngineCapabilities capabilities,
        bool privateMode,
        string sourceDescription)
    {
        if (privateMode)
        {
            return capabilities.EngineKind is TorrentEngineKind.WebTorrent
                ? $"Private browser mode requires a WSS WebTorrent tracker in the {sourceDescription}. Use a file/link with a WSS tracker or a native build with the private tracker's supported endpoint."
                : $"Private mode requires a compatible HTTPS tracker in the {sourceDescription}. Use a file/link with an HTTPS tracker or disable forced private mode only if this is a public torrent.";
        }

        return capabilities.EngineKind is TorrentEngineKind.WebTorrent
            ? $"WebTorrent cannot use BitTorrent DHT, TCP, or UDP, and this {sourceDescription} has no WSS WebTorrent tracker. Use one with a WSS tracker or open it in a native build."
            : $"{capabilities.DisplayName} does not support DHT and this {sourceDescription} has no compatible tracker.";
    }

    private async ValueTask<ManifestAttempt> TryImportManifestAsync(
        TorrentSessionId sessionId,
        MagnetLink magnetLink,
        bool privateMode,
        MagnetOpenPreferences preferences,
        CancellationToken cancellationToken)
    {
        if (!preferences.ImportInfoJson)
        {
            return default;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var importCancellation = new CancellationTokenSource(
            InfoJsonImportTimeout);
        Task<TorrentManifestImportResult> importTask;
        try
        {
            importTask = _manifestImporter.TryImportAsync(
                _engine!,
                sessionId,
                magnetLink,
                privateMode,
                importCancellation.Token).AsTask();
        }
        catch
        {
            importCancellation.Dispose();
            throw;
        }

        if (importTask.IsCompleted)
        {
            using (importCancellation)
            {
                return await CompleteManifestImportAsync(
                    importTask,
                    importCancellation);
            }
        }

        _ = ObserveManifestImportAsync(
            sessionId,
            importTask,
            importCancellation);
        return new ManifestAttempt(
            TorrentManifestImportResult.Pending,
            Warning: null);
    }

    private async Task ObserveManifestImportAsync(
        TorrentSessionId sessionId,
        Task<TorrentManifestImportResult> importTask,
        CancellationTokenSource importCancellation)
    {
        ManifestAttempt attempt;
        using (importCancellation)
        {
            attempt = await CompleteManifestImportAsync(
                importTask,
                importCancellation);
        }

        try
        {
            ManifestImportCompleted?.Invoke(
                this,
                new TorrentManifestImportCompletedEventArgs(
                    sessionId,
                    attempt.Result,
                    attempt.Warning));
        }
        catch
        {
            // A UI notification must not turn a completed background import
            // into an unobserved task failure.
        }
    }

    private static async Task<ManifestAttempt> CompleteManifestImportAsync(
        Task<TorrentManifestImportResult> importTask,
        CancellationTokenSource importCancellation)
    {
        try
        {
            var result = await importTask;
            return new ManifestAttempt(result, Warning: null);
        }
        catch (OperationCanceledException)
            when (importCancellation.IsCancellationRequested)
        {
            return new ManifestAttempt(
                Result: null,
                "The background info.json lookup timed out; the torrent remains open.");
        }
        catch (TorrentManifestValidationException exception)
        {
            return new ManifestAttempt(
                Result: null,
                string.Join(" ", exception.Errors.Take(3)));
        }
        catch (CatalogTransferValidationException exception)
        {
            return new ManifestAttempt(
                Result: null,
                string.Join(" ", exception.Errors.Take(3)));
        }
        catch (Exception exception)
        {
            return new ManifestAttempt(
                Result: null,
                $"info.json could not be imported: {exception.Message}");
        }
    }

    private static async ValueTask<ReadOnlyMemory<byte>> ReadTorrentMetadataAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        if (!source.CanRead)
        {
            throw new TorrentMetadataFormatException(
                "The selected torrent file cannot be read.");
        }

        if (source.CanSeek)
        {
            var remaining = source.Length - source.Position;
            if (remaining > TorrentMetadataParser.MaximumMetadataBytes)
            {
                throw new TorrentMetadataFormatException(
                    $"The torrent file exceeds the " +
                    $"{TorrentMetadataParser.MaximumMetadataBytes / 1024 / 1024} MiB limit.");
            }
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[80 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read >
                TorrentMetadataParser.MaximumMetadataBytes)
            {
                throw new TorrentMetadataFormatException(
                    $"The torrent file exceeds the " +
                    $"{TorrentMetadataParser.MaximumMetadataBytes / 1024 / 1024} MiB limit.");
            }

            await buffer.WriteAsync(
                chunk.AsMemory(0, read),
                cancellationToken);
        }

        return buffer.ToArray();
    }

    private static IReadOnlyList<string> CreateIdentities(MagnetLink magnetLink)
    {
        var identities = new List<string>(2);
        if (magnetLink.InfoHashV2 is not null)
        {
            identities.Add("v2:" + magnetLink.InfoHashV2);
        }

        if (magnetLink.InfoHashV1 is not null)
        {
            identities.Add("v1:" + magnetLink.InfoHashV1);
        }

        return identities;
    }

    private async ValueTask<IReadOnlyList<TrackerEndpoint>> ResolveCredentialOverridesAsync(
        IReadOnlyList<TrackerEndpoint> trackers,
        CancellationToken cancellationToken)
    {
        var resolved = new List<TrackerEndpoint>(trackers.Count);
        foreach (var tracker in trackers)
        {
            var credential = await _trackerCredentialProvider.ResolveAsync(
                new TrackerCredentialRequest(tracker.Endpoint, tracker.Transport),
                cancellationToken);
            if (credential is null)
            {
                resolved.Add(tracker);
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

            resolved.Add(new TrackerEndpoint(
                credential.Endpoint,
                tracker.Transport,
                tracker.Tier,
                tracker.SortOrder,
                usesCredential: true));
        }

        return resolved;
    }

    private readonly record struct ManifestAttempt(
        TorrentManifestImportResult? Result,
        string? Warning);
}
