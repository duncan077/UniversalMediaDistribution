using System.Net;
using System.Text;
using UniversalMediaDistribution.ContentFiltering.Compilation;
using UniversalMediaDistribution.ContentFiltering.Parsing;

namespace UniversalMediaDistribution.ContentFiltering.Subscriptions;

public sealed record FilterSubscriptionOptions
{
    public FilteringMode Mode { get; init; } = FilteringMode.Standard;

    public DeviceTier Tier { get; init; } = DeviceTier.Standard;

    public IReadOnlyList<FilterListSubscription> Catalog { get; init; } =
        FilterListCatalog.Default;

    /// <summary>
    /// Rules compiled in with the application. They are what protects a first
    /// run, an offline install, and <see cref="FilteringMode.Basic"/>, and they
    /// are always compiled alongside whatever was downloaded.
    /// </summary>
    public string SeedRuleset { get; init; } = string.Empty;

    /// <summary>
    /// A floor under each list's own <c>! Expires:</c> hint. Lists ask to be
    /// refreshed more eagerly than a media client on a metered connection
    /// should oblige.
    /// </summary>
    public TimeSpan MinimumRefreshInterval { get; init; } = TimeSpan.FromHours(12);

    /// <summary>
    /// A response larger than this is refused. Filter lists are a few
    /// megabytes; anything far past that is a redirect to something else.
    /// </summary>
    public long MaximumListBytes { get; init; } = 16 * 1024 * 1024;

    public FilterListEnvironment Environment { get; init; } =
        FilterListEnvironment.Desktop;
}

public sealed record FilterRefreshResult
{
    public bool RulesetChanged { get; init; }

    public int ListsFetched { get; init; }

    public int ListsUnchanged { get; init; }

    public int ListsFailed { get; init; }

    public RulesetCompilationReport? Compilation { get; init; }

    public static FilterRefreshResult Unchanged { get; } = new();
}

/// <summary>
/// Keeps the engine's ruleset current from upstream filter lists.
///
/// Nothing here decides <em>when</em> to refresh. Downloading fifteen megabytes
/// of list text over a television's wireless connection while a torrent stream
/// is saturating it is a real regression, and so is compiling a hundred
/// thousand rules while a video is decoding — but whether either is happening
/// is something only the host knows. The host calls
/// <see cref="RefreshAsync"/> when the device is idle and on an unmetered
/// connection; this class makes sure that call is cheap when nothing has
/// changed and safe when everything has.
///
/// Every failure keeps the previous ruleset. The application is never left
/// unprotected by a refresh.
/// </summary>
public sealed class FilterListSubscriptionService
{
    private readonly ContentFilterEngine _engine;
    private readonly FilterRulesetStore _store;
    private readonly HttpClient _client;
    private readonly TimeProvider _time;

    public FilterListSubscriptionService(
        ContentFilterEngine engine,
        FilterRulesetStore store,
        HttpClient client,
        FilterSubscriptionOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(client);
        _engine = engine;
        _store = store;
        _client = client;
        _time = timeProvider ?? TimeProvider.System;
        Options = options ?? new FilterSubscriptionOptions();
    }

    public FilterSubscriptionOptions Options { get; private set; }

    /// <summary>
    /// What the device will actually run, after the tier has had its say.
    /// </summary>
    public FilteringMode EffectiveMode => Options.Tier.Cap(Options.Mode);

    /// <summary>
    /// The startup path: adopt whatever was compiled last time. This is a file
    /// read and a header check, not a parse, so it costs the same whether the
    /// ruleset holds a hundred rules or a hundred thousand.
    /// </summary>
    public bool TryApplyCached()
    {
        if (EffectiveMode == FilteringMode.Off)
        {
            _engine.Update(CompiledRuleset.Empty);
            return true;
        }

        var manifest = _store.LoadManifest();
        if (manifest.RulesetFormatVersion != CompiledRuleset.FormatVersion ||
            !_store.TryLoadRuleset(out var ruleset))
        {
            return false;
        }

        _engine.Update(ruleset);
        return true;
    }

    /// <summary>
    /// Compiles the seed alone. The fallback when nothing has been downloaded
    /// yet, and the whole of <see cref="FilteringMode.Basic"/>.
    /// </summary>
    public RulesetCompilationReport ApplySeedOnly()
    {
        var blob = RulesetCompiler.Compile(
            [FilterListParser.Parse(Options.SeedRuleset, Options.Environment)],
            out var report,
            Options.Tier.CompilerOptions());
        _engine.Update(CompiledRuleset.Load(blob));
        return report;
    }

    public void Configure(FilterSubscriptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
    }

    public async Task<FilterRefreshResult> RefreshAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var mode = EffectiveMode;
        if (mode == FilteringMode.Off)
        {
            _engine.Update(CompiledRuleset.Empty);
            return FilterRefreshResult.Unchanged;
        }

        var subscriptions = FilterListCatalog.For(
            mode,
            Options.Tier,
            Options.Catalog);
        var manifest = _store.LoadManifest();
        var fetched = 0;
        var unchanged = 0;
        var failed = 0;
        var changed = false;

