namespace UniversalMediaDistribution.Torrenting.Ingestion;

public sealed record MagnetOpenPreferences(
    bool OpenAutomatically = false,
    bool ImportInfoJson = true,
    bool TreatExternalMagnetsAsPrivate = false);

public interface IMagnetOpenPreferencesStore
{
    ValueTask<MagnetOpenPreferences> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        MagnetOpenPreferences preferences,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryMagnetOpenPreferencesStore : IMagnetOpenPreferencesStore
{
    private readonly Lock _gate = new();
    private MagnetOpenPreferences _preferences;

    public InMemoryMagnetOpenPreferencesStore(MagnetOpenPreferences? initialPreferences = null)
    {
        _preferences = initialPreferences ?? new MagnetOpenPreferences();
    }

    public ValueTask<MagnetOpenPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_preferences);
        }
    }

    public ValueTask SaveAsync(
        MagnetOpenPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _preferences = preferences;
        }

        return ValueTask.CompletedTask;
    }
}
