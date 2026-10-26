using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using UniversalMediaDistribution.Application.ContentSources;

namespace UniversalMediaDistribution.Catalog.LiveTv;

public sealed record LiveTvProviderDescriptor(
    string Id,
    string DisplayName,
    Uri PlaylistUri,
    bool IsEnabledByDefault = true);

public sealed record LiveTvChannel(
    string ProviderId,
    string ProviderName,
    string Name,
    Uri StreamUri,
    string? ChannelId = null,
    Uri? LogoUri = null,
    string? Group = null,
    Uri? Referrer = null,
    string? UserAgent = null)
{
    public string StableKey =>
        $"{ProviderId}\n{ChannelId ?? string.Empty}\n{StreamUri.AbsoluteUri}";
}

public sealed record LiveTvPlaybackSource(
    Uri StreamUri,
    Uri? Referrer = null,
    string? UserAgent = null,
    string Label = "Automatic",
    IReadOnlyList<LiveTvPlaybackVariant>? AlternateVariants = null);

public sealed record LiveTvPlaybackVariant(
    string Label,
    Uri StreamUri,
    Uri? Referrer = null,
    string? UserAgent = null);

public sealed record LiveTvProviderFailure(
    string ProviderId,
    string ProviderName,
    string Message);

public sealed record LiveTvCatalogLoadResult(
    IReadOnlyList<LiveTvChannel> Channels,
    IReadOnlyList<LiveTvProviderFailure> Failures,
    int LoadedProviderCount);

public interface ILiveTvProvider
{
    LiveTvProviderDescriptor Descriptor { get; }

    Task<IReadOnlyList<LiveTvChannel>> LoadAsync(
        CancellationToken cancellationToken = default);

