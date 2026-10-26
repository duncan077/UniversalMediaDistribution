namespace UniversalMediaDistribution.ContentFiltering.Subscriptions;

/// <summary>
/// The viewer's filtering preference, and what is known about the last
/// refresh. Persisted alongside the application's other settings.
/// </summary>
public sealed record ContentFilterSettings
{
    public FilteringMode Mode { get; init; } = FilteringMode.Standard;

    /// <summary>
    /// Whether catalog content marked as adult, porn, +18, or 18+ is hidden.
    /// This is separate from the embedded-player request filtering mode.
    /// </summary>
    public bool HideAdultContent { get; init; } = true;

    /// <summary>
    /// When the subscribed lists were last checked. Kept so a restart does not
    /// become a reason to download everything again.
    /// </summary>
    public DateTimeOffset? LastRefreshUtc { get; init; }

    public ContentFilterSettings Normalize() =>
        Enum.IsDefined(Mode)
            ? this
            : this with { Mode = FilteringMode.Standard };
}

public interface IContentFilterSettingsStore
{
    ValueTask<ContentFilterSettings> LoadAsync(
        CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        ContentFilterSettings settings,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The store used by hosts with no durable storage. The preference lasts for
/// the run, which is the right default when nothing can be written down.
/// </summary>
public sealed class InMemoryContentFilterSettingsStore : IContentFilterSettingsStore
{
    private ContentFilterSettings _settings = new();

    public ValueTask<ContentFilterSettings> LoadAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_settings);

    public ValueTask SaveAsync(
        ContentFilterSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings.Normalize();
        return ValueTask.CompletedTask;
    }
}
