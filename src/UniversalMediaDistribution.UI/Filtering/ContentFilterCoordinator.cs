using UniversalMediaDistribution.ContentFiltering;
using UniversalMediaDistribution.ContentFiltering.Subscriptions;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.UI.Filtering;

public enum ContentFilterRefreshStatus
{
    /// <summary>Lists were checked and the ruleset is current.</summary>
    Completed,

    /// <summary>
    /// Nothing was checked because the mode subscribes to no lists. Not a
    /// failure: <c>Basic</c> is the seed, and the seed needs no network.
    /// </summary>
    NotSubscribed,

    /// <summary>
    /// The moment was wrong — a video is playing, the connection is metered,
    /// or the device is saving power. The lists stay as they are.
    /// </summary>
    Deferred,

    /// <summary>This host has nowhere to cache a downloaded ruleset.</summary>
    NoStorage,

    /// <summary>
    /// Something went wrong. The previous ruleset is still in force; a refresh
    /// never leaves the application unprotected.
    /// </summary>
    Failed
}

public sealed record ContentFilterRefreshOutcome(
    ContentFilterRefreshStatus Status,
    int NetworkRuleCount = 0,
    int ListsFetched = 0,
    int ListsFailed = 0,
    string? Error = null);

/// <summary>
/// Owns the filtering engine's lifecycle for a running application: which mode
/// the viewer chose, what the device can carry, when it is acceptable to go to
/// the network, and what is currently loaded.
///
/// The scheduling rules are the whole point of this class. Downloading a dozen
/// megabytes of filter text over a television's wireless connection while a
/// torrent stream saturates it is a real regression, and so is compiling a
/// hundred thousand rules while a video decodes. Startup therefore adopts
/// whatever was compiled last time — a file read, not a parse — and any actual
/// refresh waits for a moment the host says is safe.
/// </summary>
public sealed class ContentFilterCoordinator
{
    private static readonly TimeSpan StartupRefreshDelay =
        TimeSpan.FromSeconds(45);

    private readonly ContentFilterEngine _engine;
    private readonly IContentFilterSettingsStore _settingsStore;
    private readonly FilterListSubscriptionService? _subscriptions;
    private readonly Func<bool> _canRefresh;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _seedRuleset;
    private readonly bool _isMobileHost;
    private ContentFilterSettings _settings = new();