        foreach (var subscription in subscriptions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = manifest.Lists.GetValueOrDefault(subscription.Id);
            if (!force && !IsDue(state))
            {
                unchanged++;
                continue;
            }

            var outcome = await FetchAsync(
                subscription,
                state,
                cancellationToken);
            switch (outcome.Status)
            {
                case FetchStatus.Updated:
                    _store.WriteList(subscription.Id, outcome.Content!);
                    manifest.Lists[subscription.Id] = outcome.State!;
                    fetched++;
                    changed = true;
                    break;

                case FetchStatus.NotModified:
                    manifest.Lists[subscription.Id] = (state ?? new()) with
                    {
                        FetchedUtc = _time.GetUtcNow()
                    };
                    unchanged++;
                    break;

                default:
                    // Keep whatever is cached. A list that fails to refresh is
                    // a stale list, not an absent one.
                    failed++;
                    break;
            }
        }

        var needsCompile = changed ||
            force ||
            manifest.RulesetFormatVersion != CompiledRuleset.FormatVersion ||
            !_store.TryLoadRuleset(out _);
        if (!needsCompile)
        {
            _store.SaveManifest(manifest);
            return new FilterRefreshResult
            {
                ListsFetched = fetched,
                ListsUnchanged = unchanged,
                ListsFailed = failed
            };
        }

        var report = Compile(subscriptions);
        manifest = manifest with
        {
            RulesetFormatVersion = CompiledRuleset.FormatVersion
        };
        _store.SaveManifest(manifest);
        return new FilterRefreshResult
        {
            RulesetChanged = true,
            ListsFetched = fetched,
            ListsUnchanged = unchanged,
            ListsFailed = failed,
            Compilation = report
        };
    }

    private RulesetCompilationReport Compile(
        IReadOnlyList<FilterListSubscription> subscriptions)
    {
        var parsed = new List<FilterListParseResult>(subscriptions.Count + 1)
        {
            FilterListParser.Parse(Options.SeedRuleset, Options.Environment)
        };

        foreach (var subscription in subscriptions)
        {
            var content = _store.ReadList(subscription.Id);
            if (!string.IsNullOrEmpty(content))
            {
                parsed.Add(
                    FilterListParser.Parse(content, Options.Environment));
            }
        }

        var blob = RulesetCompiler.Compile(
            parsed,
            out var report,
            Options.Tier.CompilerOptions());
        _store.SaveRuleset(blob);
        // Keep refreshed rules file-backed too, just like the startup path.
        // Otherwise the entire compiled blob stays on the managed heap until
        // the next process restart. A refused mapping still has the read fallback.
        _engine.Update(_store.TryLoadRuleset(out var cached)
            ? cached
            : CompiledRuleset.Load(blob));
        return report;
    }

    private bool IsDue(FilterListState? state)
    {
        if (state is null || state.FetchedUtc == default)
        {
            return true;
        }

        var interval = state.Expires is { } expires &&
            expires > Options.MinimumRefreshInterval
                ? expires
                : Options.MinimumRefreshInterval;
        return _time.GetUtcNow() - state.FetchedUtc >= interval;
    }

    private enum FetchStatus
    {
        Updated,
        NotModified,
        Failed
    }

    private readonly record struct FetchOutcome(
        FetchStatus Status,
        string? Content,
        FilterListState? State);

    private async Task<FetchOutcome> FetchAsync(
        FilterListSubscription subscription,
        FilterListState? state,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                subscription.Url);
            if (!string.IsNullOrEmpty(state?.ETag))
            {
                request.Headers.TryAddWithoutValidation(
                    "If-None-Match",
                    state.ETag);
            }

            if (!string.IsNullOrEmpty(state?.LastModified))
            {
                request.Headers.TryAddWithoutValidation(
                    "If-Modified-Since",
                    state.LastModified);
            }

            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new FetchOutcome(FetchStatus.NotModified, null, null);
            }

            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentLength >
                    Options.MaximumListBytes)
            {
                return new FetchOutcome(FetchStatus.Failed, null, null);
            }

            var content = await ReadBoundedAsync(response, cancellationToken);
            if (content is null)
            {
                return new FetchOutcome(FetchStatus.Failed, null, null);
            }

            var parsed = FilterListParser.Parse(content, Options.Environment);
            return new FetchOutcome(
                FetchStatus.Updated,
                content,
                new FilterListState
                {
                    ETag = response.Headers.ETag?.ToString(),
                    LastModified =
                        response.Content.Headers.LastModified?.ToString("R"),
                    FetchedUtc = _time.GetUtcNow(),
                    Expires = parsed.Expires
                });
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
                TaskCanceledException or
                IOException &&
            !cancellationToken.IsCancellationRequested)
        {
            return new FetchOutcome(FetchStatus.Failed, null, null);
        }
    }

    /// <summary>
    /// Reads at most <see cref="FilterSubscriptionOptions.MaximumListBytes"/>.
    /// A declared content length is a claim, not a guarantee, so the limit is
    /// enforced while reading rather than only before it.
    /// </summary>
    private async Task<string?> ReadBoundedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > Options.MaximumListBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
