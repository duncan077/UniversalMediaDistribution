using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Catalog.LiveTv;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.ContentSources;

public enum CatalogSearchResultOrigin
{
    Local = 1,
    Online = 2
}

public sealed class ContentSourceCatalogSearchResult
{
    private ContentSourceCatalogSearchResult(
        CatalogCard card,
        CatalogSearchResultOrigin origin,
        ContentSourceProviderConfiguration? provider,
        string? externalId,
        CatalogTransferItem? providerItem,
        ApiContentSearchItem? apiSearchItem)
    {
        Card = card;
        Origin = origin;
        Provider = provider;
        ExternalId = externalId;
        ProviderItem = providerItem;
        ApiSearchItem = apiSearchItem;
    }

    public CatalogCard Card { get; }

    public CatalogSearchResultOrigin Origin { get; }

    public bool IsOnline => Origin is CatalogSearchResultOrigin.Online;

    public ContentSourceProviderConfiguration? Provider { get; }

    public string? ExternalId { get; }

    internal CatalogTransferItem? ProviderItem { get; }

    internal ApiContentSearchItem? ApiSearchItem { get; }

    internal static ContentSourceCatalogSearchResult Local(CatalogCard card) =>
        new(
            card ?? throw new ArgumentNullException(nameof(card)),
            CatalogSearchResultOrigin.Local,
            provider: null,
            externalId: null,
            providerItem: null,
            apiSearchItem: null);

    internal static ContentSourceCatalogSearchResult Online(
        CatalogCard card,
        ContentSourceProviderConfiguration provider,
        string externalId,
        CatalogTransferItem providerItem,
        ApiContentSearchItem? apiSearchItem = null) =>
        new(
            card ?? throw new ArgumentNullException(nameof(card)),
            CatalogSearchResultOrigin.Online,
            provider ?? throw new ArgumentNullException(nameof(provider)),
            string.IsNullOrWhiteSpace(externalId)
                ? throw new ArgumentException(
                    "An online search result requires an external ID.",
                    nameof(externalId))
                : externalId.Trim(),
            providerItem ?? throw new ArgumentNullException(nameof(providerItem)),
            apiSearchItem);
}

public sealed record ContentSourceSearchFailure(
    string ProviderId,
    string ProviderName,
    string Message);

public sealed record ContentSourceCatalogSearchResponse(
    IReadOnlyList<ContentSourceCatalogSearchResult> Results,
    IReadOnlyList<ContentSourceSearchFailure> Failures);

public sealed record ContentSourceCatalogSearchBatch(
    string? ProviderId,
    string ProviderName,
    bool IsLocal,
    IReadOnlyList<ContentSourceCatalogSearchResult> Results,
    ContentSourceSearchFailure? Failure);

public sealed record ContentSourceCatalogLoadResult(
    CatalogItemDetails Details,
    string ProviderName,
    Uri ProviderBaseUri,
    IReadOnlyList<Uri> TrustedArtworkOrigins,
    IReadOnlyList<ApiContentImageSet> ImageSets,
    IReadOnlyList<ApiContentVideoSet> VideoSets,
    int CreatedCount,
    int UpdatedCount,
    int UnchangedCount,
    Uri? ArtworkReferrer = null);

public sealed record ContentSourceCatalogRefreshResult(
    CatalogItemDetails Details,
    string ProviderId,
    string ProviderName,
    Uri ProviderBaseUri,
    IReadOnlyList<Uri> TrustedArtworkOrigins,
    double TitleSimilarity,
    int CreatedCount,
    int UpdatedCount,
    int UnchangedCount,
    Uri? ArtworkReferrer = null);

public sealed record ContentSourceDailyUpdateFailure(
    string ProviderId,
    string ProviderName,
    string Message);

public sealed record ContentSourceDailyUpdateResult(
    int CheckedProviderCount,
    int SkippedProviderCount,
    int CreatedCount,
    int UpdatedCount,
    int UnchangedCount,
    IReadOnlyList<ContentSourceDailyUpdateFailure> Failures)
{
    public bool WasRefreshAttempted => CheckedProviderCount > 0;
}

public sealed record ContentSourceDailyUpdateBatch(
    string ProviderId,
    string ProviderName,
    bool WasChecked,
    bool WasSkipped,
    int CreatedCount,
    int UpdatedCount,
    int UnchangedCount,
    ContentSourceDailyUpdateFailure? Failure);

public sealed record ApiContentUnitImageResolution(
    string ProviderId,
    string ProviderName,
    Uri ProviderBaseUri,
    ApiContentImageSet ImageSet,
    double TitleSimilarity,
    CatalogItemDetails Details,
    CatalogContentUnitDetails Unit,
    IReadOnlyList<Uri> TrustedArtworkOrigins,
    int CreatedCount,
    int UpdatedCount,
    Uri? ArtworkReferrer = null);

public sealed record ApiContentUnitVideoResolution(
    string ProviderId,
    string ProviderName,
    Uri ProviderBaseUri,
    ApiContentVideoSet VideoSet,
    double TitleSimilarity,
    CatalogItemDetails Details,
    CatalogContentUnitDetails Unit,
    IReadOnlyList<Uri> TrustedArtworkOrigins,
    int CreatedCount,
    int UpdatedCount,
    Uri? ArtworkReferrer = null);

public sealed record ApiContentUnitImageCandidate(
    ContentSourceProviderConfiguration Configuration,
    Uri ProviderBaseUri,
    ApiContentSearchItem SearchItem,
    ApiContentItem ApiItem,
    ApiContentUnitRequest UnitRequest,
    double TitleSimilarity,
    IReadOnlyList<ApiProviderUnitRoute> UnitRoutes,
    bool UsedKnownBinding)
{
    public string ProviderId => Configuration.Id;

    public string ProviderName => Configuration.DisplayName;

    public string ExternalId => SearchItem.ExternalId;
}

public sealed record ApiContentUnitVideoCandidate(
    ContentSourceProviderConfiguration Configuration,
    Uri ProviderBaseUri,
    ApiContentSearchItem SearchItem,
    ApiContentItem ApiItem,
    ApiContentUnitRequest UnitRequest,
    double TitleSimilarity,
    IReadOnlyList<ApiProviderUnitRoute> UnitRoutes,
    bool UsedKnownBinding)
{
    public string ProviderId => Configuration.Id;

    public string ProviderName => Configuration.DisplayName;

    public string ExternalId => SearchItem.ExternalId;
}

public sealed class ContentSourceCatalogService
{
    public const string ManualProviderId = "manual";

    private const int MaximumDailyUpdatesPerProvider = 100;
    private static readonly TimeSpan ApiUnitDiscoveryTimeout =
        TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ApiUnitResolutionTimeout =
        TimeSpan.FromSeconds(45);
    private static readonly TimeSpan SlowApiUnitLookupThreshold =
        TimeSpan.FromSeconds(5);
    private static readonly Guid SearchResultNamespace =
        new("72d8af79-5f71-5fe5-a941-e6d9f0d857e2");
    private readonly CatalogBrowseService _catalogBrowseService;
    private readonly CatalogJsonTransferService _catalogTransferService;
    private readonly IContentSourceProviderSettingsStore _settingsStore;
    private readonly HttpClient _httpClient;
    private readonly IReadOnlyDictionary<string, ApiContentProviderRegistration>
        _apiProviders;
    private readonly ApiContentProviderConcurrencyCoordinator
        _apiProviderConcurrency = new();
    private readonly IReadOnlyDictionary<string, LiveTvProviderDescriptor>
        _liveTvProviders;
    private readonly Func<bool> _hideAdultContent;
    private readonly ConcurrentDictionary<string, DateTimeOffset>
        _sessionDailyUpdateRefreshes = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _dailyUpdateGate = new(1, 1);

    /// <summary>
    /// How many providers may hold a fetched daily-update payload at once. The
    /// import stage is serial, so this only has to cover the fetch that
    /// overlaps the running import plus a little slack; raising it trades
    /// memory for a shorter tail when one provider is slow.
    /// </summary>
    private const int MaximumPreparedDailyUpdateLookahead = 4;

    private readonly IApiTitleBindingStore? _titleBindingStore;

    public ContentSourceCatalogService(
        CatalogBrowseService catalogBrowseService,
        CatalogJsonTransferService catalogTransferService,
        IContentSourceProviderSettingsStore settingsStore,
        HttpClient httpClient,
        IEnumerable<IApiContentProvider> apiProviders,
        IContentSourceDailyUpdateStore? dailyUpdateStore = null,
        TimeProvider? timeProvider = null,
        IApiTitleBindingStore? titleBindingStore = null,
        IEnumerable<LiveTvProviderDescriptor>? liveTvProviders = null,
        Func<bool>? hideAdultContent = null)
        : this(
            catalogBrowseService,
            catalogTransferService,
            settingsStore,
            httpClient,
            CreateRegistrations(apiProviders),
            dailyUpdateStore,
            timeProvider,
            titleBindingStore,
            liveTvProviders,
            hideAdultContent)
    {
    }

    public ContentSourceCatalogService(
        CatalogBrowseService catalogBrowseService,
        CatalogJsonTransferService catalogTransferService,
        IContentSourceProviderSettingsStore settingsStore,
        HttpClient httpClient,
        IEnumerable<ApiContentProviderRegistration> apiProviders,
        IContentSourceDailyUpdateStore? dailyUpdateStore = null,
        TimeProvider? timeProvider = null,
        IApiTitleBindingStore? titleBindingStore = null,
        IEnumerable<LiveTvProviderDescriptor>? liveTvProviders = null,
        Func<bool>? hideAdultContent = null)
    {
        _titleBindingStore = titleBindingStore;
        _catalogBrowseService = catalogBrowseService ??
            throw new ArgumentNullException(nameof(catalogBrowseService));
        _catalogTransferService = catalogTransferService ??
            throw new ArgumentNullException(nameof(catalogTransferService));
        _settingsStore = settingsStore ??
            throw new ArgumentNullException(nameof(settingsStore));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        // Retain the parameter for existing compositions, but do not use it:
        // hourly refresh checkpoints are intentionally session-only.
        _ = dailyUpdateStore;
        ArgumentNullException.ThrowIfNull(apiProviders);
        _apiProviders = apiProviders
            .Select(registration => CoordinateRegistration(
                registration,
                _apiProviderConcurrency))
            .ToDictionary(
            static registration => registration.Descriptor.Id,
            StringComparer.Ordinal);
        _liveTvProviders = (liveTvProviders ?? [])
            .ToDictionary(
                static provider => provider.Id,
                StringComparer.Ordinal);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _hideAdultContent = hideAdultContent ?? (static () => false);
    }

    public async ValueTask<ContentSourceProviderSettings> LoadSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        ContentSourceProviderSettings stored;
        try
        {
            stored = (await _settingsStore.LoadAsync(cancellationToken)).Normalize();
        }
        catch (ArgumentException)
        {
            stored = new ContentSourceProviderSettings();
        }

        var providers = new List<ContentSourceProviderConfiguration>
        {
            new()
            {
                Id = ManualProviderId,
                DisplayName = "Manual magnet links",
                Kind = ContentSourceProviderKind.Manual,
                IsEnabled = true
            }
        };
        providers.AddRange(stored.Providers
            .Where(static provider =>
                provider.Kind is ContentSourceProviderKind.TorrentCatalog)
            .Where(provider => !_apiProviders.ContainsKey(provider.Id))
            .OrderBy(
                static provider => provider.DisplayName,
                StringComparer.OrdinalIgnoreCase));

        var storedApis = stored.Providers
            .Where(static provider => provider.Kind is ContentSourceProviderKind.Api)
            .ToDictionary(static provider => provider.Id, StringComparer.Ordinal);
        foreach (var registration in _apiProviders.Values.OrderBy(
                     static provider => provider.Descriptor.DisplayName,
                     StringComparer.OrdinalIgnoreCase))
        {
            storedApis.TryGetValue(
                registration.Descriptor.Id,
                out var configuration);
            providers.Add(new ContentSourceProviderConfiguration
            {
                Id = registration.Descriptor.Id,
                DisplayName = registration.Descriptor.DisplayName,
                Kind = ContentSourceProviderKind.Api,
                IsEnabled = configuration?.IsEnabled ??
                    registration.Descriptor.IsEnabledByDefault,
                BaseUrl = ResolveBaseUrl(
                    registration.Descriptor,
                    configuration?.BaseUrl)
            }.Normalize());
        }

        var storedLiveTv = stored.Providers
            .Where(static provider =>
                provider.Kind is ContentSourceProviderKind.LiveTv)
            .ToDictionary(static provider => provider.Id, StringComparer.Ordinal);
        foreach (var descriptor in _liveTvProviders.Values.OrderBy(
                     static provider => provider.DisplayName,
                     StringComparer.OrdinalIgnoreCase))
        {
            storedLiveTv.TryGetValue(descriptor.Id, out var configuration);
            providers.Add(new ContentSourceProviderConfiguration
            {
                Id = descriptor.Id,
                DisplayName = descriptor.DisplayName,
                Kind = ContentSourceProviderKind.LiveTv,
                IsEnabled = configuration?.IsEnabled ??
                    descriptor.IsEnabledByDefault,
                BaseUrl = descriptor.PlaylistUri.AbsoluteUri
            }.Normalize());
        }