    public ContentFilterCoordinator(
        DeviceTier tier,
        IContentFilterSettingsStore? settingsStore = null,
        string? rulesetDirectory = null,
        HttpClient? httpClient = null,
        Func<bool>? canRefresh = null,
        bool isMobileHost = false,
        ContentFilterEngine? engine = null,
        string? seedRuleset = null,
        TimeProvider? timeProvider = null)
    {
        Tier = tier;
        _isMobileHost = isMobileHost;
        _engine = engine ?? EmbeddedWebAdBlocker.Engine;
        _seedRuleset = seedRuleset ?? EmbeddedWebAdBlocker.SeedRuleset;
        _settingsStore = settingsStore ?? new InMemoryContentFilterSettingsStore();
        _canRefresh = canRefresh ?? (static () => true);
        _time = timeProvider ?? TimeProvider.System;
        if (!string.IsNullOrWhiteSpace(rulesetDirectory))
        {
            _subscriptions = new FilterListSubscriptionService(
                _engine,
                new FilterRulesetStore(rulesetDirectory),
                httpClient ?? new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(60)
                },
                BuildOptions(_settings.Mode),
                _time);
        }
    }

    public DeviceTier Tier { get; }

    public FilteringMode Mode => _settings.Mode;

    public bool HideAdultContent => _settings.HideAdultContent;

    /// <summary>
    /// What the device will actually run. A viewer may ask for less filtering
    /// than the hardware could carry, never for more.
    /// </summary>
    public FilteringMode EffectiveMode => Tier.Cap(_settings.Mode);

    public DateTimeOffset? LastRefreshUtc => _settings.LastRefreshUtc;

    /// <summary>False on hosts with nowhere to cache a compiled ruleset.</summary>
    public bool CanDownload => _subscriptions is not null;

    public int NetworkRuleCount => _engine.Ruleset.NetworkRuleCount;

    /// <summary>
    /// Adopts the stored preference and whatever ruleset was compiled last
    /// time, then leaves a background refresh to catch up later. Never touches
    /// the network on the startup path.
    /// </summary>
    public async Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _settings = (await _settingsStore.LoadAsync(cancellationToken))
                .Normalize();
            ApplyCurrentRuleset();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A settings store that cannot be read is not a reason to run
            // unprotected: the seed still compiles.
            ApplySeed();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Waits, then refreshes if the host allows it. Startup is busy enough
    /// without a download competing with the first catalog load.
    /// </summary>
    public void ScheduleStartupRefresh()
    {
        if (_subscriptions is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StartupRefreshDelay);
                await RefreshAsync();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError(
                    $"Filter list refresh failed: {exception.Message}");
            }
        });
    }

    public async Task<ContentFilterRefreshOutcome> SetModeAsync(
        FilteringMode mode,
        bool? hideAdultContent = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _settings = (_settings with
            {
                Mode = mode,
                HideAdultContent = hideAdultContent ?? _settings.HideAdultContent
            }).Normalize();
            await _settingsStore.SaveAsync(_settings, cancellationToken);
            ApplyCurrentRuleset();
        }
        finally
        {
            _gate.Release();
        }

        // A mode change is the one moment the viewer is waiting on an answer,
        // so it goes to the network immediately rather than on the next tick.
        return await RefreshAsync(force: true, cancellationToken);
    }

    public async Task SetHideAdultContentAsync(
        bool hideAdultContent,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _settings = _settings with { HideAdultContent = hideAdultContent };
            await _settingsStore.SaveAsync(_settings, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ContentFilterRefreshOutcome> RefreshAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (_subscriptions is null)
        {
            return new ContentFilterRefreshOutcome(
                ContentFilterRefreshStatus.NoStorage);
        }

        if (EffectiveMode <= FilteringMode.Basic)
        {
            return new ContentFilterRefreshOutcome(
                ContentFilterRefreshStatus.NotSubscribed,
                NetworkRuleCount);
        }

        if (!_canRefresh())
        {
            return new ContentFilterRefreshOutcome(
                ContentFilterRefreshStatus.Deferred,
                NetworkRuleCount);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _subscriptions.Configure(BuildOptions(_settings.Mode));
            var result = await _subscriptions.RefreshAsync(
                force,
                cancellationToken);
            _settings = _settings with { LastRefreshUtc = _time.GetUtcNow() };
            await _settingsStore.SaveAsync(_settings, cancellationToken);
            return new ContentFilterRefreshOutcome(
                ContentFilterRefreshStatus.Completed,
                NetworkRuleCount,
                result.ListsFetched,
                result.ListsFailed);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            return new ContentFilterRefreshOutcome(
                ContentFilterRefreshStatus.Failed,
                NetworkRuleCount,
                Error: exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ApplyCurrentRuleset()
    {
        if (_subscriptions is null)
        {
            ApplySeed();
            return;
        }

        _subscriptions.Configure(BuildOptions(_settings.Mode));
        if (!_subscriptions.TryApplyCached())
        {
            // Nothing compiled yet, or a ruleset written by an older build.
            _subscriptions.ApplySeedOnly();
        }
    }

    private void ApplySeed()
    {
        if (EffectiveMode == FilteringMode.Off)
        {
            _engine.Update(CompiledRuleset.Empty);
            return;
        }

        _engine.Update(CompiledRuleset.Load(
            ContentFiltering.Compilation.RulesetCompiler.Compile(
                ContentFiltering.Parsing.FilterListParser.Parse(_seedRuleset),
                out _,
                Tier.CompilerOptions())));
    }

    private FilterSubscriptionOptions BuildOptions(FilteringMode mode) =>
        new()
        {
            Mode = mode,
            Tier = Tier,
            SeedRuleset = _seedRuleset,
            Environment = _isMobileHost
                ? ContentFiltering.Parsing.FilterListEnvironment.Mobile
                : ContentFiltering.Parsing.FilterListEnvironment.Desktop
        };
}