    ValueTask<LiveTvPlaybackSource> ResolvePlaybackAsync(
        LiveTvChannel channel,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(
            new LiveTvPlaybackSource(
                channel.StreamUri,
                channel.Referrer,
                channel.UserAgent));
}

public sealed partial class M3uLiveTvProvider(
    HttpClient httpClient,
    LiveTvProviderDescriptor descriptor) : ILiveTvProvider
{
    private const int MaximumPlaylistBytes = 4 * 1024 * 1024;
    private const int MaximumChannels = 5_000;
    private const int MaximumHeaderValueLength = 512;
    private const string RequestUserAgent =
        "UniversalMediaDistribution/1.0 (+https://umd.duncan.com.ar)";

    public LiveTvProviderDescriptor Descriptor { get; } =
        ValidateDescriptor(descriptor);

    public async Task<IReadOnlyList<LiveTvChannel>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            Descriptor.PlaylistUri);
        request.Headers.UserAgent.ParseAdd(RequestUserAgent);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue(
                "application/vnd.apple.mpegurl"));
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("audio/x-mpegurl"));
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/plain", 0.8));

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaximumPlaylistBytes)
        {
            throw new InvalidDataException(
                $"The {Descriptor.DisplayName} playlist is larger than " +
                $"{MaximumPlaylistBytes / (1024 * 1024)} MiB.");
        }

        var playlistUri =
            response.RequestMessage?.RequestUri ?? Descriptor.PlaylistUri;
        if (!IsSafeHttpUri(playlistUri))
        {
            throw new InvalidDataException(
                $"{Descriptor.DisplayName} redirected to an unsupported address.");
        }

        await using var responseStream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var boundedStream = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await responseStream.ReadAsync(
                buffer,
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (boundedStream.Length + read > MaximumPlaylistBytes)
            {
                throw new InvalidDataException(
                    $"The {Descriptor.DisplayName} playlist is larger than " +
                    $"{MaximumPlaylistBytes / (1024 * 1024)} MiB.");
            }

            await boundedStream.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }

        boundedStream.Position = 0;
        using var reader = new StreamReader(
            boundedStream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: false);
        var playlist = await reader.ReadToEndAsync(cancellationToken);
        return Parse(playlist, playlistUri);
    }

    internal IReadOnlyList<LiveTvChannel> Parse(
        string playlist,
        Uri playlistUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlist);
        ArgumentNullException.ThrowIfNull(playlistUri);

        var channels = new List<LiveTvChannel>();
        var seenStreams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        PendingChannel? pending = null;

        using var reader = new StringReader(playlist);
        while (reader.ReadLine() is { } rawLine)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                pending = ParseExtInf(line);
                continue;
            }

            if (pending is not null &&
                line.StartsWith(
                    "#EXTVLCOPT:",
                    StringComparison.OrdinalIgnoreCase))
            {
                ApplyVlcOption(pending, line["#EXTVLCOPT:".Length..]);
                continue;
            }

            if (pending is not null &&
                line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase))
            {
                pending.Group ??= CleanValue(line["#EXTGRP:".Length..]);
                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            if (pending is null ||
                channels.Count >= MaximumChannels ||
                !TryResolveSafeHttpUri(
                    playlistUri,
                    line,
                    out var streamUri))
            {
                pending = null;
                continue;
            }

            var streamKey = streamUri.AbsoluteUri;
            if (!seenStreams.Add(streamKey))
            {
                pending = null;
                continue;
            }

            var name =
                CleanValue(pending.Name) ??
                CleanValue(pending.Attributes.GetValueOrDefault("tvg-name")) ??
                CleanValue(pending.Attributes.GetValueOrDefault("tvg-id")) ??
                streamUri.Host;
            var channelId =
                CleanValue(pending.Attributes.GetValueOrDefault("tvg-id"));
            var group =
                CleanValue(pending.Group) ??
                CleanValue(
                    pending.Attributes.GetValueOrDefault("group-title"));
            var logoUri = TryResolveSafeHttpUri(
                    playlistUri,
                    pending.Attributes.GetValueOrDefault("tvg-logo"),
                    out var parsedLogo)
                ? parsedLogo
                : null;
            var referrer = TryResolveSafeHttpUri(
                    playlistUri,
                    pending.Referrer ??
                    pending.Attributes.GetValueOrDefault("http-referrer"),
                    out var parsedReferrer)
                ? parsedReferrer
                : null;
            var userAgent = CleanHeaderValue(
                pending.UserAgent ??
                pending.Attributes.GetValueOrDefault("http-user-agent") ??
                pending.Attributes.GetValueOrDefault("user-agent"));

            channels.Add(
                new LiveTvChannel(
                    Descriptor.Id,
                    Descriptor.DisplayName,
                    name,
                    streamUri,
                    channelId,
                    logoUri,
                    group,
                    referrer,
                    userAgent));
            pending = null;
        }

        return channels;
    }

    private static PendingChannel ParseExtInf(string line)
    {
        var metadata = line["#EXTINF:".Length..];
        var titleSeparator = FindTitleSeparator(metadata);
        var attributesText = titleSeparator >= 0
            ? metadata[..titleSeparator]
            : metadata;
        var name = titleSeparator >= 0
            ? metadata[(titleSeparator + 1)..]
            : null;
        var attributes = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (Match match in AttributeRegex().Matches(attributesText))
        {
            var key = match.Groups["key"].Value;
            var value = match.Groups["quoted"].Success
                ? match.Groups["quoted"].Value
                : match.Groups["bare"].Value;
            attributes[key] = value;
        }

        return new PendingChannel(attributes, CleanValue(name));
    }

    private static void ApplyVlcOption(
        PendingChannel pending,
        string option)
    {
        var separator = option.IndexOf('=');
        if (separator <= 0)
        {
            return;
        }

        var key = option[..separator].Trim();
        var value = option[(separator + 1)..].Trim();
        if (key.Equals(
                "http-user-agent",
                StringComparison.OrdinalIgnoreCase) ||
            key.Equals("user-agent", StringComparison.OrdinalIgnoreCase))
        {
            pending.UserAgent = value;
        }
        else if (
            key.Equals(
                "http-referrer",
                StringComparison.OrdinalIgnoreCase) ||
            key.Equals("http-referer", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("referrer", StringComparison.OrdinalIgnoreCase))
        {
            pending.Referrer = value;
        }
    }

    private static int FindTitleSeparator(string metadata)
    {
        var inQuotes = false;
        for (var index = 0; index < metadata.Length; index++)
        {
            if (metadata[index] == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (metadata[index] == ',' && !inQuotes)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TryResolveSafeHttpUri(
        Uri baseUri,
        string? value,
        out Uri uri)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            Uri.TryCreate(baseUri, value.Trim(), out var candidate) &&
            IsSafeHttpUri(candidate))
        {
            uri = candidate;
            return true;
        }

        uri = null!;
        return false;
    }

    private static bool IsSafeHttpUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri ||
            uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        return !IPAddress.TryParse(uri.Host, out var address) ||
            IsPublicAddress(address);
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.None) ||
            address.Equals(IPAddress.IPv6None) ||
            address.IsIPv6LinkLocal ||
            address.IsIPv6Multicast)
        {
            return false;
        }

        var bytes = address.MapToIPv6().IsIPv4MappedToIPv6
            ? address.MapToIPv4().GetAddressBytes()
            : null;
        if (bytes is not null)
        {
            return bytes[0] switch
            {
                0 or 10 or 127 => false,
                100 when bytes[1] is >= 64 and <= 127 => false,
                169 when bytes[1] == 254 => false,
                172 when bytes[1] is >= 16 and <= 31 => false,
                192 when bytes[1] == 168 => false,
                198 when bytes[1] is 18 or 19 => false,
                >= 224 => false,
                _ => true
            };
        }

        var ipv6 = address.GetAddressBytes();
        return ipv6.Length == 16 &&
            (ipv6[0] & 0xFE) != 0xFC;
    }

    private static string? CleanValue(string? value)
    {
        var cleaned = value?.Trim().Trim('"').Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static string? CleanHeaderValue(string? value)
    {
        var cleaned = CleanValue(value);
        if (cleaned is null ||
            cleaned.Length > MaximumHeaderValueLength ||
            cleaned.Contains('\r') ||
            cleaned.Contains('\n'))
        {
            return null;
        }

        return cleaned;
    }

    private static LiveTvProviderDescriptor ValidateDescriptor(
        LiveTvProviderDescriptor value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(value.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(value.DisplayName);
        if (!IsSafeHttpUri(value.PlaylistUri))
        {
            throw new ArgumentException(
                "A live TV playlist must use a public HTTP or HTTPS URL.",
                nameof(value));
        }

        return value;
    }

    [GeneratedRegex(
        "(?<key>[A-Za-z0-9_-]+)=(?:\"(?<quoted>[^\"]*)\"|(?<bare>[^\\s]+))",
        RegexOptions.CultureInvariant)]
    private static partial Regex AttributeRegex();

    private sealed class PendingChannel(
        Dictionary<string, string> attributes,
        string? name)
    {
        public Dictionary<string, string> Attributes { get; } = attributes;

        public string? Name { get; } = name;

        public string? Group { get; set; }

        public string? Referrer { get; set; }

        public string? UserAgent { get; set; }
    }
}

public sealed class LiveTvCatalogService(
    IReadOnlyList<ILiveTvProvider> providers,
    IContentSourceProviderSettingsStore? settingsStore = null)
{
    private readonly IReadOnlyList<ILiveTvProvider> _providers =
        providers?.ToArray() ??
        throw new ArgumentNullException(nameof(providers));
    private readonly IReadOnlyDictionary<string, ILiveTvProvider>
        _providerById = providers
            .GroupBy(
                static provider => provider.Descriptor.Id,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.First(),
                StringComparer.OrdinalIgnoreCase);
    private readonly IContentSourceProviderSettingsStore? _settingsStore =
        settingsStore;

    public async Task<LiveTvCatalogLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var enabledProviderIds =
            await LoadEnabledProviderIdsAsync(cancellationToken);
        var enabledProviders = _providers.Where(provider =>
            enabledProviderIds.Contains(provider.Descriptor.Id));
        var tasks = enabledProviders.Select(
            provider => LoadProviderAsync(provider, cancellationToken));
        var results = await Task.WhenAll(tasks);
        cancellationToken.ThrowIfCancellationRequested();

        var channels = results
            .SelectMany(static result => result.Channels)
            .OrderBy(static channel => channel.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static channel => channel.ProviderName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var failures = results
            .Where(static result => result.Failure is not null)
            .Select(static result => result.Failure!)
            .ToArray();
        var loadedProviderCount = results.Count(
            static result => result.Failure is null);

        return new LiveTvCatalogLoadResult(
            channels,
            failures,
            loadedProviderCount);
    }

    private async ValueTask<HashSet<string>> LoadEnabledProviderIdsAsync(
        CancellationToken cancellationToken)
    {
        var enabled = _providers
            .Where(static provider =>
                provider.Descriptor.IsEnabledByDefault)
            .Select(static provider => provider.Descriptor.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_settingsStore is null)
        {
            return enabled;
        }

        try
        {
            var stored = (await _settingsStore.LoadAsync(cancellationToken))
                .Normalize();
            var configuredById = stored.Providers
                .Where(static configuration =>
                    configuration.Kind is ContentSourceProviderKind.LiveTv)
                .ToDictionary(
                    static configuration => configuration.Id,
                    StringComparer.OrdinalIgnoreCase);
            foreach (var provider in _providers)
            {
                configuredById.TryGetValue(
                    provider.Descriptor.Id,
                    out var configured);
                if (configured?.IsEnabled is true)
                {
                    enabled.Add(provider.Descriptor.Id);
                }
                else if (configured is not null)
                {
                    enabled.Remove(provider.Descriptor.Id);
                }
            }
        }
        catch (ArgumentException)
        {
            // Invalid persisted settings must not make Live TV disappear.
        }

        return enabled;
    }

    public async ValueTask<LiveTvPlaybackSource> ResolvePlaybackAsync(
        LiveTvChannel channel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var enabledProviderIds =
            await LoadEnabledProviderIdsAsync(cancellationToken);
        if (!enabledProviderIds.Contains(channel.ProviderId))
        {
            throw new InvalidOperationException(
                $"{channel.ProviderName} is disabled in Settings.");
        }

        if (!_providerById.TryGetValue(channel.ProviderId, out var provider))
        {
            return new LiveTvPlaybackSource(
                channel.StreamUri,
                channel.Referrer,
                channel.UserAgent);
        }

        return await provider.ResolvePlaybackAsync(
            channel,
            cancellationToken);
    }

    private static async Task<ProviderLoadResult> LoadProviderAsync(
        ILiveTvProvider provider,
        CancellationToken cancellationToken)
    {
        try
        {
            var channels = await provider.LoadAsync(cancellationToken);
            return new ProviderLoadResult(channels, null);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ProviderLoadResult(
                [],
                new LiveTvProviderFailure(
                    provider.Descriptor.Id,
                    provider.Descriptor.DisplayName,
                    exception.Message));
        }
    }

    private sealed record ProviderLoadResult(
        IReadOnlyList<LiveTvChannel> Channels,
        LiveTvProviderFailure? Failure);
}
