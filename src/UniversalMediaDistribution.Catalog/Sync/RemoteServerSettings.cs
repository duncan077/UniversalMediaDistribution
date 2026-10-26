namespace UniversalMediaDistribution.Catalog.Sync;

public sealed record RemoteServerSettings
{
    public const string DefaultServiceUrl =
        "https://umd.duncan.com.ar/";

    public string ServiceUrl { get; init; } =
        DefaultServiceUrl;

    public string Username { get; init; } = string.Empty;

    public string DeviceId { get; init; } =
        Guid.CreateVersion7().ToString("N");

    public string DeviceName { get; init; } = Environment.MachineName;

    public bool OnlineCatalogEnabled { get; init; } = true;

    public RemoteServerSettings Normalize()
    {
        if ((ServiceUrl?.Length ?? 0) > 2_048)
        {
            throw new ArgumentException(
                "The optional server URL is too long.",
                nameof(ServiceUrl));
        }

        var serviceUrl = ServiceUrl?.Trim() ?? string.Empty;
        if (serviceUrl.Length > 0 &&
            (!Uri.TryCreate(
                 serviceUrl,
                 UriKind.Absolute,
                 out var serviceUri) ||
             !IsSecureServiceUri(serviceUri)))
        {
            throw new ArgumentException(
                "The optional server URL must use HTTPS. HTTP is allowed only for loopback development.",
                nameof(ServiceUrl));
        }

        var username = Username?.Trim().ToLowerInvariant() ?? string.Empty;
        if (username.Length > 64 ||
            username.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-')))
        {
            throw new ArgumentException(
                "The account name contains unsupported characters.",
                nameof(Username));
        }

        var deviceId = DeviceId?.Trim() ?? string.Empty;
        var deviceName = DeviceName?.Trim() ?? string.Empty;
        UserSyncDocumentValidator.Validate(
            new UserSyncDocument
            {
                DeviceId = deviceId,
                DeviceName = deviceName,
                Catalog = new Transfer.CatalogTransferDocument()
            });

        return this with
        {
            ServiceUrl = serviceUrl.Length == 0
                ? string.Empty
                : EnsureTrailingSlash(new Uri(serviceUrl)).AbsoluteUri,
            Username = username,
            DeviceId = deviceId,
            DeviceName = deviceName,
            OnlineCatalogEnabled =
                serviceUrl.Length > 0 && OnlineCatalogEnabled
        };
    }

    public static bool IsSecureServiceUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.UserInfo.Length == 0 &&
        string.IsNullOrEmpty(uri.Fragment) &&
        string.IsNullOrEmpty(uri.Query) &&
        (string.Equals(
             uri.Scheme,
             Uri.UriSchemeHttps,
             StringComparison.OrdinalIgnoreCase) ||
         (string.Equals(
              uri.Scheme,
              Uri.UriSchemeHttp,
              StringComparison.OrdinalIgnoreCase) &&
          uri.IsLoopback));

    private static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/')
            ? uri
            : new Uri(uri.AbsoluteUri + '/', UriKind.Absolute);
}

public interface IRemoteServerSettingsStore
{
    ValueTask<RemoteServerSettings> LoadAsync(
        CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        RemoteServerSettings settings,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryRemoteServerSettingsStore(
    RemoteServerSettings? initialSettings = null) :
    IRemoteServerSettingsStore
{
    private readonly Lock _gate = new();
    private RemoteServerSettings _settings =
        (initialSettings ?? new RemoteServerSettings()).Normalize();

    public ValueTask<RemoteServerSettings> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_settings);
        }
    }

    public ValueTask SaveAsync(
        RemoteServerSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _settings = settings.Normalize();
        }

        return ValueTask.CompletedTask;
    }
}
