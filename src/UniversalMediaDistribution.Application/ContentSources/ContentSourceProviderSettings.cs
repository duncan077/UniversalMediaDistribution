namespace UniversalMediaDistribution.Application.ContentSources;

public enum ContentSourceProviderKind
{
    Manual = 1,
    TorrentCatalog = 2,
    Api = 3,
    LiveTv = 4
}

public sealed record ContentSourceProviderConfiguration
{
    public const int MaximumProviderCount = 32;

    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required ContentSourceProviderKind Kind { get; init; }

    public bool IsEnabled { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public ContentSourceProviderConfiguration Normalize()
    {
        var id = Id?.Trim() ?? string.Empty;
        if (id.Length is < 1 or > 96 ||
            !id.All(static character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or '-'))
        {
            throw new ArgumentException(
                "A content-source provider ID must contain 1 to 96 ASCII letters, digits, dots, underscores, or hyphens.",
                nameof(Id));
        }

        var displayName = DisplayName?.Trim() ?? string.Empty;
        if (displayName.Length is < 1 or > 120)
        {
            throw new ArgumentException(
                "A content-source provider name must contain 1 to 120 characters.",
                nameof(DisplayName));
        }

        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind));
        }

        var baseUrl = NormalizeBaseUrl(BaseUrl, IsEnabled && Kind is not ContentSourceProviderKind.Manual);
        if (Kind is ContentSourceProviderKind.Manual && baseUrl.Length > 0)
        {
            throw new ArgumentException(
                "The manual magnet provider does not use a base URL.",
                nameof(BaseUrl));
        }

        return this with
        {
            Id = id,
            DisplayName = displayName,
            BaseUrl = baseUrl,
            IsEnabled = Kind is ContentSourceProviderKind.Manual || IsEnabled
        };
    }

    public static string NormalizeBaseUrl(string? value, bool required)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            if (required)
            {
                throw new ArgumentException(
                    "An enabled online provider requires a base URL.",
                    nameof(value));
            }

            return string.Empty;
        }

        if (normalized.Length > 2048 ||
            !Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "Provider base URLs must be absolute HTTP or HTTPS URLs without credentials or fragments.",
                nameof(value));
        }

        if (!IsPublicHost(uri))
        {
            throw new ArgumentException(
                "Provider base URLs must point at a public host, not at this " +
                "device or the local network.",
                nameof(value));
        }

        return uri.AbsoluteUri;
    }

    /// <summary>
    /// Whether the host is somewhere on the public internet.
    /// <para>
    /// A provider URL is fetched on every search and its origin is trusted for
    /// artwork, so without this the settings field is a way to point the app at
    /// the machine it runs on or at anything else on the LAN and have the
    /// replies rendered. Checked here, at the point the value is accepted,
    /// because that is the only place every caller passes through.
    /// </para>
    /// </summary>
    private static bool IsPublicHost(Uri uri)
    {
        var host = uri.DnsSafeHost;
        if (host.Length == 0 ||
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!System.Net.IPAddress.TryParse(host, out var address))
        {
            // A name. It may still resolve into private space, which only a
            // check at connect time could catch; the literal forms that make
            // this trivial to abuse are refused above and below.
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (System.Net.IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily is
            System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var octets = address.GetAddressBytes();
            return octets[0] switch
            {
                10 => false,
                127 => false,
                0 => false,
                172 when octets[1] is >= 16 and <= 31 => false,
                192 when octets[1] is 168 => false,
                169 when octets[1] is 254 => false,
                100 when octets[1] is >= 64 and <= 127 => false,
                >= 224 => false,
                _ => true
            };
        }

        var bytes = address.GetAddressBytes();
        return !address.IsIPv6LinkLocal &&
            !address.IsIPv6SiteLocal &&
            !address.IsIPv6Multicast &&
            (bytes[0] & 0xFE) != 0xFC;
    }
}

public sealed record ContentSourceProviderSettings
{
    public IReadOnlyList<ContentSourceProviderConfiguration> Providers { get; set; } = [];

    public ContentSourceProviderSettings Normalize()
    {
        if (Providers.Count > ContentSourceProviderConfiguration.MaximumProviderCount)
        {
            throw new ArgumentException(
                $"At most {ContentSourceProviderConfiguration.MaximumProviderCount} content-source providers can be configured.",
                nameof(Providers));
        }

        var normalized = Providers
            .Select(static provider =>
                (provider ?? throw new ArgumentException(
                    "Content-source provider settings cannot contain null entries."))
                .Normalize())
            .ToArray();
        if (normalized
            .GroupBy(static provider => provider.Id, StringComparer.Ordinal)
            .Any(static group => group.Count() > 1))
        {
            throw new ArgumentException(
                "Content-source provider IDs must be unique.",
                nameof(Providers));
        }

        return this with { Providers = normalized };
    }
}

public interface IContentSourceProviderSettingsStore
{
    ValueTask<ContentSourceProviderSettings> LoadAsync(
        CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        ContentSourceProviderSettings settings,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryContentSourceProviderSettingsStore :
    IContentSourceProviderSettingsStore
{
    private readonly Lock _gate = new();
    private ContentSourceProviderSettings _settings;

    public InMemoryContentSourceProviderSettingsStore(
        ContentSourceProviderSettings? initialSettings = null)
    {
        _settings = (initialSettings ?? new ContentSourceProviderSettings()).Normalize();
    }

    public ValueTask<ContentSourceProviderSettings> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_settings);
        }
    }

    public ValueTask SaveAsync(
        ContentSourceProviderSettings settings,
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