        return new ContentSourceProviderSettings
        {
            Providers = providers
        }.Normalize();
    }

    /// <summary>
    /// Returns the media categories a registered API provider serves without
    /// constructing its implementation. Settings uses this descriptor data to
    /// arrange providers into useful media groups.
    /// </summary>
    public ApiContentProviderScope GetApiProviderScope(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return _apiProviders.TryGetValue(providerId.Trim(), out var registration)
            ? registration.Descriptor.Scope
            : ApiContentProviderScope.All;
    }

    /// <summary>
    /// Picks the base URL a built-in provider should actually be fetched from.
    /// <para>
    /// A saved value normally wins, which is what lets a source be pointed
    /// somewhere else. Two cases override that. Empty counts as absent rather
    /// than as a chosen value: a provider saved while disabled may carry a blank
    /// URL, and treating it as chosen shadowed the built-in default
    /// permanently, leaving no way to re-enable the provider from the UI. A
    /// value on a host the source has since left is not a preference either,
    /// only a dead address that would otherwise keep winning on every existing
    /// install while new ones picked up the move.
    /// </para>
    /// </summary>
    private static string ResolveBaseUrl(
        ApiContentProviderDescriptor descriptor,
        string? savedBaseUrl) =>
        // Whatever address is settled on, a site that has since announced a
        // move is followed to where it went. A retired host still falls back
        // to the shipped default first, so a move is applied to the address
        // actually in use rather than to one already known to be dead.
        ProviderBaseUrlRedirects.Apply(
            string.IsNullOrWhiteSpace(savedBaseUrl) ||
            descriptor.IsRetiredBaseUrl(savedBaseUrl)
                ? descriptor.DefaultBaseUrl
                : savedBaseUrl);

    public IReadOnlyList<Uri> GetTrustedArtworkOrigins(
        ContentSourceProviderSettings settings) =>
        NormalizeArtworkOrigins(
            GetArtworkPolicies(settings)
                .Select(static policy => policy.Origin));

    public IReadOnlyList<ApiContentArtworkPolicy> GetArtworkPolicies(
        ContentSourceProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var policies = new List<ApiContentArtworkPolicy>();
        foreach (var configuration in settings.Normalize().Providers.Where(
                     static provider => provider.IsEnabled))
        {
            if (!Uri.TryCreate(
                    configuration.BaseUrl,
                    UriKind.Absolute,
                    out var baseUri))
            {
                continue;
            }

            if (configuration.Kind is ContentSourceProviderKind.Api &&
                _apiProviders.TryGetValue(
                    configuration.Id,
                    out var registration))
            {
                // Settings and startup discovery must remain metadata-only.
                // Before first use, trust the configured origin; once the
                // provider has run, include any CDN origins it discovered.
                policies.AddRange(registration.IsValueCreated
                    ? GetArtworkPolicies(
                        registration.GetProvider(),
                        baseUri)
                    : [new ApiContentArtworkPolicy(baseUri)]);
            }
            else if (configuration.Kind is
                     ContentSourceProviderKind.TorrentCatalog)
            {
                policies.Add(new ApiContentArtworkPolicy(baseUri));
            }
        }

        return NormalizeArtworkPolicies(policies);
    }

    public IReadOnlyList<Uri> GetTrustedArtworkOrigins(
        ContentSourceCatalogSearchResult searchResult) =>
        NormalizeArtworkOrigins(
            GetArtworkPolicies(searchResult)
                .Select(static policy => policy.Origin));

    public IReadOnlyList<ApiContentArtworkPolicy> GetArtworkPolicies(
        ContentSourceCatalogSearchResult searchResult)
    {
        ArgumentNullException.ThrowIfNull(searchResult);
        var configuration = searchResult.Provider;
        if (configuration is null ||
            !Uri.TryCreate(
                configuration.BaseUrl,
                UriKind.Absolute,
                out var baseUri))
        {
            return [];
        }

        if (configuration.Kind is ContentSourceProviderKind.Api &&
            _apiProviders.TryGetValue(
                configuration.Id,
                out var registration))
        {
            return GetArtworkPolicies(
                registration.GetProvider(),
                baseUri);
        }

        return configuration.Kind is
            ContentSourceProviderKind.TorrentCatalog
            ? [new ApiContentArtworkPolicy(baseUri)]
            : [];
    }

    public static Uri? ResolveArtworkReferrer(
        string? artworkReference,
        IReadOnlyList<ApiContentArtworkPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        return Uri.TryCreate(
                   artworkReference,
                   UriKind.Absolute,
                   out var artworkUri) &&
               artworkUri.Scheme is "http" or "https" &&
               artworkUri.UserInfo.Length == 0
            ? policies.FirstOrDefault(policy =>
                    policy.Referrer is not null &&
                    policy.Matches(artworkUri))
                ?.Referrer
            : null;
    }

    public async ValueTask<ContentSourceProviderSettings> SaveSettingsAsync(
        ContentSourceProviderSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Normalize();
        var suppliedApis = normalized.Providers
            .Where(static provider => provider.Kind is ContentSourceProviderKind.Api)
            .ToDictionary(static provider => provider.Id, StringComparer.Ordinal);
        var unknownApi = suppliedApis.Keys.FirstOrDefault(id =>
            !_apiProviders.ContainsKey(id));
        if (unknownApi is not null)
        {
            throw new ArgumentException(
                $"API provider '{unknownApi}' is not registered by application code.",
                nameof(settings));
        }

        var suppliedLiveTv = normalized.Providers
            .Where(static provider =>
                provider.Kind is ContentSourceProviderKind.LiveTv)
            .ToDictionary(static provider => provider.Id, StringComparer.Ordinal);
        var unknownLiveTv = suppliedLiveTv.Keys.FirstOrDefault(id =>
            !_liveTvProviders.ContainsKey(id));
        if (unknownLiveTv is not null)
        {
            throw new ArgumentException(
                $"Live TV provider '{unknownLiveTv}' is not registered by application code.",
                nameof(settings));
        }

        var persisted = new List<ContentSourceProviderConfiguration>();
        foreach (var torrentProvider in normalized.Providers.Where(
                     static provider =>
                         provider.Kind is ContentSourceProviderKind.TorrentCatalog))
        {
            if (string.Equals(
                    torrentProvider.Id,
                    ManualProviderId,
                    StringComparison.Ordinal) ||
                _apiProviders.ContainsKey(torrentProvider.Id) ||
                _liveTvProviders.ContainsKey(torrentProvider.Id))
            {
                throw new ArgumentException(
                    $"Provider ID '{torrentProvider.Id}' is reserved.",
                    nameof(settings));
            }

            persisted.Add(torrentProvider);
        }

        foreach (var registration in _apiProviders.Values)
        {
            suppliedApis.TryGetValue(
                registration.Descriptor.Id,
                out var supplied);
            persisted.Add(new ContentSourceProviderConfiguration
            {
                Id = registration.Descriptor.Id,
                DisplayName = registration.Descriptor.DisplayName,
                Kind = ContentSourceProviderKind.Api,
                IsEnabled = supplied?.IsEnabled ??
                    registration.Descriptor.IsEnabledByDefault,
                BaseUrl = ResolveBaseUrl(
                    registration.Descriptor,
                    supplied?.BaseUrl)
            }.Normalize());
        }

        foreach (var descriptor in _liveTvProviders.Values)
        {
            suppliedLiveTv.TryGetValue(descriptor.Id, out var supplied);
            persisted.Add(new ContentSourceProviderConfiguration
            {
                Id = descriptor.Id,
                DisplayName = descriptor.DisplayName,
                Kind = ContentSourceProviderKind.LiveTv,
                IsEnabled = supplied?.IsEnabled ??
                    descriptor.IsEnabledByDefault,
                BaseUrl = descriptor.PlaylistUri.AbsoluteUri
            }.Normalize());
        }

        await _settingsStore.SaveAsync(
            new ContentSourceProviderSettings
            {
                Providers = persisted
            }.Normalize(),
            cancellationToken);
        return await LoadSettingsAsync(cancellationToken);
    }

    /// <param name="subtypeGroup">
    /// Keeps a search inside the shelf the user is browsing, so an anime
    /// provider cannot answer for a live-action title and the other way round.
    /// </param>
    public ValueTask<ContentSourceCatalogSearchResponse> SearchAsync(
        string query,
        MediaType? mediaType = null,
        MediaSubtypeGroup? subtypeGroup = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        return SearchAsync(
            new ContentSourceSearchCriteria(query, mediaType, subtypeGroup),
            cancellationToken);
    }

    public async ValueTask<ContentSourceCatalogSearchResponse> SearchAsync(
        ContentSourceSearchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ContentSourceCatalogSearchResult>();
        var failures = new List<ContentSourceSearchFailure>();
        await foreach (var batch in SearchStreamingAsync(
                           criteria,
                           cancellationToken))
        {
            results.AddRange(batch.Results);
            if (batch.Failure is not null)
            {
                failures.Add(batch.Failure);
            }
        }

        return new ContentSourceCatalogSearchResponse(results, failures);
    }

    public IAsyncEnumerable<ContentSourceCatalogSearchBatch>
        SearchStreamingAsync(
            string query,
            MediaType? mediaType = null,
            MediaSubtypeGroup? subtypeGroup = null,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        return SearchStreamingAsync(
            new ContentSourceSearchCriteria(query, mediaType, subtypeGroup),
            cancellationToken);
    }

    public async IAsyncEnumerable<ContentSourceCatalogSearchBatch>
        SearchStreamingAsync(
            ContentSourceSearchCriteria criteria,
            [EnumeratorCancellation] CancellationToken cancellationToken =
                default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        var settingsTask = LoadSettingsAsync(cancellationToken).AsTask();
        var overview = await _catalogBrowseService.GetOverviewAsync(
            criteria.MediaType,
            cancellationToken);
        var localResults = overview.Items
            .Where(card =>
                !ShouldHideAdultContent(card) &&
                MatchesSubtypeGroup(card, criteria.SubtypeGroup) &&
                criteria.Matches(card))
            .Select(ContentSourceCatalogSearchResult.Local)
            .ToArray();
        yield return new ContentSourceCatalogSearchBatch(
            ProviderId: null,
            ProviderName: "Local",
            IsLocal: true,
            localResults,
            Failure: null);

        var settings = await settingsTask;
        var configurations = settings.Providers
            .Where(provider =>
                IsProviderAvailableForSearch(
                    provider,
                    criteria.MediaType,
                    criteria.SubtypeGroup))
            .Where(provider => CanSatisfyCriteria(provider, criteria))
            .ToArray();
        var pending = configurations
            .Select(SearchProviderAsync)
            .ToList();

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);
            var (configuration, response) = await completed;
            yield return new ContentSourceCatalogSearchBatch(
                configuration.Id,
                configuration.DisplayName,
                IsLocal: false,
                FilterProviderResults(criteria, response.Results),
                response.Failure);
        }

        async Task<(ContentSourceProviderConfiguration Configuration,
            ProviderSearchResponse Response)> SearchProviderAsync(
            ContentSourceProviderConfiguration configuration)
        {
            var response = await SearchProviderSafelyAsync(
                configuration,
                criteria,
                cancellationToken);
            return (configuration, response);
        }
    }

    /// <summary>
    /// Drops a source that cannot honour every criterion, before it is asked.
    /// Skipping is the point: a source allowed to answer a broader question
    /// would return titles that quietly ignore the filter the user set.
    /// </summary>
    private bool CanSatisfyCriteria(
        ContentSourceProviderConfiguration configuration,
        ContentSourceSearchCriteria criteria)
    {
        if (!criteria.HasFilters)
        {
            // Plain text search, which every kind of source can answer.
            return true;
        }

        switch (configuration.Kind)
        {
            case ContentSourceProviderKind.Api:
                // Reading capabilities builds the provider, which is why this
                // is only reached once a filter is actually set.
                return !_apiProviders.TryGetValue(
                        configuration.Id,
                        out var registration) ||
                    registration.GetProvider().SearchCapabilities.CanSatisfy(
                        criteria.ToApiRequest());

            case ContentSourceProviderKind.TorrentCatalog:
                // No upstream filtering, so it answers on text and its results
                // are verified locally. With nothing to search for, it has no
                // way in.
                return !criteria.IsFilterOnly;

            default:
                return false;
        }
    }

    /// <summary>
    /// Applies the shelf the user is browsing. The criteria themselves are
    /// already settled by this point: the torrent catalog checks every one of
    /// them against the cards it built, and an API provider only ran at all
    /// after declaring it could honour them upstream. Re-checking a natively
    /// filtered API result here would discard titles whose search preview
    /// simply does not repeat the genre it was filtered on.
    /// </summary>
    private IReadOnlyList<ContentSourceCatalogSearchResult>
        FilterProviderResults(
            ContentSourceSearchCriteria criteria,
            IReadOnlyList<ContentSourceCatalogSearchResult> results) =>
        results
            .Where(result =>
                !ShouldHideAdultContent(result.Card) &&
                MatchesSubtypeGroup(result.Card, criteria.SubtypeGroup))
            .ToArray();

    public async ValueTask<ContentSourceDailyUpdateResult>
        RefreshDailyUpdatesAsync(
            CancellationToken cancellationToken = default)
    {
        var checkedProviderCount = 0;
        var skippedProviderCount = 0;
        var createdCount = 0;
        var updatedCount = 0;
        var unchangedCount = 0;
        var failures = new List<ContentSourceDailyUpdateFailure>();
        await foreach (var batch in RefreshDailyUpdatesStreamingAsync(
                           cancellationToken))
        {
            checkedProviderCount += batch.WasChecked ? 1 : 0;
            skippedProviderCount += batch.WasSkipped ? 1 : 0;
            createdCount += batch.CreatedCount;
            updatedCount += batch.UpdatedCount;
            unchangedCount += batch.UnchangedCount;
            if (batch.Failure is not null)
            {
                failures.Add(batch.Failure);
            }
        }

        return new ContentSourceDailyUpdateResult(
            checkedProviderCount,
            skippedProviderCount,
            createdCount,
            updatedCount,
            unchangedCount,
            failures);
    }

    public async IAsyncEnumerable<ContentSourceDailyUpdateBatch>
        RefreshDailyUpdatesStreamingAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken =
                default,
            bool forceRefresh = false)
    {
        await _dailyUpdateGate.WaitAsync(cancellationToken);
        try
        {
            var settings = await LoadSettingsAsync(cancellationToken);
            var configurations = settings.Providers
                .Where(static provider =>
                    provider.IsEnabled &&
                    provider.Kind is ContentSourceProviderKind.Api)
                .Where(provider =>
                    _apiProviders.TryGetValue(
                        provider.Id,
                        out var registration) &&
                    registration.SupportsDailyUpdates)
                .ToArray();
            var refreshStartedAt = _timeProvider.GetUtcNow();
            var minimumRefreshInterval = TimeSpan.FromHours(1);
            // Preparing every provider up front fetched all of their update
            // payloads at once and held each one until the serial import loop
            // below reached it, so peak memory was the sum of every provider's
            // payload rather than the few actually in flight. On a 2 GB
            // television that pushed the process to 581 MB against 76 MB of
            // free memory and a nearly full swap, and the resulting paging
            // stalls tripped the 5 second input dispatch deadline. Imports run
            // one at a time, so a small lookahead is all it takes to keep the
            // pipeline busy.
            var remaining = new Queue<ContentSourceProviderConfiguration>(
                configurations);
            var pending = new List<Task<PreparedDailyUpdate>>(
                MaximumPreparedDailyUpdateLookahead);
            while (pending.Count < MaximumPreparedDailyUpdateLookahead &&
                   remaining.Count > 0)
            {
                pending.Add(PrepareAsync(remaining.Dequeue()));
            }

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var completed = await Task.WhenAny(pending);
                pending.Remove(completed);
                // Refill before importing so the next fetch overlaps this
                // import instead of waiting for it.
                if (remaining.Count > 0)
                {
                    pending.Add(PrepareAsync(remaining.Dequeue()));
                }

                var prepared = await completed;
                if (prepared.WasSkipped || prepared.Failure is not null)
                {
                    yield return new ContentSourceDailyUpdateBatch(
                        prepared.Configuration.Id,
                        prepared.Configuration.DisplayName,
                        WasChecked: !prepared.WasSkipped,
                        prepared.WasSkipped,
                        CreatedCount: 0,
                        UpdatedCount: 0,
                        UnchangedCount: 0,
                        prepared.Failure);
                    continue;
                }

                CatalogImportResult? import = null;
                ContentSourceDailyUpdateFailure? failure = null;
                try
                {
                    import = await ImportDailyUpdatesAsync(
                        prepared.Configuration,
                        prepared.Provider!,
                        prepared.BaseUri!,
                        prepared.Updates!,
                        cancellationToken);
                    _sessionDailyUpdateRefreshes[prepared.SourceKey!] =
                        refreshStartedAt;
                }
                catch (Exception exception)
                    when (exception is not OperationCanceledException)
                {
                    TraceProviderFailure(
                        "daily-import",
                        prepared.Configuration,
                        exception);
                    failure = new ContentSourceDailyUpdateFailure(
                        prepared.Configuration.Id,
                        prepared.Configuration.DisplayName,
                        exception.Message);
                }

                yield return new ContentSourceDailyUpdateBatch(
                    prepared.Configuration.Id,
                    prepared.Configuration.DisplayName,
                    WasChecked: true,
                    WasSkipped: false,
                    import?.CreatedCount ?? 0,
                    import?.UpdatedCount ?? 0,
                    import?.UnchangedCount ?? 0,
                    failure);
            }

            async Task<PreparedDailyUpdate> PrepareAsync(
                ContentSourceProviderConfiguration configuration)
            {
                try
                {
                    var sourceKey = BuildDailyUpdateSourceKey(configuration);
                    if (!forceRefresh &&
                        _sessionDailyUpdateRefreshes.TryGetValue(
                            sourceKey,
                            out var lastSuccess) &&
                        refreshStartedAt - lastSuccess >= TimeSpan.Zero &&
                        refreshStartedAt - lastSuccess <
                        minimumRefreshInterval)
                    {
                        return PreparedDailyUpdate.Skipped(configuration);
                    }

                    var provider = GetApiProvider(configuration.Id);
                    var baseUri = new Uri(
                        configuration.BaseUrl,
                        UriKind.Absolute);
                    var updates = await provider.LoadDailyUpdatesAsync(
                        baseUri,
                        cancellationToken);
                    updates = updates
                        .Where(update =>
                            !ShouldHideAdultContent(update.PreviewItem))
                        .ToArray();
                    if (updates.Count > MaximumDailyUpdatesPerProvider)
                    {
                        throw new InvalidDataException(
                            $"{configuration.DisplayName} returned too many daily updates.");
                    }

                    return PreparedDailyUpdate.Ready(
                        configuration,
                        provider,
                        baseUri,
                        sourceKey,
                        updates);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    TraceProviderFailure(
                        "daily-fetch",
                        configuration,
                        exception);
                    return PreparedDailyUpdate.Failed(
                        configuration,
                        new ContentSourceDailyUpdateFailure(
                            configuration.Id,
                            configuration.DisplayName,
                            exception.Message));
                }
            }
        }
        finally
        {
            _dailyUpdateGate.Release();
        }
    }

    public async ValueTask<ContentSourceCatalogLoadResult> LoadOnlineItemAsync(
        ContentSourceCatalogSearchResult searchResult,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(searchResult);
        if (!searchResult.IsOnline ||
            searchResult.Provider is null ||
            string.IsNullOrWhiteSpace(searchResult.ExternalId))
        {
            throw new ArgumentException(
                "Only an online provider result can be loaded through this method.",
                nameof(searchResult));
        }

        var settings = await LoadSettingsAsync(cancellationToken);
        var configuration = settings.Providers.FirstOrDefault(provider =>
            string.Equals(
                provider.Id,
                searchResult.Provider.Id,
                StringComparison.Ordinal) &&
            provider.IsEnabled) ??
            throw new InvalidOperationException(
                "The selected online provider is no longer enabled.");
        var baseUri = new Uri(configuration.BaseUrl, UriKind.Absolute);
        CatalogTransferItem item;
        IReadOnlyList<ApiContentImageSet> imageSets = [];
        IReadOnlyList<ApiContentVideoSet> videoSets = [];
        IReadOnlyList<Uri> trustedArtworkOrigins = [baseUri];
        Uri? artworkReferrer = null;
        if (configuration.Kind is ContentSourceProviderKind.TorrentCatalog)
        {
            var document = await CatalogDocumentHttpReader.ReadAsync(
                _httpClient,
                baseUri,
                cancellationToken);
            item = document.Items.FirstOrDefault(candidate =>
                    string.Equals(
                        UmdJsonApiContentProvider.BuildExternalId(candidate),
                        searchResult.ExternalId,
                        StringComparison.Ordinal)) ??
                throw new InvalidOperationException(
                    "The selected title is no longer present in the provider catalog.");
        }
        else if (configuration.Kind is ContentSourceProviderKind.Api)
        {
            var provider = GetApiProvider(configuration.Id);
            var apiItem = await provider.LoadItemAsync(
                baseUri,
                searchResult.ApiSearchItem ??
                    throw new InvalidOperationException(
                        "The API search result no longer has a provider reference."),
                cancellationToken);
            item = apiItem.CatalogItem;
            if (string.IsNullOrWhiteSpace(item.ThumbnailRef) &&
                !string.IsNullOrWhiteSpace(
                    searchResult.ApiSearchItem?.PreviewItem.ThumbnailRef))
            {
                item = item with
                {
                    ThumbnailRef =
                        searchResult.ApiSearchItem.PreviewItem.ThumbnailRef
                };
            }

            imageSets = apiItem.ImageSets;
            videoSets = apiItem.VideoSets;
            trustedArtworkOrigins =
                GetTrustedArtworkOrigins(provider, baseUri);
            artworkReferrer = GetArtworkReferrer(
                provider,
                baseUri,
                item.ThumbnailRef);
        }
        else
        {
            throw new InvalidOperationException(
                "The manual provider does not expose online catalog results.");
        }

        var providerTitle = item.Name;
        EnsureAdultContentIsAllowed(item);
        item = AttachDistributor(item, configuration);
        var documentToImport = new CatalogTransferDocument
        {
            SourceLabel = configuration.DisplayName,
            Items = [item]
        };
        CatalogTransferValidator.Validate(documentToImport);
        ValidateImageSets(
            CatalogDetailsMapper.ToDetails(
                item,
                CreateStableGuid(
                    $"{configuration.Id}:{searchResult.ExternalId}:validation")),
            imageSets);
        ValidateVideoSets(
            CatalogDetailsMapper.ToDetails(
                item,
                CreateStableGuid(
                    $"{configuration.Id}:{searchResult.ExternalId}:video-validation")),
            videoSets);

        var catalogSnapshot =
            await _catalogTransferService.ExportMatchingDocumentAsync(
                [item], cancellationToken);
        var existingItem = CatalogTransferIdentity.FindBestMatch(
            catalogSnapshot.Items,
            item);
        if (existingItem is not null)
        {
            item = AlignStandaloneMovieUnits(existingItem, item);
            item = ApiCatalogItemMerger.Merge(existingItem, item);
            item = AttachDistributor(item, configuration);
            EnsureAdultContentIsAllowed(item);
            documentToImport.Items = [item];
            CatalogTransferValidator.Validate(documentToImport);
        }

        var import = await _catalogTransferService.ImportSelectedItemAsync(
            item,
            $"provider:{configuration.Id}",
            cancellationToken);
        var media = CatalogSubtypeResolver.ParseMediaType(item.MediaType);
        var overview = await _catalogBrowseService.GetOverviewAsync(
            media,
            cancellationToken);
        var importedCard = overview.Items.FirstOrDefault(card =>
                (media != MediaType.Book || string.Equals(card.SubtypeCode,
                    CatalogSubtypeResolver.ResolveCode(media, item.Subtype), StringComparison.OrdinalIgnoreCase)) &&
                string.Equals(
                    NameNormalizer.Normalize(card.Name),
                    NameNormalizer.Normalize(item.Name),
                    StringComparison.Ordinal)) ??
            throw new InvalidOperationException(
                "The provider title was imported but could not be found in the local catalog.");
        var details = await _catalogBrowseService.GetDetailsAsync(
                importedCard.Id,
                cancellationToken) ??
            throw new InvalidOperationException(
                "The provider title was imported but its details could not be loaded.");
        // The user selected this exact upstream title. Remember it now rather
        // than require name search to rediscover it when an episode is opened.
        // Metadata-only catalogs and torrent documents cannot serve units.
        if (configuration.Kind is ContentSourceProviderKind.Api && ProvidesUnitContent(configuration.Id))
        {
            await RememberTitleBindingAsync(details, configuration, searchResult.ApiSearchItem!, providerTitle,
                [], cancellationToken);
        }
        return new ContentSourceCatalogLoadResult(
            details,
            configuration.DisplayName,
            baseUri,
            trustedArtworkOrigins,
            imageSets,
            videoSets,
            import.CreatedCount,
            import.UpdatedCount,
            import.UnchangedCount,
            artworkReferrer);
    }

    public async ValueTask<ContentSourceCatalogRefreshResult?>
        RefreshApiItemAsync(
            CatalogItemDetails localDetails,
        IProgress<ContentSourceCatalogRefreshResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localDetails);
        if (localDetails.Item.MediaType is not (
                MediaType.Book or MediaType.Movie))
        {
            return null;
        }

        // What a title already carries is how a provider's answer gets checked,
        // so one holding units that are not episodes is left alone rather than
        // risk attaching the wrong show to it.
        //
        // A title holding nothing at all is the opposite case, and asking for
        // an episode first is circular: the units are the very thing the
        // refresh exists to fetch, so demanding one up front means it can never
        // get any. That was every title the daily fetch discovered — they
        // arrive as a name and a poster and stayed that way, which is what a
        // details page with nothing to select actually was.
        if (localDetails.Item.MediaType is MediaType.Movie &&
            HasRecordedUnits(localDetails) &&
            !localDetails.Units
                .Concat(localDetails.Seasons.SelectMany(
                    static season => season.Units))
                .Any(static unit => unit.Kind is ContentUnitKind.Episode))
        {
            TraceRefreshSkipped(localDetails, "no-episode-to-match-on");
            return null;
        }

        var settings = await LoadSettingsAsync(cancellationToken);
        var configurations = settings.Providers
            .Where(static provider =>
                provider.Kind is ContentSourceProviderKind.Api &&
                provider.IsEnabled)
            .Where(provider =>
                IsProviderAvailableForSearch(
                    provider,
                    localDetails.Item.MediaType,
                    localDetails.Item.MediaType is MediaType.Movie
                        ? MediaSubtypeGroups.Resolve(
                            localDetails.Item.SubtypeCode)
                        : null))
            .ToArray();
        if (configurations.Length == 0)
        {
            TraceRefreshSkipped(localDetails, "no-api-provider-for-media");
            return null;
        }

        var catalogSnapshot =
            await _catalogTransferService.ExportItemDocumentAsync(
                localDetails.Item.Id, cancellationToken);
        var localItem = catalogSnapshot.Items.SingleOrDefault(item =>
            MatchesLocalFormat(item, localDetails) &&
            string.Equals(
                NameNormalizer.Normalize(item.Name),
                localDetails.Item.NameKey,
                StringComparison.Ordinal));
        if (localItem is null ||
            !HasMissingAutomaticInformation(localItem))
        {
            TraceRefreshSkipped(
                localDetails,
                localItem is null ? "not-in-catalog" : "already-complete");
            return null;
        }

        var localNames = GetLocalTitleNames(localDetails);
        var pending = configurations
            .Select(configuration => ResolveApiItemSafelyAsync(
                configuration,
                localNames,
                localDetails,
                cancellationToken))
            .ToList();

        // Every provider is asked at once and answered as it replies, rather
        // than all of them awaited before anything is shown. The first usable
        // answer lands immediately, and a later one only replaces it if it is
        // actually better — which, for a title being caught up, usually means
        // it carries more episodes. Sources differ by hundreds of chapters and
        // the fullest is often the slowest, so waiting is worth it and making
        // the reader wait for it is not.
        //
        // Replacing costs nothing that was already applied: the import unions
        // units, so a richer answer adds what it knows on top of what is
        // already there instead of trading one list for another.
        MatchedApiItem? best = null;
        ContentSourceCatalogRefreshResult? applied = null;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);

            var resolution = await completed;
            if (resolution.Match is null ||
                best is not null && CompareMatches(resolution.Match, best) <= 0)
            {
                continue;
            }

            best = resolution.Match;
            var refreshed = await ApplyApiMatchAsync(
                localItem,
                localDetails,
                best,
                cancellationToken);
            if (refreshed is null)
            {
                continue;
            }

            // Later answers must build on information already imported, not
            // the sparse snapshot captured before the first provider replied.
            localItem = AttachDistributor(
                ApiCatalogItemMerger.MergeMissing(localItem, best.ApiItem.CatalogItem),
                best.Configuration);
            var artworkPolicies = GetArtworkPolicies(settings);
            refreshed = refreshed with
            {
                TrustedArtworkOrigins = artworkPolicies.Select(static policy => policy.Origin).Distinct().ToArray(),
                ArtworkReferrer = ResolveArtworkReferrer(refreshed.Details.Item.ThumbnailRef, artworkPolicies)
                    ?? refreshed.ArtworkReferrer
            };
            applied = refreshed;

            // Reported as it happens, so the page fills in from the first
            // answer and grows as the slower ones arrive.
            progress?.Report(refreshed);
        }

        if (applied is null)
        {
            TraceRefreshSkipped(
                localDetails,
                $"no-provider-match; providers={configurations.Length}");
        }

        return applied;
    }

    /// <summary>
    /// Which of two answers to keep. Title match first, because a fuller list
    /// of the wrong title helps nobody; among equally good matches the one
    /// carrying more episodes wins, which is the whole reason a slow provider
    /// is worth waiting for.
    /// </summary>
    private static int CompareMatches(
        MatchedApiItem candidate,
        MatchedApiItem current)
    {
        var similarity = candidate.TitleSimilarity.CompareTo(
            current.TitleSimilarity);
        if (similarity != 0)
        {
            return similarity;
        }

        var units = CountUnits(candidate.ApiItem.CatalogItem)
            .CompareTo(CountUnits(current.ApiItem.CatalogItem));
        if (units != 0)
        {
            return units;
        }

        var overlap = candidate.UnitOverlapCount.CompareTo(
            current.UnitOverlapCount);
        return overlap != 0
            ? overlap
            : string.Compare(
                current.ProviderName,
                candidate.ProviderName,
                StringComparison.OrdinalIgnoreCase);
    }

    private static int CountUnits(CatalogTransferItem item) =>
        item.Units.Count +
        item.Seasons.Sum(static season => season.Units.Count);

    /// <summary>
    /// Folds one provider's answer into the stored title and reads back what
    /// the page should now show.
    /// </summary>
    private async ValueTask<ContentSourceCatalogRefreshResult?>
        ApplyApiMatchAsync(
            CatalogTransferItem localItem,
            CatalogItemDetails localDetails,
            MatchedApiItem match,
            CancellationToken cancellationToken)
    {
        var mergedItem = AttachDistributor(
            ApiCatalogItemMerger.MergeMissing(
                localItem,
                match.ApiItem.CatalogItem),
            match.Configuration);
        var documentToImport = new CatalogTransferDocument
        {
            SourceLabel = match.ProviderName,
            Items = [mergedItem]
        };
        CatalogTransferValidator.Validate(documentToImport);
        var import = await _catalogTransferService.ImportAsync(
            documentToImport,
            $"provider:{match.ProviderId}:refresh",
            cancellationToken);
        var refreshedDetails =
            await _catalogBrowseService.GetDetailsAsync(
                localDetails.Item.Id,
                cancellationToken);
        if (refreshedDetails is null)
        {
            return null;
        }

        await RememberTitleBindingAsync(
            localDetails,
            match.Configuration,
            new ApiContentSearchItem(
                match.ExternalId,
                match.ApiItem.CatalogItem),
            match.ApiItem.CatalogItem.Name,
            [],
            cancellationToken);
        return new ContentSourceCatalogRefreshResult(
            refreshedDetails,
            match.ProviderId,
            match.ProviderName,
            match.ProviderBaseUri,
            GetTrustedArtworkOrigins(
                match.Provider,
                match.ProviderBaseUri),
            match.TitleSimilarity,
            import.CreatedCount,
            import.UpdatedCount,
            import.UnchangedCount,
            GetArtworkReferrer(
                match.Provider,
                match.ProviderBaseUri,
                refreshedDetails.Item.ThumbnailRef));
    }

    public async ValueTask<ApiContentUnitImageResolution>
        ResolveApiImagesForUnitAsync(
            CatalogItemDetails localDetails,
            CatalogContentUnitDetails localUnit,
            CancellationToken cancellationToken = default)
    {
        var candidates = await FindApiImageCandidatesForUnitAsync(
            localDetails,
            localUnit,
            cancellationToken);
        return await ResolveApiImagesForUnitAsync(
            localDetails,
            localUnit,
            candidates[0],
            cancellationToken);
    }

    public ValueTask<IReadOnlyList<ApiContentUnitImageCandidate>>
        FindApiImageCandidatesForUnitAsync(
            CatalogItemDetails localDetails,
            CatalogContentUnitDetails localUnit,
            CancellationToken cancellationToken = default) =>
        ExecuteApiUnitLookupAsync(
            "chapter-discovery",
            ApiUnitDiscoveryTimeout,
            token => FindApiImageCandidatesForUnitCoreAsync(
                localDetails,
                localUnit,
                token),
            cancellationToken);

    private async ValueTask<IReadOnlyList<ApiContentUnitImageCandidate>>
        FindApiImageCandidatesForUnitCoreAsync(
            CatalogItemDetails localDetails,
            CatalogContentUnitDetails localUnit,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(localDetails);
        ArgumentNullException.ThrowIfNull(localUnit);
        if (localDetails.Item.MediaType is not MediaType.Book)
        {
            throw new ArgumentException(
                "Dynamic API image resolution is available only for book and manga chapters.",
                nameof(localDetails));
        }

        var knownUnit = localDetails.Units
            .Concat(localDetails.Seasons.SelectMany(static season => season.Units))
            .FirstOrDefault(unit =>
                string.Equals(
                    unit.Identity.StableKey,
                    localUnit.Identity.StableKey,
                    StringComparison.Ordinal));
        if (knownUnit is null)
        {
            throw new ArgumentException(
                "The selected chapter does not belong to the supplied catalog item.",
                nameof(localUnit));
        }

        var settings = await LoadSettingsAsync(cancellationToken);
        var configurations = settings.Providers
            .Where(static provider =>
                provider.Kind is ContentSourceProviderKind.Api &&
                provider.IsEnabled)
            .Where(provider => ProvidesUnitContent(provider.Id))
            .Where(provider =>
                IsProviderAvailableForSearch(
                    provider,
                    MediaType.Book,
                    subtypeGroup: null))
            .ToArray();
        if (configurations.Length == 0)
        {
            throw new InvalidOperationException(
                "No API content provider is enabled.");
        }
        ApiUnitLookupDiagnostics.Current?.SetProviderCount(
            configurations.Length);

        var localNames = GetLocalTitleNames(localDetails);
        var bindings = await LoadTitleBindingsSafelyAsync(
            localDetails.Item.Id,
            cancellationToken);
        var rememberedConfigurations = configurations
            .Where(configuration => bindings.ContainsKey(configuration.Id))
            .OrderByDescending(configuration =>
                bindings[configuration.Id].LastUsedAt ??
                DateTimeOffset.MinValue)
            .ToArray();
        var rememberedProviderIds = rememberedConfigurations
            .Select(static configuration => configuration.Id)
            .ToHashSet(StringComparer.Ordinal);
        var orderedConfigurations = rememberedConfigurations
            .Concat(configurations.Where(configuration =>
                !rememberedProviderIds.Contains(configuration.Id)))
            .ToArray();
        var providerTasks = orderedConfigurations
            .Select(configuration =>
            {
                bindings.TryGetValue(
                    configuration.Id,
                    out var knownBinding);
                return ResolveApiImagesSafelyAsync(
                    configuration,
                    localNames,
                    knownUnit,
                    cancellationToken,
                    localDetails,
                    knownBinding);
            });
        var providerResults = (await Task.WhenAll(providerTasks)).ToList();
        var matches = providerResults
            .Where(static result => result.Match is not null)
            .Select(static result => result.Match!)
            .OrderByDescending(static result => result.TitleSimilarity)
            .ThenBy(
                static result => result.ProviderName,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (matches.Length > 0)
        {
            ApiUnitLookupDiagnostics.Current?.SetMatchCount(matches.Length);
            return matches
                .Select(static match =>
                    new ApiContentUnitImageCandidate(
                        match.Configuration,
                        match.ProviderBaseUri,
                        match.SearchItem,
                        match.ApiItem,
                        match.UnitRequest,
                        match.TitleSimilarity,
                        match.UnitRoutes,
                        match.UsedKnownBinding))
                .ToArray();
        }

        var failedCount = providerResults.Count(
            static result => result.Failed);
        throw new InvalidOperationException(
            failedCount == configurations.Length
                ? "Every enabled API provider failed while resolving the selected chapter."
                : "No enabled API provider found a sufficiently close manga title with the selected chapter.");
    }

    public ValueTask<ApiContentUnitImageResolution>
        ResolveApiImagesForUnitAsync(
            CatalogItemDetails localDetails,
            CatalogContentUnitDetails localUnit,
            ApiContentUnitImageCandidate candidate,
            CancellationToken cancellationToken = default) =>
        ExecuteApiUnitLookupAsync(
            "chapter-resolution",
            ApiUnitResolutionTimeout,
            token => ResolveApiImagesForUnitCoreAsync(
                localDetails,
                localUnit,
                candidate,
                token),
            cancellationToken);

    private async ValueTask<ApiContentUnitImageResolution>
        ResolveApiImagesForUnitCoreAsync(
            CatalogItemDetails localDetails,
            CatalogContentUnitDetails localUnit,
            ApiContentUnitImageCandidate candidate,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(localDetails);
        ArgumentNullException.ThrowIfNull(localUnit);
        ArgumentNullException.ThrowIfNull(candidate);
        ApiUnitLookupDiagnostics.Current?.SetSelectedProvider(
            candidate.ProviderId);
        var knownUnit = localDetails.Units
            .Concat(localDetails.Seasons.SelectMany(
                static season => season.Units))
            .FirstOrDefault(unit =>
                string.Equals(
                    unit.Identity.StableKey,
                    localUnit.Identity.StableKey,
                    StringComparison.Ordinal)) ??
            throw new ArgumentException(
                "The selected chapter does not belong to the supplied catalog item.",
                nameof(localUnit));
        if (localDetails.Item.MediaType is not MediaType.Book ||
            !MatchesLocalFormat(candidate.ApiItem.CatalogItem, localDetails) ||
            knownUnit.Kind is not ContentUnitKind.Chapter ||
            candidate.UnitRequest.Kind != knownUnit.Kind ||
            candidate.UnitRequest.SeasonNumber !=
            knownUnit.Identity.SeasonNumber ||
            !ApiContentUnitRequest.DisplayNumbersMatch(
                candidate.UnitRequest.DisplayNumber,
                knownUnit.DisplayNumber))
        {
            throw new ArgumentException(
                "The selected API provider result does not match this chapter.",
                nameof(candidate));
        }

        var provider = GetApiProvider(candidate.ProviderId);
        ApiContentImageSet? imageSet;
        try
        {
            imageSet = candidate.ApiItem.ImageSets.FirstOrDefault(
                candidate.UnitRequest.Matches);
            imageSet ??= await provider.LoadUnitImagesAsync(
                candidate.ProviderBaseUri,
                candidate.SearchItem,
                candidate.UnitRequest,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (candidate.UsedKnownBinding)
        {
            ApiUnitLookupDiagnostics.Current?.RecordProviderFailure(
                candidate.ProviderId,
                exception);
            return await RetryApiImageResolutionAfterStaleBindingAsync(
                localDetails,
                knownUnit,
                candidate,
                cancellationToken);
        }

        if (imageSet is null ||
            !imageSet.Matches(
                knownUnit.Kind,
                knownUnit.DisplayNumber,
                knownUnit.Identity.SeasonNumber))
        {
            if (candidate.UsedKnownBinding)
            {
                ApiUnitLookupDiagnostics.Current?.RecordProviderFailure(
                    candidate.ProviderId,
                    new InvalidDataException(
                        "The remembered chapter route no longer resolves."));
                return await RetryApiImageResolutionAfterStaleBindingAsync(
                    localDetails,
                    knownUnit,
                    candidate,
                    cancellationToken);
            }

            throw new InvalidOperationException(
                "The selected API provider no longer exposes this chapter.");
        }

        var learnedRoutes = GetKnownUnitRoutesSafely(
            provider,
            candidate.ProviderBaseUri,
            candidate.SearchItem,
            candidate.UnitRequest);
        var unitRoutes = MergeUnitRoutes(
            candidate.UnitRoutes,
            learnedRoutes);
        // This provider title answered for this manga, so later chapters go
        // straight to it instead of searching by name again.
        await RememberTitleBindingAsync(
            localDetails,
            candidate.Configuration,
            new ApiContentSearchItem(
                candidate.ExternalId,
                candidate.ApiItem.CatalogItem,
                unitRoutes),
            candidate.ApiItem.CatalogItem.Name,
            unitRoutes,
            cancellationToken);
        return await MergeMatchedApiItemAsync(
            localDetails,
            knownUnit,
            new MatchedApiUnit(
                candidate.Configuration,
                candidate.ProviderBaseUri,
                provider,
                candidate.ApiItem,
                imageSet,
                candidate.TitleSimilarity,
                candidate.ExternalId,
                unitRoutes),
            cancellationToken);
    }

    private async ValueTask<ApiContentUnitImageResolution>
        RetryApiImageResolutionAfterStaleBindingAsync(
            CatalogItemDetails localDetails,
            CatalogContentUnitDetails localUnit,
            ApiContentUnitImageCandidate staleCandidate,
            CancellationToken cancellationToken)
    {
        await ForgetTitleBindingAsync(
            localDetails,
            staleCandidate.Configuration,
            cancellationToken);
        var result = await ResolveApiImagesSafelyAsync(
            staleCandidate.Configuration,
            GetLocalTitleNames(localDetails),
            localUnit,
            cancellationToken,
            localDetails,
            knownBinding: null);
        var match = result.Match ??
            throw new InvalidOperationException(
                "The remembered chapter route is stale and the provider " +
                "could not find the title again.");
        return await ResolveApiImagesForUnitCoreAsync(
            localDetails,
            localUnit,
            new ApiContentUnitImageCandidate(
                match.Configuration,
                match.ProviderBaseUri,
                match.SearchItem,
                match.ApiItem,
                match.UnitRequest,
                match.TitleSimilarity,
                match.UnitRoutes,
                UsedKnownBinding: false),
            cancellationToken);
    }

    public async ValueTask<ApiContentUnitVideoResolution>
        ResolveApiVideosForUnitAsync(
            CatalogItemDetails localDetails,
            CatalogContentUnitDetails localUnit,
            CancellationToken cancellationToken = default)
    {
        var candidates = await FindApiVideoCandidatesForUnitAsync(
            localDetails,
            localUnit,
            cancellationToken);
        return await ResolveApiVideosForUnitAsync(
            localDetails,
            localUnit,
            candidates[0],
            cancellationToken);
    }

    public ValueTask<IReadOnlyList<ApiContentUnitVideoCandidate>>
        FindApiVideoCandidatesForUnitAsync(
            CatalogItemDetails localDetails,
            CatalogContentUnitDetails localUnit,
            CancellationToken cancellationToken = default) =>
        FindApiVideoCandidatesForUnitAsync(
            localDetails,
            localUnit,
            onCandidateDiscovered: null,
            cancellationToken);

    /// <summary>
    /// Discovers every matching provider and reports each match as soon as that
    /// provider finishes. A saved route for the most recently used provider is
    /// still tried first so the caller can open it before the remaining
    /// providers are searched.
    /// </summary>
    public ValueTask<IReadOnlyList<ApiContentUnitVideoCandidate>>
        FindApiVideoCandidatesForUnitAsync(
            CatalogItemDetails localDetails,
            CatalogContentUnitDetails localUnit,
            Func<
                ApiContentUnitVideoCandidate,
                CancellationToken,
                ValueTask>? onCandidateDiscovered,
            CancellationToken cancellationToken = default) =>
        ExecuteApiUnitLookupAsync(
            "episode-discovery",
            ApiUnitDiscoveryTimeout,
            token => FindApiVideoCandidatesForUnitCoreAsync(
                localDetails,
                localUnit,
                onCandidateDiscovered,
                token),
            cancellationToken);

    private async ValueTask<IReadOnlyList<ApiContentUnitVideoCandidate>>
        FindApiVideoCandidatesForUnitCoreAsync(
            CatalogItemDetails localDetails,
            CatalogContentUnitDetails localUnit,
            Func<
                ApiContentUnitVideoCandidate,
                CancellationToken,
                ValueTask>? onCandidateDiscovered,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(localDetails);
        ArgumentNullException.ThrowIfNull(localUnit);
        if (localDetails.Item.MediaType is not MediaType.Movie ||
            localUnit.Kind is not ContentUnitKind.Episode)
        {
            throw new ArgumentException(
                "Dynamic API video resolution is available only for anime and series episodes.",
                nameof(localDetails));
        }

        var knownUnit = localDetails.Units
            .Concat(localDetails.Seasons.SelectMany(static season => season.Units))
            .FirstOrDefault(unit =>
                string.Equals(
                    unit.Identity.StableKey,
                    localUnit.Identity.StableKey,
                    StringComparison.Ordinal));
        if (knownUnit is null)
        {
            throw new ArgumentException(
                "The selected episode does not belong to the supplied catalog item.",
                nameof(localUnit));
        }

        var settings = await LoadSettingsAsync(cancellationToken);
        var videoProviders = settings.Providers
            .Where(static provider =>
                provider.Kind is ContentSourceProviderKind.Api &&
                provider.IsEnabled)
            .Where(provider => ProvidesUnitContent(provider.Id))
            .Where(provider =>
                IsProviderAvailableForSearch(
                    provider,
                    MediaType.Movie,
                    subtypeGroup: null))
            .ToArray();
        var configurations = videoProviders
            .Where(provider =>
                IsProviderAvailableForSearch(
                    provider,
                    MediaType.Movie,
                    MediaSubtypeGroups.Resolve(
                        localDetails.Item.SubtypeCode)))
            .ToArray();

        // Scope splits the providers in two and MediaSubtypeGroups.Resolve has
        // no unknown case: anything that is not tagged as anime counts as live
        // action, including a title with no subtype at all. A provider outside
        // the resolved half was therefore never asked, even when it carried
        // the episode — some providers list Breaking Bad and The Last of Us under
        // /latino/ but is scoped to anime, so no live-action title ever
        // reached it. The other half is kept as a second pass rather than
        // merged, so a correctly tagged title still costs exactly the requests
        // it did before and only a miss widens the search.
        var scopedProviderIds = configurations
            .Select(static configuration => configuration.Id)
            .ToHashSet(StringComparer.Ordinal);
        var fallbackConfigurations = videoProviders
            .Where(provider => !scopedProviderIds.Contains(provider.Id))
            .ToArray();
        if (configurations.Length == 0)
        {
            configurations = fallbackConfigurations;
            fallbackConfigurations = [];
        }

        if (configurations.Length == 0)
        {
            throw new InvalidOperationException(
                "No API content provider is enabled.");
        }
        ApiUnitLookupDiagnostics.Current?.SetProviderCount(
            configurations.Length + fallbackConfigurations.Length);

        var localNames = GetLocalTitleNames(localDetails);
        var bindings = await LoadTitleBindingsSafelyAsync(
            localDetails.Item.Id,
            cancellationToken);
        var rememberedConfigurations = configurations
            .Where(configuration => bindings.ContainsKey(configuration.Id))
            .OrderByDescending(configuration =>
                bindings[configuration.Id].LastUsedAt ??
                DateTimeOffset.MinValue)
            .ToArray();
        var rememberedProviderIds = rememberedConfigurations
            .Select(static configuration => configuration.Id)
            .ToHashSet(StringComparer.Ordinal);
        var orderedConfigurations = rememberedConfigurations
            .Concat(configurations.Where(configuration =>
                !rememberedProviderIds.Contains(configuration.Id)))
            .ToArray();
        var providerResults = new List<UnitVideoProviderResolution>(
            orderedConfigurations.Length);
        IEnumerable<ContentSourceProviderConfiguration>
            remainingConfigurations = orderedConfigurations;

        // Do not put the remembered fast path behind the three-provider gate.
        // It resolves alone, the caller can open it, and only then do the
        // remaining providers start. They are still all searched so their
        // alternatives remain available.
        var preferredRememberedConfiguration =
            onCandidateDiscovered is null
                ? null
                : rememberedConfigurations.FirstOrDefault(configuration =>
                    bindings[configuration.Id].UnitRoutes.Any(
                        route => MatchesUnitRoute(route, knownUnit)));
        if (preferredRememberedConfiguration is not null)
        {
            var knownBinding =
                bindings[preferredRememberedConfiguration.Id];
            var preferredResult = await ResolveApiVideosSafelyAsync(
                preferredRememberedConfiguration,
                localNames,
                knownUnit,
                cancellationToken,
                localDetails,
                knownBinding);
            providerResults.Add(preferredResult);
            remainingConfigurations = orderedConfigurations.Where(
                configuration => !string.Equals(
                    configuration.Id,
                    preferredRememberedConfiguration.Id,
                    StringComparison.Ordinal));

            if (preferredResult.Match is { } preferredMatch)
            {
                await onCandidateDiscovered!(
                    CreateVideoCandidate(preferredMatch),
                    cancellationToken);
            }
        }

        async Task SweepAsync(
            IEnumerable<ContentSourceProviderConfiguration> sweepConfigurations)
        {
            var providerTasks = sweepConfigurations
                .Select(configuration =>
                {
                    bindings.TryGetValue(
                        configuration.Id,
                        out var knownBinding);
                    return ResolveApiVideosSafelyAsync(
                        configuration,
                        localNames,
                        knownUnit,
                        cancellationToken,
                        localDetails,
                        knownBinding);
                })
                .ToList();
            if (onCandidateDiscovered is null)
            {
                providerResults.AddRange(await Task.WhenAll(providerTasks));
                return;
            }

            while (providerTasks.Count > 0)
            {
                var completedTask = await Task.WhenAny(providerTasks);
                providerTasks.Remove(completedTask);
                var result = await completedTask;
                providerResults.Add(result);
                if (result.Match is { } match)
                {
                    await onCandidateDiscovered(
                        CreateVideoCandidate(match),
                        cancellationToken);
                }
            }
        }

        await SweepAsync(remainingConfigurations);
        if (providerResults.All(static result => result.Match is null) &&
            fallbackConfigurations.Length > 0)
        {
            await SweepAsync(fallbackConfigurations);
        }

        var matches = providerResults
            .Where(static result => result.Match is not null)
            .Select(static result => result.Match!)
            .OrderByDescending(static result => result.TitleSimilarity)
            .ThenBy(
                static result => result.ProviderName,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (matches.Length > 0)
        {
            ApiUnitLookupDiagnostics.Current?.SetMatchCount(matches.Length);
            return matches
                .Select(CreateVideoCandidate)
                .ToArray();
        }

        var failedCount = providerResults.Count(
            static result => result.Failed);
        throw new InvalidOperationException(
            failedCount == providerResults.Count
                ? "Every enabled API provider failed while resolving the selected episode."
                : "No enabled API provider found a sufficiently close video title with the selected episode.");
    }

    private static ApiContentUnitVideoCandidate CreateVideoCandidate(
        DiscoveredApiVideoUnit match) =>
        new(
            match.Configuration,
            match.ProviderBaseUri,
            match.SearchItem,
            match.ApiItem,
            match.UnitRequest,
            match.TitleSimilarity,
            match.UnitRoutes,
            match.UsedKnownBinding);

    public ValueTask<ApiContentUnitVideoResolution>
        ResolveApiVideosForUnitAsync(
            CatalogItemDetails localDetails,
            CatalogContentUnitDetails localUnit,
            ApiContentUnitVideoCandidate candidate,
            CancellationToken cancellationToken = default) =>
        ExecuteApiUnitLookupAsync(
            "episode-resolution",
            ApiUnitResolutionTimeout,
            token => ResolveApiVideosForUnitCoreAsync(
                localDetails,
                localUnit,
                candidate,
                token),
            cancellationToken);

    private async ValueTask<ApiContentUnitVideoResolution>
        ResolveApiVideosForUnitCoreAsync(
            CatalogItemDetails localDetails,
            CatalogContentUnitDetails localUnit,
            ApiContentUnitVideoCandidate candidate,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(localDetails);
        ArgumentNullException.ThrowIfNull(localUnit);
        ArgumentNullException.ThrowIfNull(candidate);
        ApiUnitLookupDiagnostics.Current?.SetSelectedProvider(
            candidate.ProviderId);
        var knownUnit = localDetails.Units
            .Concat(localDetails.Seasons.SelectMany(
                static season => season.Units))
            .FirstOrDefault(unit =>
                string.Equals(
                    unit.Identity.StableKey,
                    localUnit.Identity.StableKey,
                    StringComparison.Ordinal)) ??
            throw new ArgumentException(
                "The selected episode does not belong to the supplied catalog item.",
                nameof(localUnit));
        var isStandaloneMovie = IsStandaloneMovie(localDetails);
        if (localDetails.Item.MediaType is not MediaType.Movie ||
            knownUnit.Kind is not ContentUnitKind.Episode ||
            candidate.UnitRequest.Kind is not ContentUnitKind.Episode ||
            !isStandaloneMovie &&
            (candidate.UnitRequest.SeasonNumber !=
                 knownUnit.Identity.SeasonNumber ||
             !ApiContentUnitRequest.DisplayNumbersMatch(
                 candidate.UnitRequest.DisplayNumber,
                 knownUnit.DisplayNumber)))
        {
            throw new ArgumentException(
                "The selected API provider result does not match this episode.",
                nameof(candidate));
        }

        var provider = GetApiProvider(candidate.ProviderId);
        ApiContentVideoSet? videoSet;
        try
        {
            videoSet = candidate.ApiItem.VideoSets.FirstOrDefault(
                candidate.UnitRequest.Matches);
            videoSet ??= await provider.LoadUnitVideosAsync(
                candidate.ProviderBaseUri,
                candidate.SearchItem,
                candidate.UnitRequest,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (candidate.UsedKnownBinding)
        {
            ApiUnitLookupDiagnostics.Current?.RecordProviderFailure(
                candidate.ProviderId,
                exception);
            return await RetryApiVideoResolutionAfterStaleBindingAsync(
                localDetails,
                knownUnit,
                candidate,
                cancellationToken);
        }

        if (videoSet is null ||
            !isStandaloneMovie &&
            !candidate.UnitRequest.Matches(videoSet))
        {
            if (candidate.UsedKnownBinding)
            {
                ApiUnitLookupDiagnostics.Current?.RecordProviderFailure(
                    candidate.ProviderId,
                    new InvalidDataException(
                        "The remembered episode route no longer resolves."));
                return await RetryApiVideoResolutionAfterStaleBindingAsync(
                    localDetails,
                    knownUnit,
                    candidate,
                    cancellationToken);
            }

            throw new InvalidOperationException(
                "The selected API provider no longer exposes this episode.");
        }

        var learnedRoutes = GetKnownUnitRoutesSafely(
            provider,
            candidate.ProviderBaseUri,
            candidate.SearchItem,
            candidate.UnitRequest);
        var unitRoutes = MergeUnitRoutes(
            candidate.UnitRoutes,
            learnedRoutes);
        // This provider title answered for this item, so later episodes go
        // straight to it instead of searching by name again.
        await RememberTitleBindingAsync(
            localDetails,
            candidate.Configuration,
            new ApiContentSearchItem(
                candidate.ExternalId,
                candidate.ApiItem.CatalogItem,
                unitRoutes),
            candidate.ApiItem.CatalogItem.Name,
            unitRoutes,
            cancellationToken);
        return await MergeMatchedApiVideoItemAsync(
            localDetails,
            knownUnit,
            new MatchedApiVideoUnit(
                candidate.Configuration,
                candidate.ProviderBaseUri,
                provider,
                candidate.ApiItem,
                videoSet,
                candidate.TitleSimilarity,
                candidate.ExternalId,
                unitRoutes),
            cancellationToken);
    }

    private async ValueTask<ApiContentUnitVideoResolution>
        RetryApiVideoResolutionAfterStaleBindingAsync(
            CatalogItemDetails localDetails,
            CatalogContentUnitDetails localUnit,
            ApiContentUnitVideoCandidate staleCandidate,
            CancellationToken cancellationToken)
    {
        await ForgetTitleBindingAsync(
            localDetails,
            staleCandidate.Configuration,
            cancellationToken);
        var result = await ResolveApiVideosSafelyAsync(
            staleCandidate.Configuration,
            GetLocalTitleNames(localDetails),
            localUnit,
            cancellationToken,
            localDetails,
            knownBinding: null);
        var match = result.Match ??
            throw new InvalidOperationException(
                "The remembered episode route is stale and the provider " +
                "could not find the title again.");
        return await ResolveApiVideosForUnitCoreAsync(
            localDetails,
            localUnit,
            new ApiContentUnitVideoCandidate(
                match.Configuration,
                match.ProviderBaseUri,
                match.SearchItem,
                match.ApiItem,
                match.UnitRequest,
                match.TitleSimilarity,
                match.UnitRoutes,
                UsedKnownBinding: false),
            cancellationToken);
    }

    public static ContentSourceProviderConfiguration CreateTorrentProvider(
        string displayName,
        string baseUrl) =>
        new ContentSourceProviderConfiguration
        {
            Id = $"torrent.{Guid.NewGuid():N}",
            DisplayName = displayName,
            Kind = ContentSourceProviderKind.TorrentCatalog,
            IsEnabled = true,
            BaseUrl = baseUrl
        }.Normalize();

    private async Task<ProviderSearchResponse> SearchProviderSafelyAsync(
        ContentSourceProviderConfiguration configuration,
        ContentSourceSearchCriteria criteria,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = configuration.Kind switch
            {
                // Only reached with query text: CanSatisfyCriteria keeps the
                // torrent catalog out of filter-only searches.
                ContentSourceProviderKind.TorrentCatalog =>
                    await SearchTorrentCatalogAsync(
                        configuration,
                        criteria,
                        cancellationToken),
                ContentSourceProviderKind.Api =>
                    await SearchApiAsync(
                        configuration,
                        criteria,
                        cancellationToken),
                _ => []
            };
            return new ProviderSearchResponse(results, Failure: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            TraceProviderFailure(
                "search",
                configuration,
                exception);
            return new ProviderSearchResponse(
                [],
                new ContentSourceSearchFailure(
                    configuration.Id,
                    configuration.DisplayName,
                    exception.Message));
        }
    }

    private async Task<UnitImageProviderResolution>
        ResolveApiImagesSafelyAsync(
            ContentSourceProviderConfiguration configuration,
            IReadOnlyList<string> localNames,
            CatalogContentUnitDetails localUnit,
            CancellationToken cancellationToken,
            CatalogItemDetails? localDetails = null,
            ApiTitleBinding? knownBinding = null)
    {
        try
        {
            var provider = GetApiProvider(configuration.Id);
            var baseUri = new Uri(configuration.BaseUrl, UriKind.Absolute);
            var normalizedLocalNames = NormalizeTitleNames(localNames);
            var candidates = new Dictionary<string, RankedApiCandidate>(
                StringComparer.Ordinal);

            // A title already confirmed for this provider is tried first and,
            // when it still carries the chapter, skips name search for this
            // provider. Other providers still run so the picker keeps every
            // available alternative.
            if (localDetails is not null && knownBinding is not null)
            {
                var bound = CreateBoundSearchItem(
                    localDetails,
                    knownBinding,
                    localUnit);
                var boundMatch = await TryMatchApiImageUnitAsync(
                    configuration,
                    provider,
                    baseUri,
                    bound,
                    normalizedLocalNames,
                    localUnit,
                    requireTitleSimilarity: false,
                    cancellationToken,
                    expectedBookSubtype: localDetails.Item.SubtypeCode);
                if (boundMatch is not null)
                {
                    return new UnitImageProviderResolution(
                        boundMatch,
                        Failed: false,
                        UsedKnownBinding: true);
                }

                // The pairing no longer resolves, so it is dropped and the
                // normal search runs below.
                await ForgetTitleBindingAsync(
                    localDetails,
                    configuration,
                    cancellationToken);
            }
            foreach (var query in localNames.Take(4))
            {
                var results = await provider.SearchAsync(
                    baseUri,
                    query,
                    MediaType.Book,
                    cancellationToken);
                var foundExactTitle = false;
                foreach (var candidate in results.Take(100))
                {
                    if (candidates.ContainsKey(candidate.ExternalId) ||
                        localDetails is not null && !MatchesLocalFormat(candidate.PreviewItem, localDetails))
                    {
                        continue;
                    }

                    var providerTitleNames =
                        GetProviderTitleNames(candidate.PreviewItem);
                    var similarity = GetBestTitleSimilarityFromNormalized(
                        normalizedLocalNames,
                        providerTitleNames);
                    candidates.Add(
                        candidate.ExternalId,
                        new RankedApiCandidate(
                            candidate,
                            similarity,
                            GetBestTitleLengthGapFromNormalized(
                                normalizedLocalNames,
                                providerTitleNames)));
                    foundExactTitle |= similarity >= 0.999_999;
                }

                if (foundExactTitle)
                {
                    break;
                }
            }

            var rankedCandidates = RankApiCandidates(candidates.Values);

            // See the video path: the leader is a best guess among titles that
            // tie on containment, so a miss falls through to the runner-up
            // rather than reporting the chapter as unavailable.
            DiscoveredApiImageUnit? best = null;
            foreach (var candidate in rankedCandidates)
            {
                best = await TryMatchApiImageUnitAsync(
                    configuration,
                    provider,
                    baseUri,
                    candidate.SearchItem,
                    normalizedLocalNames,
                    localUnit,
                    requireTitleSimilarity: true,
                    cancellationToken,
                    expectedBookSubtype: localDetails?.Item.SubtypeCode);
                if (best is not null)
                {
                    break;
                }
            }

            return new UnitImageProviderResolution(
                best,
                Failed: false,
                UsedKnownBinding: false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ApiUnitLookupDiagnostics.Current?.RecordProviderFailure(
                configuration.Id,
                exception);
            return new UnitImageProviderResolution(
                Match: null,
                Failed: true,
                UsedKnownBinding: false);
        }
    }

    /// <summary>
    /// Loads one provider title and returns it only when it really carries the
    /// requested chapter. <paramref name="requireTitleSimilarity"/> is false for
    /// a remembered pairing, which the user has already accepted.
    /// </summary>
    private async ValueTask<DiscoveredApiImageUnit?>
        TryMatchApiImageUnitAsync(
        ContentSourceProviderConfiguration configuration,
        IApiContentProvider provider,
        Uri baseUri,
        ApiContentSearchItem searchItem,
        IReadOnlyList<string> normalizedLocalNames,
        CatalogContentUnitDetails localUnit,
        bool requireTitleSimilarity,
        CancellationToken cancellationToken,
        string? expectedBookSubtype)
    {
        var unitRequest = new ApiContentUnitRequest(
            localUnit.Kind,
            localUnit.DisplayNumber,
            localUnit.Identity.SeasonNumber,
            localUnit.Name);
        ApiContentItem apiItem;
        double titleSimilarity;
        try
        {
            apiItem = await provider.LoadItemForUnitAsync(
                baseUri,
                searchItem,
                unitRequest,
                cancellationToken);
            if (ShouldHideAdultContent(apiItem.CatalogItem))
            {
                return null;
            }
            var validationDocument = new CatalogTransferDocument
            {
                SourceLabel = configuration.DisplayName,
                Items = [apiItem.CatalogItem]
            };
            CatalogTransferValidator.Validate(validationDocument);
            var providerDetails = CatalogDetailsMapper.ToDetails(
                apiItem.CatalogItem,
                CreateStableGuid(
                    $"{configuration.Id}:{searchItem.ExternalId}:dynamic-validation"));
            if (providerDetails.Item.MediaType is not MediaType.Book ||
                expectedBookSubtype is not null && !string.Equals(
                    providerDetails.Item.SubtypeCode, expectedBookSubtype, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            ValidateImageSets(providerDetails, apiItem.ImageSets);
            var providerNames = GetProviderTitleNames(apiItem.CatalogItem);
            titleSimilarity = GetBestTitleSimilarityFromNormalized(
                normalizedLocalNames,
                providerNames);
            if ((requireTitleSimilarity &&
                 titleSimilarity <
                 LevenshteinTitleMatcher.DefaultMinimumSimilarity) ||
                !IsRememberedTitlePlausible(searchItem, providerNames) ||
                FindMatchingUnit(providerDetails, unitRequest) is null)
            {
                return null;
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ApiUnitLookupDiagnostics.Current?.RecordProviderFailure(
                configuration.Id,
                exception);
            return null;
        }

        var unitRoutes = GetKnownUnitRoutesSafely(
            provider,
            baseUri,
            searchItem,
            unitRequest);
        return new DiscoveredApiImageUnit(
            configuration,
            baseUri,
            apiItem,
            searchItem,
            unitRequest,
            titleSimilarity,
            unitRoutes,
            UsedKnownBinding: !requireTitleSimilarity);
    }

    private async Task<UnitVideoProviderResolution>
        ResolveApiVideosSafelyAsync(
            ContentSourceProviderConfiguration configuration,
            IReadOnlyList<string> localNames,
            CatalogContentUnitDetails localUnit,
            CancellationToken cancellationToken,
            CatalogItemDetails? localDetails = null,
            ApiTitleBinding? knownBinding = null)
    {
        try
        {
            var provider = GetApiProvider(configuration.Id);
            var baseUri = new Uri(configuration.BaseUrl, UriKind.Absolute);
            var isStandaloneMovie = localDetails is not null &&
                IsStandaloneMovie(localDetails);
            var normalizedLocalNames = NormalizeTitleNames(localNames);
            var candidates = new Dictionary<string, RankedApiCandidate>(
                StringComparer.Ordinal);

            // A title already confirmed for this provider is tried first and,
            // when it still carries the unit, skips name search for this
            // provider. Other providers still run so alternatives remain
            // visible.
            if (localDetails is not null && knownBinding is not null)
            {
                var bound = CreateBoundSearchItem(
                    localDetails,
                    knownBinding,
                    localUnit);
                var boundMatch = await TryMatchApiVideoUnitAsync(
                    configuration,
                    provider,
                    baseUri,
                    bound,
                    normalizedLocalNames,
                    localUnit,
                    isStandaloneMovie,
                    requireTitleSimilarity: false,
                    cancellationToken);
                if (boundMatch is not null)
                {
                    return new UnitVideoProviderResolution(
                        boundMatch,
                        Failed: false,
                        UsedKnownBinding: true);
                }

                // The pairing no longer resolves, so it is dropped and the
                // normal search runs below.
                await ForgetTitleBindingAsync(
                    localDetails,
                    configuration,
                    cancellationToken);
            }
            foreach (var query in localNames.Take(4))
            {
                var results = await provider.SearchAsync(
                    baseUri,
                    query,
                    MediaType.Movie,
                    cancellationToken);
                var foundExactTitle = false;
                foreach (var candidate in results.Take(100))
                {
                    if (candidates.ContainsKey(candidate.ExternalId))
                    {
                        continue;
                    }

                    var providerTitleNames =
                        GetProviderTitleNames(candidate.PreviewItem);
                    var similarity = GetBestTitleSimilarityFromNormalized(
                        normalizedLocalNames,
                        providerTitleNames);
                    candidates.Add(
                        candidate.ExternalId,
                        new RankedApiCandidate(
                            candidate,
                            similarity,
                            GetBestTitleLengthGapFromNormalized(
                                normalizedLocalNames,
                                providerTitleNames)));
                    foundExactTitle |= similarity >= 0.999_999;
                }

                if (foundExactTitle)
                {
                    break;
                }
            }

            var rankedCandidates = RankApiCandidates(candidates.Values);

            // Every candidate here cleared the similarity floor, and
            // containment makes several of them tie, so the leader is a
            // best guess rather than a certainty. Taking only the leader meant
            // one wrong guess — a same-named film, a spin-off — reported the
            // title as unavailable while the episode sat on the runner-up.
            DiscoveredApiVideoUnit? best = null;
            foreach (var candidate in rankedCandidates)
            {
                best = await TryMatchApiVideoUnitAsync(
                    configuration,
                    provider,
                    baseUri,
                    candidate.SearchItem,
                    normalizedLocalNames,
                    localUnit,
                    isStandaloneMovie,
                    requireTitleSimilarity: true,
                    cancellationToken);
                if (best is not null)
                {
                    break;
                }
            }

            return new UnitVideoProviderResolution(
                best,
                Failed: false,
                UsedKnownBinding: false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ApiUnitLookupDiagnostics.Current?.RecordProviderFailure(
                configuration.Id,
                exception);
            return new UnitVideoProviderResolution(
                Match: null,
                Failed: true,
                UsedKnownBinding: false);
        }
    }

    /// <summary>
    /// Loads one provider title and returns it only when it really carries the
    /// requested unit. <paramref name="requireTitleSimilarity"/> is false for a
    /// remembered pairing, which the user has already accepted.
    /// </summary>
    private async ValueTask<DiscoveredApiVideoUnit?>
        TryMatchApiVideoUnitAsync(
        ContentSourceProviderConfiguration configuration,
        IApiContentProvider provider,
        Uri baseUri,
        ApiContentSearchItem searchItem,
        IReadOnlyList<string> normalizedLocalNames,
        CatalogContentUnitDetails localUnit,
        bool isStandaloneMovie,
        bool requireTitleSimilarity,
        CancellationToken cancellationToken)
    {
        ApiContentItem apiItem;
        double titleSimilarity;
        ApiContentUnitRequest unitRequest;
        try
        {
            var discoveryRequest = isStandaloneMovie
                ? new ApiContentUnitRequest(
                    ContentUnitKind.Episode,
                    "1",
                    seasonNumber: null,
                    localUnit.Name)
                : new ApiContentUnitRequest(
                    localUnit.Kind,
                    localUnit.DisplayNumber,
                    localUnit.Identity.SeasonNumber,
                    localUnit.Name);
            apiItem = await provider.LoadItemForUnitAsync(
                baseUri,
                searchItem,
                discoveryRequest,
                cancellationToken);
            if (ShouldHideAdultContent(apiItem.CatalogItem))
            {
                return null;
            }
            var validationDocument = new CatalogTransferDocument
            {
                SourceLabel = configuration.DisplayName,
                Items = [apiItem.CatalogItem]
            };
            CatalogTransferValidator.Validate(validationDocument);
            var providerDetails = CatalogDetailsMapper.ToDetails(
                apiItem.CatalogItem,
                CreateStableGuid(
                    $"{configuration.Id}:{searchItem.ExternalId}:video-dynamic-validation"));
            if (providerDetails.Item.MediaType is not MediaType.Movie ||
                isStandaloneMovie &&
                !CatalogTransferIdentity.IsStandaloneMovie(
                    apiItem.CatalogItem))
            {
                return null;
            }

            ValidateVideoSets(providerDetails, apiItem.VideoSets);
            var providerNames = GetProviderTitleNames(apiItem.CatalogItem);
            titleSimilarity = GetBestTitleSimilarityFromNormalized(
                normalizedLocalNames,
                providerNames);
            if ((requireTitleSimilarity &&
                 titleSimilarity <
                 LevenshteinTitleMatcher.DefaultMinimumSimilarity) ||
                !IsRememberedTitlePlausible(searchItem, providerNames))
            {
                return null;
            }

            if (isStandaloneMovie)
            {
                var providerUnit = providerDetails.Units
                    .Concat(providerDetails.Seasons.SelectMany(
                        static season => season.Units))
                    .FirstOrDefault();
                if (providerUnit is null)
                {
                    return null;
                }

                unitRequest = new ApiContentUnitRequest(
                    providerUnit.Kind,
                    providerUnit.DisplayNumber,
                    providerUnit.Identity.SeasonNumber,
                    providerUnit.Name);
            }
            else
            {
                unitRequest = new ApiContentUnitRequest(
                    localUnit.Kind,
                    localUnit.DisplayNumber,
                    localUnit.Identity.SeasonNumber,
                    localUnit.Name);
                if (FindMatchingUnit(providerDetails, unitRequest) is null)
                {
                    return null;
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ApiUnitLookupDiagnostics.Current?.RecordProviderFailure(
                configuration.Id,
                exception);
            return null;
        }

        var unitRoutes = GetKnownUnitRoutesSafely(
            provider,
            baseUri,
            searchItem,
            unitRequest);
        return new DiscoveredApiVideoUnit(
            configuration,
            baseUri,
            apiItem,
            searchItem,
            unitRequest,
            titleSimilarity,
            unitRoutes,
            UsedKnownBinding: !requireTitleSimilarity);
    }
    private async Task<ApiItemProviderResolution>
        ResolveApiItemSafelyAsync(
            ContentSourceProviderConfiguration configuration,
            IReadOnlyList<string> localNames,
            CatalogItemDetails localDetails,
            CancellationToken cancellationToken)
    {
        try
        {
            var provider = GetApiProvider(configuration.Id);
            var baseUri = new Uri(configuration.BaseUrl, UriKind.Absolute);
            var candidates = new Dictionary<string, ApiContentSearchItem>(
                StringComparer.Ordinal);

            var bound = await TryGetBoundSearchItemAsync(
                localDetails,
                configuration,
                cancellationToken);
            if (bound is not null)
            {
                try
                {
                    var boundItem = await provider.LoadItemAsync(
                        baseUri,
                        bound,
                        cancellationToken);
                    if (ShouldHideAdultContent(boundItem.CatalogItem))
                    {
                        await ForgetTitleBindingAsync(
                            localDetails,
                            configuration,
                            cancellationToken);
                        throw new InvalidOperationException(
                            "The remembered title is hidden by the adult-content filter.");
                    }
                    var boundDocument = new CatalogTransferDocument
                    {
                        SourceLabel = configuration.DisplayName,
                        Items = [boundItem.CatalogItem]
                    };
                    CatalogTransferValidator.Validate(boundDocument);
                    var boundDetails = CatalogDetailsMapper.ToDetails(
                        boundItem.CatalogItem,
                        CreateStableGuid(
                            $"{configuration.Id}:{bound.ExternalId}:bound-refresh-validation"));
                    if (!MatchesLocalFormat(boundItem.CatalogItem, localDetails))
                    {
                        System.Diagnostics.Trace.TraceWarning(
                            "Content provider binding rejected: " +
                            $"provider={configuration.Id}; externalId={bound.ExternalId}; " +
                            $"localSubtype={localDetails.Item.SubtypeCode}; providerSubtype={boundDetails.Item.SubtypeCode}");
                    }
                    if (MatchesLocalFormat(boundItem.CatalogItem, localDetails))
                    {
                        ValidateImageSets(
                            boundDetails,
                            boundItem.ImageSets);
                        ValidateVideoSets(
                            boundDetails,
                            boundItem.VideoSets);
                        var overlap = CountMatchingUnits(
                            localDetails,
                            boundDetails);
                        if (localDetails.Item.MediaType is not
                                MediaType.Movie ||
                            IsStandaloneMovie(localDetails) ||
                            !HasRecordedUnits(localDetails) ||
                            overlap > 0)
                        {
                            return new ApiItemProviderResolution(
                                new MatchedApiItem(
                                    configuration,
                                    baseUri,
                                    provider,
                                    boundItem,
                                    TitleSimilarity: 1,
                                    overlap,
                                    bound.ExternalId),
                                Failed: false);
                        }
                    }
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // A stale binding is removed below before normal search.
                }

                await ForgetTitleBindingAsync(
                    localDetails,
                    configuration,
                    cancellationToken);
            }

            foreach (var query in localNames.Take(4))
            {
                var results = await provider.SearchAsync(
                    baseUri,
                    query,
                    localDetails.Item.MediaType,
                    cancellationToken);
                foreach (var candidate in results.Take(500))
                {
                    if (ShouldHideAdultContent(candidate.PreviewItem) ||
                        !MatchesLocalFormat(candidate.PreviewItem, localDetails))
                    {
                        continue;
                    }
                    candidates.TryAdd(candidate.ExternalId, candidate);
                }

                if (candidates.Values.Any(candidate =>
                        GetBestTitleSimilarity(
                            localNames,
                            GetProviderTitleNames(candidate.PreviewItem)) >=
                        0.999_999))
                {
                    break;
                }
            }

            var rankedCandidates = candidates.Values
                .Select(candidate =>
                {
                    var providerTitleNames =
                        GetProviderTitleNames(candidate.PreviewItem);
                    return new RankedApiCandidate(
                        candidate,
                        GetBestTitleSimilarity(
                            localNames,
                            providerTitleNames),
                        GetBestTitleLengthGap(
                            localNames,
                            providerTitleNames));
                })
                .Where(static candidate =>
                    candidate.Similarity >=
                    LevenshteinTitleMatcher.DefaultMinimumSimilarity)
                .OrderByDescending(static candidate => candidate.Similarity)
                // Containment ties at 1, so without this the provider id
                // decided which title won.
                .ThenBy(static candidate => candidate.TitleLengthGap)
                .ThenBy(
                    static candidate => candidate.SearchItem.ExternalId,
                    StringComparer.Ordinal)
                .Take(5)
                .ToArray();

            MatchedApiItem? best = null;
            foreach (var candidate in rankedCandidates)
            {
                try
                {
                    var apiItem = await provider.LoadItemAsync(
                        baseUri,
                        candidate.SearchItem,
                        cancellationToken);
                    if (ShouldHideAdultContent(apiItem.CatalogItem))
                    {
                        continue;
                    }
                    var validationDocument = new CatalogTransferDocument
                    {
                        SourceLabel = configuration.DisplayName,
                        Items = [apiItem.CatalogItem]
                    };
                    CatalogTransferValidator.Validate(validationDocument);
                    var providerDetails = CatalogDetailsMapper.ToDetails(
                        apiItem.CatalogItem,
                        CreateStableGuid(
                            $"{configuration.Id}:{candidate.SearchItem.ExternalId}:refresh-validation"));
                    if (!MatchesLocalFormat(apiItem.CatalogItem, localDetails))
                    {
                        continue;
                    }

                    ValidateImageSets(providerDetails, apiItem.ImageSets);
                    ValidateVideoSets(providerDetails, apiItem.VideoSets);
                    var titleSimilarity = GetBestTitleSimilarity(
                        localNames,
                        GetProviderTitleNames(apiItem.CatalogItem));
                    if (titleSimilarity <
                        LevenshteinTitleMatcher.DefaultMinimumSimilarity)
                    {
                        continue;
                    }

                    var overlap = CountMatchingUnits(
                        localDetails,
                        providerDetails);
                    if (localDetails.Item.MediaType is MediaType.Movie &&
                        !IsStandaloneMovie(localDetails) &&
                        HasRecordedUnits(localDetails) &&
                        overlap == 0)
                    {
                        continue;
                    }

                    var resolution = new MatchedApiItem(
                        configuration,
                        baseUri,
                        provider,
                        apiItem,
                        titleSimilarity,
                        overlap,
                        candidate.SearchItem.ExternalId);
                    if (best is null ||
                        resolution.TitleSimilarity >
                        best.TitleSimilarity ||
                        resolution.TitleSimilarity ==
                        best.TitleSimilarity &&
                        resolution.UnitOverlapCount >
                        best.UnitOverlapCount)
                    {
                        best = resolution;
                    }
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Try the next close title returned by this provider.
                }
            }

            return new ApiItemProviderResolution(
                best,
                Failed: false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ApiItemProviderResolution(
                Match: null,
                Failed: true);
        }
    }

    private async ValueTask<ApiContentUnitImageResolution>
        MergeMatchedApiItemAsync(
            CatalogItemDetails localDetails,
            CatalogContentUnitDetails selectedUnit,
            MatchedApiUnit match,
            CancellationToken cancellationToken)
    {
        var catalogSnapshot =
            await _catalogTransferService.ExportItemDocumentAsync(
                localDetails.Item.Id, cancellationToken);
        var localItem = catalogSnapshot.Items.SingleOrDefault(item =>
                MatchesLocalFormat(item, localDetails) &&
                string.Equals(
                    NameNormalizer.Normalize(item.Name),
                    localDetails.Item.NameKey,
                    StringComparison.Ordinal)) ??
            throw new InvalidOperationException(
                "The local manga was removed before the API update could be applied.");
        var mergedItem = AttachDistributor(
            ApiCatalogItemMerger.Merge(
                localItem,
                match.ApiItem.CatalogItem),
            match.Configuration);
        var documentToImport = new CatalogTransferDocument
        {
            SourceLabel = match.ProviderName,
            Items = [mergedItem]
        };
        CatalogTransferValidator.Validate(documentToImport);
        var import = await _catalogTransferService.ImportAsync(
            documentToImport,
            $"provider:{match.ProviderId}:dynamic",
            cancellationToken);
        var refreshedDetails =
            await _catalogBrowseService.GetDetailsAsync(
                localDetails.Item.Id,
                cancellationToken) ??
            throw new InvalidOperationException(
                "The matched manga was updated but could not be reloaded.");
        var unitRequest = new ApiContentUnitRequest(
            selectedUnit.Kind,
            selectedUnit.DisplayNumber,
            selectedUnit.Identity.SeasonNumber,
            selectedUnit.Name);
        var refreshedUnit = FindMatchingUnit(
                refreshedDetails,
                unitRequest) ??
            throw new InvalidOperationException(
                "The matched manga was updated but the selected chapter could not be reloaded.");

        return new ApiContentUnitImageResolution(
            match.ProviderId,
            match.ProviderName,
            match.ProviderBaseUri,
            match.ImageSet,
            match.TitleSimilarity,
            refreshedDetails,
            refreshedUnit,
            GetTrustedArtworkOrigins(
                match.Provider,
                match.ProviderBaseUri),
            import.CreatedCount,
            import.UpdatedCount,
            GetArtworkReferrer(
                match.Provider,
                match.ProviderBaseUri,
                refreshedDetails.Item.ThumbnailRef));
    }

    private async ValueTask<ApiContentUnitVideoResolution>
        MergeMatchedApiVideoItemAsync(
            CatalogItemDetails localDetails,
            CatalogContentUnitDetails selectedUnit,
            MatchedApiVideoUnit match,
            CancellationToken cancellationToken)
    {
        var catalogSnapshot =
            await _catalogTransferService.ExportItemDocumentAsync(
                localDetails.Item.Id, cancellationToken);
        var localItem = catalogSnapshot.Items.SingleOrDefault(item =>
                MatchesLocalFormat(item, localDetails) &&
                string.Equals(
                    NameNormalizer.Normalize(item.Name),
                    localDetails.Item.NameKey,
                    StringComparison.Ordinal)) ??
            throw new InvalidOperationException(
                "The local anime was removed before the API update could be applied.");
        var providerItem = AlignStandaloneMovieUnits(
            localItem,
            match.ApiItem.CatalogItem);
        var mergedItem = AttachDistributor(
            ApiCatalogItemMerger.Merge(
                localItem,
                providerItem),
            match.Configuration);
        var documentToImport = new CatalogTransferDocument
        {
            SourceLabel = match.ProviderName,
            Items = [mergedItem]
        };
        CatalogTransferValidator.Validate(documentToImport);
        var import = await _catalogTransferService.ImportAsync(
            documentToImport,
            $"provider:{match.ProviderId}:dynamic-video",
            cancellationToken);
        var refreshedDetails =
            await _catalogBrowseService.GetDetailsAsync(
                localDetails.Item.Id,
                cancellationToken) ??
            throw new InvalidOperationException(
                "The matched anime was updated but could not be reloaded.");
        var unitRequest = new ApiContentUnitRequest(
            selectedUnit.Kind,
            selectedUnit.DisplayNumber,
            selectedUnit.Identity.SeasonNumber,
            selectedUnit.Name);
        var refreshedUnit = FindMatchingUnit(
                refreshedDetails,
                unitRequest) ??
            throw new InvalidOperationException(
                "The matched anime was updated but the selected episode could not be reloaded.");

        return new ApiContentUnitVideoResolution(
            match.ProviderId,
            match.ProviderName,
            match.ProviderBaseUri,
            match.VideoSet,
            match.TitleSimilarity,
            refreshedDetails,
            refreshedUnit,
            GetTrustedArtworkOrigins(
                match.Provider,
                match.ProviderBaseUri),
            import.CreatedCount,
            import.UpdatedCount,
            GetArtworkReferrer(
                match.Provider,
                match.ProviderBaseUri,
                refreshedDetails.Item.ThumbnailRef));
    }

    private async ValueTask<IReadOnlyList<ContentSourceCatalogSearchResult>>
        SearchTorrentCatalogAsync(
            ContentSourceProviderConfiguration configuration,
            ContentSourceSearchCriteria criteria,
            CancellationToken cancellationToken)
    {
        var mediaType = criteria.MediaType;
        var document = await CatalogDocumentHttpReader.ReadAsync(
            _httpClient,
            new Uri(configuration.BaseUrl, UriKind.Absolute),
            cancellationToken);
        return document.Items
            .Where(item => mediaType is null ||
                CatalogSubtypeResolver.ParseMediaType(item.MediaType) == mediaType)
            .Select(item => new
            {
                Item = item,
                ExternalId = UmdJsonApiContentProvider.BuildExternalId(item)
            })
            .Select(value => new
            {
                value.Item,
                value.ExternalId,
                Card = CreateProviderCard(
                    configuration,
                    value.ExternalId,
                    value.Item)
            })
            .Where(value =>
                !ShouldHideAdultContent(value.Card) &&
                criteria.Matches(value.Card))
            .Select(value => ContentSourceCatalogSearchResult.Online(
                value.Card,
                configuration,
                value.ExternalId,
                value.Item))
            .ToArray();
    }

    private async ValueTask<IReadOnlyList<ContentSourceCatalogSearchResult>>
        SearchApiAsync(
            ContentSourceProviderConfiguration configuration,
            ContentSourceSearchCriteria criteria,
            CancellationToken cancellationToken)
    {
        var provider = GetApiProvider(configuration.Id);
        var baseUri = new Uri(configuration.BaseUrl, UriKind.Absolute);
        var page = await provider.SearchAsync(
            baseUri,
            criteria.ToApiRequest(),
            cancellationToken);
        return page.Items
            .Select(result => new
            {
                Result = result,
                Card = CreateProviderCard(
                    configuration,
                    result.ExternalId,
                    result.PreviewItem)
            })
            .Where(value =>
                !ShouldHideAdultContent(value.Card) &&
                (criteria.MediaType is null ||
                 value.Card.MediaType == criteria.MediaType) &&
                criteria.MatchesQueryText(value.Card))
            .Select(value => ContentSourceCatalogSearchResult.Online(
                value.Card,
                configuration,
                value.Result.ExternalId,
                value.Result.PreviewItem,
                value.Result))
            .ToArray();
    }

    private async ValueTask<CatalogImportResult> ImportDailyUpdatesAsync(
        ContentSourceProviderConfiguration configuration,
        IApiContentProvider provider,
        Uri baseUri,
        IReadOnlyList<ApiContentSearchItem> updates,
        CancellationToken cancellationToken)
    {
        if (updates.Count == 0)
        {
            return new CatalogImportResult(0, 0, 0, []);
        }

        var catalog = await _catalogTransferService.ExportMatchingDocumentAsync(
            updates.Select(static update => update.PreviewItem).ToArray(),
            cancellationToken);
        var availableItems = catalog.Items.ToDictionary(
            BuildCatalogIdentity,
            StringComparer.Ordinal);
        var itemsToImport = new Dictionary<string, CatalogTransferItem>(
            StringComparer.Ordinal);
        foreach (var update in updates.DistinctBy(
                     static item => item.ExternalId,
                     StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldHideAdultContent(update.PreviewItem))
            {
                continue;
            }
            var enrichedUpdate = await EnrichMissingDailyThumbnailAsync(
                provider,
                baseUri,
                update,
                cancellationToken);
            var providerItem =
                CatalogTransferMapper.ToCanonicalTransferItem(
                    enrichedUpdate.PreviewItem);
            providerItem = AttachDistributor(
                providerItem,
                configuration);
            var localItem = CatalogTransferIdentity.FindBestMatch(
                availableItems.Values,
                providerItem);
            var key = localItem is null
                ? BuildCatalogIdentity(providerItem)
                : BuildCatalogIdentity(localItem);
            if (localItem is not null)
            {
                providerItem = AlignStandaloneMovieUnits(
                    localItem,
                    providerItem);
                providerItem = RebaseDailyUnitOrder(
                    localItem,
                    providerItem);
                providerItem = ApiCatalogItemMerger.Merge(
                    localItem,
                    providerItem);
            }

            availableItems[key] = providerItem;
            itemsToImport[key] = providerItem;
        }

        if (itemsToImport.Count == 0)
        {
            return new CatalogImportResult(0, 0, 0, []);
        }

        var document = new CatalogTransferDocument
        {
            SourceLabel = configuration.DisplayName,
            Items = itemsToImport.Values.ToArray()
        };
        CatalogTransferValidator.Validate(document);
        return await _catalogTransferService.ImportAsync(
            document,
            $"provider:{configuration.Id}:daily",
            cancellationToken);
    }

    private static async ValueTask<ApiContentSearchItem>
        EnrichMissingDailyThumbnailAsync(
            IApiContentProvider provider,
            Uri baseUri,
            ApiContentSearchItem update,
            CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(update.PreviewItem.ThumbnailRef))
        {
            return update;
        }

        try
        {
            var mediaType = CatalogSubtypeResolver.ParseMediaType(
                update.PreviewItem.MediaType);
            var candidates = await provider.SearchAsync(
                baseUri,
                update.PreviewItem.Name,
                mediaType,
                cancellationToken);
            var matching = candidates.FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.ExternalId,
                        update.ExternalId,
                        StringComparison.Ordinal)) ??
                candidates.FirstOrDefault(candidate =>
                    string.Equals(
                        NameNormalizer.Normalize(
                            candidate.PreviewItem.Name),
                        NameNormalizer.Normalize(
                            update.PreviewItem.Name),
                        StringComparison.Ordinal));
            var thumbnail = matching?.PreviewItem.ThumbnailRef;
            if (string.IsNullOrWhiteSpace(thumbnail))
            {
                return update;
            }

            return new ApiContentSearchItem(
                update.ExternalId,
                update.PreviewItem with
                {
                    ThumbnailRef = thumbnail
                });
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A missing preview image must not prevent chapter or episode
            // updates from reaching the local catalog.
            return update;
        }
    }

    private static bool HasMissingAutomaticInformation(
        CatalogTransferItem item) =>
        string.IsNullOrWhiteSpace(item.Description) ||
        string.IsNullOrWhiteSpace(item.ThumbnailRef) ||
        string.IsNullOrWhiteSpace(item.ReleaseDate) ||
        item.Genres.Count == 0 ||
        item.Categories.Count == 0 ||
        item.Units.Count == 0 && item.Seasons.Count == 0;

    /// <summary>
    /// Whether the title has any unit recorded at all, in its own list or
    /// under a season.
    /// </summary>
    /// <remarks>
    /// Several rules check a provider's answer against what is already here —
    /// which episode numbers overlap, above all. None of them can say anything
    /// about a title holding nothing yet, and applying them anyway rejects
    /// every answer, so the title stays empty precisely because it is empty.
    /// Those rules ask this first and fall back to the title match, which is
    /// how a film has always been identified.
    /// </remarks>
    private static bool HasRecordedUnits(CatalogItemDetails details) =>
        details.Units.Count > 0 ||
        details.Seasons.Any(static season => season.Units.Count > 0);

    private static bool IsStandaloneMovie(CatalogItemDetails details) =>
        details.Item.MediaType is MediaType.Movie &&
        MediaSubtypeDefinition.GetRequired(
            details.Item.SubtypeCode).HierarchyMode is HierarchyMode.Single;

    private static CatalogTransferItem AlignStandaloneMovieUnits(
        CatalogTransferItem localItem,
        CatalogTransferItem providerItem)
    {
        if (!CatalogTransferIdentity.IsStandaloneMovie(localItem) ||
            !CatalogTransferIdentity.IsStandaloneMovie(providerItem) ||
            localItem.Units.Count != 1 ||
            providerItem.Units.Count != 1)
        {
            return providerItem;
        }

        var localUnit = localItem.Units[0];
        var providerUnit = providerItem.Units[0];
        return providerItem with
        {
            Units =
            [
                providerUnit with
                {
                    Kind = localUnit.Kind,
                    DisplayNumber = localUnit.DisplayNumber,
                    SortOrder = localUnit.SortOrder
                }
            ]
        };
    }
    private static CatalogTransferItem RebaseDailyUnitOrder(
        CatalogTransferItem localItem,
        CatalogTransferItem providerItem)
    {
        var nextSortOrder = localItem.Units
            .Select(static unit => unit.SortOrder)
            .DefaultIfEmpty(-1)
            .Max();
        var units = new List<CatalogTransferUnit>(
            providerItem.Units.Count);
        foreach (var providerUnit in providerItem.Units
                     .OrderBy(static unit => unit.SortOrder))
        {
            var localUnit = localItem.Units.FirstOrDefault(candidate =>
                CatalogTransferMapper.ParseUnitKind(candidate.Kind) ==
                CatalogTransferMapper.ParseUnitKind(providerUnit.Kind) &&
                ApiContentUnitRequest.DisplayNumbersMatch(
                    candidate.DisplayNumber,
                    providerUnit.DisplayNumber));
            units.Add(providerUnit with
            {
                SortOrder = localUnit?.SortOrder ?? ++nextSortOrder
            });
        }

        return providerItem with { Units = units };
    }

    private static bool MatchesLocalFormat(CatalogTransferItem item, CatalogItemDetails localDetails)
    {
        var media = CatalogSubtypeResolver.ParseMediaType(item.MediaType);
        return media == localDetails.Item.MediaType &&
            (media != MediaType.Book || string.Equals(
                CatalogSubtypeResolver.ResolveCode(media, item.Subtype),
                localDetails.Item.SubtypeCode, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The key a title occupies while a refresh decides what to import.
    /// <para>
    /// Media type and name alone are not an identity: a novel and its manga
    /// adaptation share both, and so do a light novel and its comic. Keying
    /// only on those collapsed them onto one entry — and because the local
    /// catalog can legitimately hold both, building the dictionary of existing
    /// items threw on the duplicate key. The subtype is part of what makes
    /// them different works, so it is part of the key, split exactly the way
    /// <see cref="CatalogTransferIdentity.AreCompatible"/> splits them: movies
    /// by subtype group, so a movie and its anime-movie form still converge,
    /// and everything else by exact subtype.
    /// </para>
    /// </summary>
    private static string BuildCatalogIdentity(
        CatalogTransferItem item)
    {
        var mediaType = CatalogSubtypeResolver.ParseMediaType(item.MediaType);
        var subtypeCode = CatalogSubtypeResolver.ResolveCode(
            mediaType,
            item.Subtype);
        var subtypeKey = mediaType is MediaType.Movie
            ? MediaSubtypeGroups.Resolve(subtypeCode).ToString()
            : subtypeCode;
        return $"{mediaType}:{subtypeKey}:" +
            NameNormalizer.Normalize(item.Name);
    }

    private static string BuildDailyUpdateSourceKey(
        ContentSourceProviderConfiguration configuration)
    {
        var baseUrlHash = Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(configuration.BaseUrl)))[..16];
        return $"{configuration.Id}:{baseUrlHash}";
    }

    private static CatalogCard CreateProviderCard(
        ContentSourceProviderConfiguration configuration,
        string externalId,
        CatalogTransferItem item)
    {
        item = AttachDistributor(item, configuration);
        var mapped = CatalogTransferMapper.ToCatalogItem(
            item,
            CreateStableGuid($"{configuration.Id}:{externalId}"));
        return new CatalogCard(
            mapped.Id,
            mapped.MediaType,
            mapped.SubtypeCode,
            mapped.Name,
            mapped.Description,
            mapped.ThumbnailRef,
            mapped.Genres,
            mapped.Categories,
            mapped.AlternativeNames,
            mapped.Contributors,
            mapped.ReleaseDate,
            mapped.UpdatedAt,
            PublicationStatus: mapped.PublicationStatus)
        {
            ReleaseYear = mapped.ReleaseYear
        };
    }

    private static CatalogTransferItem AttachDistributor(
        CatalogTransferItem item,
        ContentSourceProviderConfiguration configuration)
    {
        if (configuration.Id == UmdJsonApiContentProvider.ProviderId) return item;
        var distributorName = configuration.DisplayName.Trim();
        var distributorNameKey =
            NameNormalizer.Normalize(distributorName);
        var contributors = item.Contributors.ToList();
        var existingIndex = contributors.FindIndex(contributor =>
            string.Equals(
                contributor.Kind,
                "organization",
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                NameNormalizer.Normalize(contributor.Name),
                distributorNameKey,
                StringComparison.Ordinal));
        var credit = new CatalogTransferCredit
        {
            Role = "Distributor"
        };
        if (existingIndex < 0)
        {
            contributors.Add(new CatalogTransferContributor
            {
                Name = distributorName,
                Kind = "organization",
                Url = configuration.BaseUrl,
                Credits = [credit]
            });
        }
        else
        {
            var existing = contributors[existingIndex];
            contributors[existingIndex] = existing with
            {
                Url = string.IsNullOrWhiteSpace(existing.Url)
                    ? configuration.BaseUrl
                    : existing.Url,
                Credits = existing.Credits.Any(static existingCredit =>
                    string.Equals(
                        existingCredit.Role,
                        "Distributor",
                        StringComparison.OrdinalIgnoreCase))
                    ? existing.Credits
                    : existing.Credits.Append(credit).ToArray()
            };
        }

        return item with
        {
            Contributors = contributors
        };
    }

    private static bool MatchesSubtypeGroup(
        CatalogCard card,
        MediaSubtypeGroup? subtypeGroup) =>
        MediaSubtypeGroups.Matches(
            subtypeGroup,
            card.MediaType,
            card.SubtypeCode);

    /// <summary>
    /// Returns whether a configured provider is enabled and can answer a
    /// search from the requested catalog section. An unscoped Home search
    /// intentionally accepts every enabled catalog/API provider.
    /// </summary>
    public bool IsProviderAvailableForSearch(
        ContentSourceProviderConfiguration configuration,
        MediaType? mediaType,
        MediaSubtypeGroup? subtypeGroup)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.IsEnabled ||
            configuration.Kind is not (
                ContentSourceProviderKind.TorrentCatalog or
                ContentSourceProviderKind.Api))
        {
            return false;
        }

        if (configuration.Kind is ContentSourceProviderKind.TorrentCatalog)
        {
            return true;
        }

        if (!_apiProviders.TryGetValue(configuration.Id, out var registration))
        {
            return false;
        }

        var requestedScope = mediaType switch
        {
            MediaType.Book => ApiContentProviderScope.Manga,
            MediaType.Movie when subtypeGroup is MediaSubtypeGroup.Anime =>
                ApiContentProviderScope.Anime,
            MediaType.Movie when subtypeGroup is MediaSubtypeGroup.LiveAction =>
                ApiContentProviderScope.LiveAction,
            MediaType.Movie =>
                ApiContentProviderScope.Anime |
                ApiContentProviderScope.LiveAction,
            _ => ApiContentProviderScope.All
        };
        return (registration.Descriptor.Scope & requestedScope) != 0;
    }

    public bool ProvidesUnitContent(string providerId) =>
        _apiProviders.TryGetValue(providerId, out var registration) && registration.Descriptor.ProvidesUnitContent;

    /// <summary>
    /// The provider title already known to answer for this item, if any. It is
    /// tried before searching, so a title confirmed once keeps being used.
    /// </summary>
    private async ValueTask<IReadOnlyDictionary<string, ApiTitleBinding>>
        LoadTitleBindingsSafelyAsync(
            Guid catalogItemId,
            CancellationToken cancellationToken)
    {
        if (_titleBindingStore is null)
        {
            return new Dictionary<string, ApiTitleBinding>(
                StringComparer.Ordinal);
        }

        try
        {
            return await _titleBindingStore.LoadAsync(
                catalogItemId,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Bindings only select a fast path. A store failure falls back to
            // the normal all-provider search.
            return new Dictionary<string, ApiTitleBinding>(
                StringComparer.Ordinal);
        }
    }

    private async ValueTask<ApiContentSearchItem?> TryGetBoundSearchItemAsync(
        CatalogItemDetails localDetails,
        ContentSourceProviderConfiguration configuration,
        CancellationToken cancellationToken,
        CatalogContentUnitDetails? localUnit = null)
    {
        if (_titleBindingStore is null)
        {
            return null;
        }

        try
        {
            var bindings = await _titleBindingStore.LoadAsync(
                localDetails.Item.Id,
                cancellationToken);
            if (!bindings.TryGetValue(configuration.Id, out var binding))
            {
                return null;
            }

            return CreateBoundSearchItem(
                localDetails,
                binding,
                localUnit);
        }
        catch
        {
            // A binding is an optimisation; failing to read it just means the
            // normal search runs.
            return null;
        }
    }

    private static ApiContentSearchItem CreateBoundSearchItem(
        CatalogItemDetails localDetails,
        ApiTitleBinding binding,
        CatalogContentUnitDetails? localUnit)
    {
        var applicableRoutes = localUnit is null
            ? []
            : binding.UnitRoutes
                .Where(route => MatchesUnitRoute(route, localUnit))
                .ToArray();
        return new ApiContentSearchItem(
            binding.ExternalId,
            new CatalogTransferItem
            {
                MediaType = localDetails.Item.MediaType
                    .ToString()
                    .ToLowerInvariant(),
                // The subtype code is "movie.anime_series"; the transfer
                // document wants the part after the dot.
                Subtype = localDetails.Item.SubtypeCode.Split('.')[^1],
                Name = binding.Title
            },
            applicableRoutes,
            RequireProviderTitle: true);
    }

    private async ValueTask RememberTitleBindingAsync(
        CatalogItemDetails localDetails,
        ContentSourceProviderConfiguration configuration,
        ApiContentSearchItem searchItem,
        string title,
        IReadOnlyList<ApiProviderUnitRoute> unitRoutes,
        CancellationToken cancellationToken)
    {
        if (_titleBindingStore is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<ApiProviderUnitRoute> mergedRoutes = unitRoutes;
            var existing = await _titleBindingStore.LoadAsync(
                localDetails.Item.Id,
                cancellationToken);
            if (existing.TryGetValue(configuration.Id, out var previous) &&
                string.Equals(
                    previous.ExternalId,
                    searchItem.ExternalId,
                    StringComparison.Ordinal))
            {
                mergedRoutes = MergeUnitRoutes(
                    previous.UnitRoutes,
                    unitRoutes);
            }

            await _titleBindingStore.SaveAsync(
                new ApiTitleBinding(
                    localDetails.Item.Id,
                    configuration.Id,
                    searchItem.ExternalId,
                    title,
                    mergedRoutes,
                    _timeProvider.GetUtcNow()),
                cancellationToken);
        }
        catch
        {
            // Remembering is best effort; the lookup already succeeded.
        }
    }

    private async ValueTask ForgetTitleBindingAsync(
        CatalogItemDetails localDetails,
        ContentSourceProviderConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (_titleBindingStore is null)
        {
            return;
        }

        try
        {
            await _titleBindingStore.RemoveAsync(
                localDetails.Item.Id,
                configuration.Id,
                cancellationToken);
        }
        catch
        {
            // The stale binding is simply retried and dropped next time.
        }
    }

    private static IReadOnlyList<ApiProviderUnitRoute>
        GetKnownUnitRoutesSafely(
            IApiContentProvider provider,
            Uri baseUri,
            ApiContentSearchItem searchItem,
            ApiContentUnitRequest unit)
    {
        try
        {
            return provider.GetKnownUnitRoutes(baseUri, searchItem, unit)
                .Where(route =>
                    route.UnitKind == unit.Kind &&
                    route.SeasonNumber == unit.SeasonNumber &&
                    (route.DisplayNumber is null ||
                     ApiContentUnitRequest.DisplayNumbersMatch(
                         route.DisplayNumber,
                         unit.DisplayNumber)))
                .Take(16)
                .ToArray();
        }
        catch
        {
            // A route is an optimisation. The provider result itself remains
            // valid even when its optional route cannot be captured.
            return [];
        }
    }

    private static bool MatchesUnitRoute(
        ApiProviderUnitRoute route,
        CatalogContentUnitDetails unit) =>
        route.UnitKind == unit.Kind &&
        route.SeasonNumber == unit.Identity.SeasonNumber &&
        (route.DisplayNumber is null ||
         ApiContentUnitRequest.DisplayNumbersMatch(
             route.DisplayNumber,
             unit.DisplayNumber));

    private static IReadOnlyList<ApiProviderUnitRoute> MergeUnitRoutes(
        IReadOnlyList<ApiProviderUnitRoute> existing,
        IReadOnlyList<ApiProviderUnitRoute> learned)
    {
        var routes = new Dictionary<string, ApiProviderUnitRoute>(
            StringComparer.Ordinal);
        // Newly confirmed routes stay at the front so a long-running manga
        // does not discard the chapter the user just opened when the cap is
        // reached. Existing routes fill the remaining slots.
        foreach (var route in learned.Concat(existing))
        {
            var key =
                $"{(int)route.UnitKind}:{route.SeasonNumber?.ToString(CultureInfo.InvariantCulture) ?? "-"}:" +
                (route.DisplayNumber?.Trim().ToUpperInvariant() ?? "-");
            routes.TryAdd(key, route);
        }

        return routes.Values.Take(256).ToArray();
    }

    private static IReadOnlyList<string> GetLocalTitleNames(
        CatalogItemDetails details) =>
        new[] { details.Item.Name, details.OriginalName }
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name!)
            .Concat(details.Item.AlternativeNames.Select(
                static name => name.Name))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> GetProviderTitleNames(
        CatalogTransferItem item) =>
        new[] { item.Name, item.OriginalName }
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name!)
            .Concat(CatalogTransferMapper.GetAlternativeNames(item)
                .Select(static name => name.Name))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    private static double GetBestTitleSimilarity(
        IReadOnlyList<string> localNames,
        IReadOnlyList<string> providerNames) =>
        GetBestTitleSimilarityFromNormalized(
            NormalizeTitleNames(localNames),
            providerNames);

    private static IReadOnlyList<string> NormalizeTitleNames(
        IReadOnlyList<string> names) =>
        names
            .Select(LevenshteinTitleMatcher.Normalize)
            .Where(static name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static double GetBestTitleSimilarityFromNormalized(
        IReadOnlyList<string> normalizedLocalNames,
        IReadOnlyList<string> providerNames)
    {
        var best = 0d;
        foreach (var providerName in providerNames)
        {
            var normalizedProviderName =
                LevenshteinTitleMatcher.Normalize(providerName);
            foreach (var normalizedLocalName in normalizedLocalNames)
            {
                var similarity =
                    LevenshteinTitleMatcher.SimilarityNormalized(
                        normalizedLocalName,
                        normalizedProviderName);
                if (similarity >= 1)
                {
                    return 1;
                }

                best = Math.Max(best, similarity);
            }
        }

        return best;
    }

    /// <summary>
    /// How many titles one provider is allowed to try for a single unit. The
    /// leaders tie on containment so the runner-up is worth a request, but the
    /// tail is unrelated noise and every attempt costs a page fetch.
    /// </summary>
    private const int MaximumRankedApiCandidates = 3;

    /// <summary>
    /// Orders the candidates that cleared the similarity floor: closest title
    /// first, then provider id purely so the order is stable.
    /// </summary>
    private static IReadOnlyList<RankedApiCandidate> RankApiCandidates(
        IEnumerable<RankedApiCandidate> candidates) =>
        candidates
            .Where(static candidate =>
                candidate.Similarity >=
                LevenshteinTitleMatcher.DefaultMinimumSimilarity)
            .OrderByDescending(static candidate => candidate.Similarity)
            // Containment ties at 1, so without this the provider id decided
            // which title won and a spin-off or a same-named film could beat
            // the requested series.
            .ThenBy(static candidate => candidate.TitleLengthGap)
            .ThenBy(
                static candidate => candidate.SearchItem.ExternalId,
                StringComparer.Ordinal)
            .Take(MaximumRankedApiCandidates)
            .ToArray();

    private static int GetBestTitleLengthGap(
        IReadOnlyList<string> localNames,
        IReadOnlyList<string> providerNames) =>
        GetBestTitleLengthGapFromNormalized(
            NormalizeTitleNames(localNames),
            providerNames);

    /// <summary>
    /// The smallest normalized length gap across every local/provider title
    /// pair. Used to order candidates that tie on similarity.
    /// </summary>
    private static int GetBestTitleLengthGapFromNormalized(
        IReadOnlyList<string> normalizedLocalNames,
        IReadOnlyList<string> providerNames)
    {
        var best = int.MaxValue;
        foreach (var providerName in providerNames)
        {
            var normalizedProviderName =
                LevenshteinTitleMatcher.Normalize(providerName);
            foreach (var normalizedLocalName in normalizedLocalNames)
            {
                var gap = LevenshteinTitleMatcher.NormalizedLengthGap(
                    normalizedLocalName.AsSpan(),
                    normalizedProviderName.AsSpan());
                if (gap == 0)
                {
                    return 0;
                }

                best = Math.Min(best, gap);
            }
        }

        return best;
    }

    private static bool IsRememberedTitlePlausible(
        ApiContentSearchItem searchItem,
        IReadOnlyList<string> providerNames) =>
        !searchItem.RequireProviderTitle ||
        GetBestTitleSimilarity(
            [searchItem.PreviewItem.Name],
            providerNames) >=
        LevenshteinTitleMatcher.DefaultMinimumSimilarity;

    private static CatalogContentUnitDetails? FindMatchingUnit(
        CatalogItemDetails details,
        ApiContentUnitRequest request) =>
        details.Units
            .Concat(details.Seasons.SelectMany(static season => season.Units))
            .FirstOrDefault(unit =>
                unit.Kind == request.Kind &&
                unit.Identity.SeasonNumber == request.SeasonNumber &&
                ApiContentUnitRequest.DisplayNumbersMatch(
                    unit.DisplayNumber,
                    request.DisplayNumber));

    private static int CountMatchingUnits(
        CatalogItemDetails left,
        CatalogItemDetails right)
    {
        var rightUnits = right.Units
            .Concat(right.Seasons.SelectMany(static season => season.Units))
            .ToArray();
        return left.Units
            .Concat(left.Seasons.SelectMany(static season => season.Units))
            .Count(leftUnit => rightUnits.Any(rightUnit =>
                leftUnit.Kind == rightUnit.Kind &&
                leftUnit.Identity.SeasonNumber ==
                rightUnit.Identity.SeasonNumber &&
                ApiContentUnitRequest.DisplayNumbersMatch(
                    leftUnit.DisplayNumber,
                    rightUnit.DisplayNumber)));
    }

    private static IReadOnlyList<Uri> GetTrustedArtworkOrigins(
        IApiContentProvider provider,
        Uri baseUri) =>
        NormalizeArtworkOrigins(
            GetArtworkPolicies(provider, baseUri)
                .Select(static policy => policy.Origin));

    private static IReadOnlyList<ApiContentArtworkPolicy> GetArtworkPolicies(
        IApiContentProvider provider,
        Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(baseUri);
        var origins = NormalizeArtworkOrigins(
            provider
                .GetTrustedArtworkOrigins(baseUri)
                .Append(baseUri));
        return NormalizeArtworkPolicies(origins.Select(origin =>
            new ApiContentArtworkPolicy(
                origin,
                provider.GetArtworkReferrer(baseUri, origin))));
    }

    private static Uri? GetArtworkReferrer(
        IApiContentProvider provider,
        Uri baseUri,
        string? artworkReference) =>
        ResolveArtworkReferrer(
            artworkReference,
            GetArtworkPolicies(provider, baseUri));

    private static IReadOnlyList<ApiContentArtworkPolicy>
        NormalizeArtworkPolicies(
            IEnumerable<ApiContentArtworkPolicy> policies) =>
        policies
            .Where(static policy => policy is not null)
            .DistinctBy(static policy =>
                $"{policy.Origin.AbsoluteUri}\n" +
                $"{policy.Referrer?.AbsoluteUri ?? string.Empty}",
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<Uri> NormalizeArtworkOrigins(
        IEnumerable<Uri> origins) =>
        origins
            .Where(static uri =>
                uri is not null &&
                uri.IsAbsoluteUri &&
                uri.Scheme is "http" or "https" &&
                uri.UserInfo.Length == 0)
            .DistinctBy(static uri =>
                $"{uri.Scheme.ToLowerInvariant()}://{uri.IdnHost.ToLowerInvariant()}:{uri.Port}")
            .ToArray();

    private static Guid CreateStableGuid(string value)
    {
        var namespaceBytes = SearchResultNamespace.ToByteArray();
        var valueBytes = Encoding.UTF8.GetBytes(value);
        var input = new byte[namespaceBytes.Length + valueBytes.Length];
        namespaceBytes.CopyTo(input, 0);
        valueBytes.CopyTo(input, namespaceBytes.Length);
        var hash = SHA256.HashData(input);
        return new Guid(hash.AsSpan(0, 16));
    }

    private static void ValidateImageSets(
        CatalogItemDetails details,
        IReadOnlyList<ApiContentImageSet> imageSets)
    {
        if (imageSets.Count(static imageSet => imageSet.IsItemLevel) > 1)
        {
            throw new InvalidOperationException(
                "The API provider returned more than one title-level image set.");
        }

        var units = details.Units
            .Concat(details.Seasons.SelectMany(static season => season.Units))
            .ToArray();
        foreach (var imageSet in imageSets.Where(
                     static imageSet => !imageSet.IsItemLevel))
        {
            if (!units.Any(unit => imageSet.Matches(
                    unit.Kind,
                    unit.DisplayNumber,
                    unit.Identity.SeasonNumber)))
            {
                throw new InvalidOperationException(
                    "The API provider returned images for a chapter or episode that is not present in its catalog item.");
            }
        }

        if (imageSets
            .GroupBy(static imageSet =>
                $"{imageSet.UnitKind}:{imageSet.SeasonNumber}:{imageSet.DisplayNumber}",
                StringComparer.OrdinalIgnoreCase)
            .Any(static group => group.Count() > 1))
        {
            throw new InvalidOperationException(
                "The API provider returned duplicate image sets for one catalog target.");
        }
    }

    private static void ValidateVideoSets(
        CatalogItemDetails details,
        IReadOnlyList<ApiContentVideoSet> videoSets)
    {
        var units = details.Units
            .Concat(details.Seasons.SelectMany(static season => season.Units))
            .ToArray();
        foreach (var videoSet in videoSets)
        {
            if (!units.Any(unit => videoSet.Matches(
                    unit.Kind,
                    unit.DisplayNumber,
                    unit.Identity.SeasonNumber)))
            {
                throw new InvalidOperationException(
                    "The API provider returned video servers for an episode that is not present in its catalog item.");
            }
        }

        if (videoSets
            .GroupBy(static videoSet =>
                $"{videoSet.UnitKind}:{videoSet.SeasonNumber}:{videoSet.DisplayNumber}",
                StringComparer.OrdinalIgnoreCase)
            .Any(static group => group.Count() > 1))
        {
            throw new InvalidOperationException(
                "The API provider returned duplicate video sets for one catalog episode.");
        }
    }

    private IApiContentProvider GetApiProvider(string providerId) =>
        _apiProviders.TryGetValue(providerId, out var registration)
            ? registration.GetProvider()
            : throw new InvalidOperationException(
                "The configured API provider implementation is unavailable.");

    private bool ShouldHideAdultContent(CatalogCard card) =>
        _hideAdultContent() &&
        AdultContentCategoryFilter.Matches(card.Categories, card.Genres);

    private bool ShouldHideAdultContent(CatalogTransferItem item) =>
        _hideAdultContent() &&
        AdultContentCategoryFilter.Matches(item.Categories, item.Genres);

    private void EnsureAdultContentIsAllowed(CatalogTransferItem item)
    {
        if (ShouldHideAdultContent(item))
        {
            throw new InvalidOperationException(
                "The selected title is hidden by the adult-content filter.");
        }
    }

    private static async ValueTask<T> ExecuteApiUnitLookupAsync<T>(
        string operation,
        TimeSpan timeout,
        Func<CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();

        using var deadline =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        deadline.CancelAfter(timeout);
        var requestMetrics = new ApiProviderRequestMetrics();
        var diagnostics = new ApiUnitLookupDiagnostics(operation);
        using var requestMetricsScope =
            ApiProviderRequestMetricsContext.Enter(requestMetrics);
        using var diagnosticsScope =
            ApiUnitLookupDiagnostics.Enter(diagnostics);
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var outcome = "success";
        Exception? failure = null;
        try
        {
            return await action(deadline.Token);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            outcome = "cancelled";
            throw;
        }
        catch (OperationCanceledException exception)
            when (deadline.IsCancellationRequested)
        {
            outcome = "timeout";
            var timeoutFailure = new TimeoutException(
                $"The online {operation.Replace('-', ' ')} exceeded its " +
                $"{timeout.TotalSeconds:0}-second deadline.",
                exception);
            failure = timeoutFailure;
            throw timeoutFailure;
        }
        catch (Exception exception)
        {
            outcome = "failed";
            failure = exception;
            throw;
        }
        finally
        {
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(
                startedAt);
            if (elapsed >= SlowApiUnitLookupThreshold ||
                failure is not null ||
                diagnostics.HasProviderFailures)
            {
                System.Diagnostics.Trace.TraceWarning(
                    diagnostics.FormatLog(
                        elapsed,
                        requestMetrics.RequestCount,
                        outcome,
                        failure));
            }
        }
    }

    /// <summary>
    /// Says why a refresh returned nothing.
    /// </summary>
    /// <remarks>
    /// Every one of these exits used to be silent, which reads from outside
    /// as the page failing to load rather than as the refresh deciding there
    /// was nothing to ask for. A title that never fills in is the report this
    /// answers, so the reason has to reach the log.
    /// </remarks>
    private static void TraceRefreshSkipped(
        CatalogItemDetails localDetails,
        string reason) =>
        System.Diagnostics.Trace.TraceWarning(
            "Content refresh skipped: " +
            $"reason={reason}; " +
            $"title={localDetails.Item.Name}; " +
            $"mediaType={localDetails.Item.MediaType}; " +
            $"subtype={localDetails.Item.SubtypeCode}");

    private static void TraceProviderFailure(
        string operation,
        ContentSourceProviderConfiguration configuration,
        Exception exception)
    {
        System.Diagnostics.Trace.TraceError(
            "Content provider operation failed: " +
            $"operation={operation}; " +
            $"provider={configuration.DisplayName}; " +
            $"providerId={configuration.Id}" +
            Environment.NewLine +
            exception);
    }

    private static IEnumerable<ApiContentProviderRegistration>
        CreateRegistrations(IEnumerable<IApiContentProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        return providers.Select(
            static provider => new ApiContentProviderRegistration(provider));
    }

    private static ApiContentProviderRegistration CoordinateRegistration(
        ApiContentProviderRegistration registration,
        ApiContentProviderConcurrencyCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(coordinator);
        return new ApiContentProviderRegistration(
            registration.Descriptor,
            () => coordinator.Coordinate(registration.GetProvider()),
            registration.SupportsDailyUpdates);
    }

    private sealed record ProviderSearchResponse(
        IReadOnlyList<ContentSourceCatalogSearchResult> Results,
        ContentSourceSearchFailure? Failure);

    private sealed class ApiUnitLookupDiagnostics(string operation)
    {
        private static readonly AsyncLocal<ApiUnitLookupDiagnostics?> Context =
            new();
        private readonly ConcurrentDictionary<string, string>
            _providerFailures = new(StringComparer.Ordinal);
        private int _providerCount;
        private int _matchCount = -1;
        private string? _selectedProvider;

        internal static ApiUnitLookupDiagnostics? Current => Context.Value;

        internal bool HasProviderFailures => !_providerFailures.IsEmpty;

        internal static Scope Enter(ApiUnitLookupDiagnostics diagnostics)
        {
            ArgumentNullException.ThrowIfNull(diagnostics);
            var previous = Context.Value;
            Context.Value = diagnostics;
            return new Scope(previous);
        }

        internal void SetProviderCount(int count) =>
            Volatile.Write(ref _providerCount, Math.Max(0, count));

        internal void SetMatchCount(int count) =>
            Volatile.Write(ref _matchCount, Math.Max(0, count));

        internal void SetSelectedProvider(string providerId)
        {
            if (!string.IsNullOrWhiteSpace(providerId))
            {
                _selectedProvider = providerId.Trim();
            }
        }

        internal void RecordProviderFailure(
            string providerId,
            Exception exception)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return;
            }

            ArgumentNullException.ThrowIfNull(exception);
            _providerFailures.TryAdd(
                providerId.Trim(),
                $"{exception.GetType().Name}:" +
                NormalizeLogMessage(exception.Message));
        }

        internal string FormatLog(
            TimeSpan elapsed,
            int requestCount,
            string outcome,
            Exception? failure)
        {
            var failures = _providerFailures
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Take(8)
                .Select(static pair => $"{pair.Key}={pair.Value}")
                .ToArray();
            var failureDetail = failure is null
                ? "-"
                : $"{failure.GetType().Name}:" +
                  NormalizeLogMessage(failure.Message);
            return "API unit lookup: " +
                $"operation={operation}; " +
                $"outcome={outcome}; " +
                $"durationMs={elapsed.TotalMilliseconds:0}; " +
                $"requests={requestCount}; " +
                $"providers={Volatile.Read(ref _providerCount)}; " +
                $"matches={Volatile.Read(ref _matchCount)}; " +
                $"selectedProvider={_selectedProvider ?? "-"}; " +
                $"failure={failureDetail}; " +
                "providerFailures=" +
                (failures.Length == 0
                    ? "-"
                    : string.Join('|', failures));
        }

        private static string NormalizeLogMessage(string message)
        {
            var normalized = message
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace(';', ',')
                .Trim();
            return normalized.Length <= 160
                ? normalized
                : normalized[..160];
        }

        internal readonly struct Scope(ApiUnitLookupDiagnostics? previous)
            : IDisposable
        {
            public void Dispose() => Context.Value = previous;
        }
    }

    private sealed record PreparedDailyUpdate(
        ContentSourceProviderConfiguration Configuration,
        bool WasSkipped,
        IApiContentProvider? Provider,
        Uri? BaseUri,
        string? SourceKey,
        IReadOnlyList<ApiContentSearchItem>? Updates,
        ContentSourceDailyUpdateFailure? Failure)
    {
        public static PreparedDailyUpdate Skipped(
            ContentSourceProviderConfiguration configuration) =>
            new(
                configuration,
                WasSkipped: true,
                Provider: null,
                BaseUri: null,
                SourceKey: null,
                Updates: null,
                Failure: null);

        public static PreparedDailyUpdate Ready(
            ContentSourceProviderConfiguration configuration,
            IApiContentProvider provider,
            Uri baseUri,
            string sourceKey,
            IReadOnlyList<ApiContentSearchItem> updates) =>
            new(
                configuration,
                WasSkipped: false,
                provider,
                baseUri,
                sourceKey,
                updates,
                Failure: null);

        public static PreparedDailyUpdate Failed(
            ContentSourceProviderConfiguration configuration,
            ContentSourceDailyUpdateFailure failure) =>
            new(
                configuration,
                WasSkipped: false,
                Provider: null,
                BaseUri: null,
                SourceKey: null,
                Updates: null,
                failure);
    }

    /// <summary>
    /// <paramref name="TitleLengthGap"/> orders candidates that tie on
    /// <paramref name="Similarity"/>; see
    /// <see cref="LevenshteinTitleMatcher.NormalizedLengthGap(string, string)"/>.
    /// </summary>
    private sealed record RankedApiCandidate(
        ApiContentSearchItem SearchItem,
        double Similarity,
        int TitleLengthGap = int.MaxValue);

    private sealed record UnitImageProviderResolution(
        DiscoveredApiImageUnit? Match,
        bool Failed,
        bool UsedKnownBinding);

    private sealed record UnitVideoProviderResolution(
        DiscoveredApiVideoUnit? Match,
        bool Failed,
        bool UsedKnownBinding);

    private sealed record ApiItemProviderResolution(
        MatchedApiItem? Match,
        bool Failed);

    private sealed record MatchedApiItem(
        ContentSourceProviderConfiguration Configuration,
        Uri ProviderBaseUri,
        IApiContentProvider Provider,
        ApiContentItem ApiItem,
        double TitleSimilarity,
        int UnitOverlapCount,
        string ExternalId)
    {
        public string ProviderId => Configuration.Id;

        public string ProviderName => Configuration.DisplayName;
    }

    private sealed record DiscoveredApiImageUnit(
        ContentSourceProviderConfiguration Configuration,
        Uri ProviderBaseUri,
        ApiContentItem ApiItem,
        ApiContentSearchItem SearchItem,
        ApiContentUnitRequest UnitRequest,
        double TitleSimilarity,
        IReadOnlyList<ApiProviderUnitRoute> UnitRoutes,
        bool UsedKnownBinding)
    {
        public string ProviderName => Configuration.DisplayName;
    }

    private sealed record DiscoveredApiVideoUnit(
        ContentSourceProviderConfiguration Configuration,
        Uri ProviderBaseUri,
        ApiContentItem ApiItem,
        ApiContentSearchItem SearchItem,
        ApiContentUnitRequest UnitRequest,
        double TitleSimilarity,
        IReadOnlyList<ApiProviderUnitRoute> UnitRoutes,
        bool UsedKnownBinding)
    {
        public string ProviderName => Configuration.DisplayName;
    }

    private sealed record MatchedApiUnit(
        ContentSourceProviderConfiguration Configuration,
        Uri ProviderBaseUri,
        IApiContentProvider Provider,
        ApiContentItem ApiItem,
        ApiContentImageSet ImageSet,
        double TitleSimilarity,
        string ExternalId,
        IReadOnlyList<ApiProviderUnitRoute> UnitRoutes)
    {
        public string ProviderId => Configuration.Id;

        public string ProviderName => Configuration.DisplayName;
    }

    private sealed record MatchedApiVideoUnit(
        ContentSourceProviderConfiguration Configuration,
        Uri ProviderBaseUri,
        IApiContentProvider Provider,
        ApiContentItem ApiItem,
        ApiContentVideoSet VideoSet,
        double TitleSimilarity,
        string ExternalId,
        IReadOnlyList<ApiProviderUnitRoute> UnitRoutes)
    {
        public string ProviderId => Configuration.Id;

        public string ProviderName => Configuration.DisplayName;
    }
}
