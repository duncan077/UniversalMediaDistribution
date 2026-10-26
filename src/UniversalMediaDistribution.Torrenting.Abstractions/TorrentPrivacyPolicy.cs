namespace UniversalMediaDistribution.Torrenting;

public static class TorrentPrivacyPolicy
{
    private static readonly HashSet<string> ForbiddenPrivateMagnetParameters =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "as",
            "ws",
            "xs",
            "x.pe"
        };

    public static void Validate(
        TorrentEngineCapabilities capabilities,
        TorrentAddOptions options)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.IsPrivate)
        {
            return;
        }

        if (!capabilities.SupportsPrivateTorrents)
        {
            throw new InvalidOperationException(
                $"{capabilities.DisplayName} does not support private torrents.");
        }

        if (options.Trackers.Count == 0)
        {
            throw new InvalidOperationException(
                "A private torrent requires an approved encrypted tracker.");
        }

        var requiredTransport = capabilities.EngineKind switch
        {
            TorrentEngineKind.MonoTorrent => TrackerTransport.Https,
            TorrentEngineKind.WebTorrent => TrackerTransport.Wss,
            _ => throw new InvalidOperationException(
                $"{capabilities.DisplayName} has no private tracker policy.")
        };
        if (options.Trackers.Any(tracker => tracker.Transport != requiredTransport))
        {
            throw new InvalidOperationException(
                $"Private {capabilities.DisplayName} sessions accept only " +
                $"{requiredTransport.ToString().ToUpperInvariant()} trackers.");
        }
    }

    public static void Validate(
        TorrentEngineCapabilities capabilities,
        TorrentDescriptor descriptor,
        TorrentAddOptions options)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Validate(capabilities, options);
        if (!options.IsPrivate || descriptor.MagnetUri is null)
        {
            return;
        }

        var approvedTrackers = options.Trackers
            .Select(static tracker => tracker.Endpoint.AbsoluteUri)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var embeddedApprovedTrackers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queryIndex = descriptor.MagnetUri.IndexOf('?');
        if (queryIndex >= 0)
        {
            foreach (var segment in descriptor.MagnetUri[(queryIndex + 1)..]
                         .Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = segment.IndexOf('=');
                var key = DecodeMagnetValue(
                    separator < 0 ? segment : segment[..separator]);
                if (ForbiddenPrivateMagnetParameters.Contains(key))
                {
                    throw new InvalidOperationException(
                        "A private magnet cannot contain web seeds, exact sources, " +
                        "or direct peer endpoints.");
                }

                if (!string.Equals(key, "tr", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = DecodeMagnetValue(
                    separator < 0 ? string.Empty : segment[(separator + 1)..]);
                if (!Uri.TryCreate(value, UriKind.Absolute, out var tracker) ||
                    !approvedTrackers.Contains(tracker.AbsoluteUri))
                {
                    throw new InvalidOperationException(
                        "A private magnet contains an unapproved tracker.");
                }

                embeddedApprovedTrackers.Add(tracker.AbsoluteUri);
            }
        }

        if (capabilities.EngineKind is TorrentEngineKind.MonoTorrent &&
            !approvedTrackers.SetEquals(embeddedApprovedTrackers))
        {
            throw new InvalidOperationException(
                "A native private magnet must contain every approved HTTPS tracker.");
        }
    }

    private static string DecodeMagnetValue(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException exception)
        {
            throw new InvalidOperationException(
                "A private magnet contains invalid parameter encoding.",
                exception);
        }
    }
}
