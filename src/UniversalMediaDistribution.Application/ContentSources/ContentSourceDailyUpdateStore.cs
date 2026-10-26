namespace UniversalMediaDistribution.Application.ContentSources;

public interface IContentSourceDailyUpdateStore
{
    ValueTask<DateOnly?> LoadLastSuccessfulDateAsync(
        string sourceKey,
        CancellationToken cancellationToken = default);

    ValueTask SaveLastSuccessfulDateAsync(
        string sourceKey,
        DateOnly date,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryContentSourceDailyUpdateStore :
    IContentSourceDailyUpdateStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateOnly> _dates =
        new(StringComparer.Ordinal);

    public ValueTask<DateOnly?> LoadLastSuccessfulDateAsync(
        string sourceKey,
        CancellationToken cancellationToken = default)
    {
        ValidateSourceKey(sourceKey);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult<DateOnly?>(
                _dates.GetValueOrDefault(sourceKey));
        }
    }

    public ValueTask SaveLastSuccessfulDateAsync(
        string sourceKey,
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        ValidateSourceKey(sourceKey);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _dates[sourceKey] = date;
        }

        return ValueTask.CompletedTask;
    }

    internal static void ValidateSourceKey(string sourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        if (sourceKey.Length > 200)
        {
            throw new ArgumentException(
                "A daily-update source key cannot exceed 200 characters.",
                nameof(sourceKey));
        }
    }
}
