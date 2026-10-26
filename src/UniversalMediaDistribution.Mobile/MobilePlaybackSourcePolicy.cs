using System.Net;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.Mobile;

public static class MobilePlaybackSourcePolicy
{
    public static Uri RequirePlaybackUri(PlatformMediaRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.InitialVariant.Source.PlaybackUri ??
            throw new NotSupportedException(
                "Mobile playback requires an HTTP or HTTPS streaming address.");

        if (!uri.IsAbsoluteUri ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            (uri.IsLoopback &&
             !string.Equals(
                 uri.Scheme,
                 Uri.UriSchemeHttp,
                 StringComparison.OrdinalIgnoreCase)) ||
            (IPAddress.TryParse(uri.Host, out var address) &&
             !uri.IsLoopback &&
             !IsPublicAddress(address)))
        {
            throw new NotSupportedException(
                "Mobile playback accepts the app's loopback HTTP stream or a public HTTP/HTTPS stream.");
        }

        return uri;
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
}
