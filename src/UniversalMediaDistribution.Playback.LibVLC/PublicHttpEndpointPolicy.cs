using System.Net;
using System.Net.Sockets;

namespace UniversalMediaDistribution.Playback.LibVLC;

/// <summary>
/// Keeps media fetched from an untrusted playlist on public HTTP endpoints.
/// The connect callback validates the resolved address and then connects to
/// that exact address, closing the DNS-rebinding gap between checking a name
/// and opening the socket.
/// </summary>
internal static class PublicHttpEndpointPolicy
{
    public static bool IsSafePublicHttpUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri ||
            uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0)
        {
            return false;
        }

        var host = uri.IdnHost.TrimEnd('.');
        if (string.Equals(
                host,
                "localhost",
                StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(
                ".localhost",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IPAddress.TryParse(host, out var address) ||
               IsPublicAddress(address);
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes.Length switch
        {
            4 => IsPublicIpv4Address(bytes),
            16 => IsPublicIpv6Address(address, bytes),
            _ => false
        };
    }

    public static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var endpoint = context.DnsEndPoint;
        IPAddress[] addresses;
        if (IPAddress.TryParse(endpoint.Host, out var literalAddress))
        {
            addresses = [literalAddress];
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(
                endpoint.Host,
                cancellationToken);
        }

        Exception? lastFailure = null;
        foreach (var address in addresses)
        {
            if (!IsPublicAddress(address))
            {
                continue;
            }

            var socket = new Socket(
                address.AddressFamily,
                SocketType.Stream,
                ProtocolType.Tcp)
            {
                NoDelay = true
            };
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, endpoint.Port),
                    cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException)
            {
                socket.Dispose();
                throw;
            }
            catch (Exception exception) when (
                exception is SocketException or IOException)
            {
                socket.Dispose();
                lastFailure = exception;
            }
        }

        throw new HttpRequestException(
            $"The media host '{endpoint.Host}' did not resolve to a reachable public address.",
            lastFailure);
    }

    private static bool IsPublicIpv4Address(ReadOnlySpan<byte> address)
    {
        var first = address[0];
        var second = address[1];
        return
            first != 0 &&
            first != 10 &&
            first != 127 &&
            !(first == 100 && second is >= 64 and <= 127) &&
            !(first == 169 && second == 254) &&
            !(first == 172 && second is >= 16 and <= 31) &&
            !(first == 192 && second == 168) &&
            !(first == 192 && second == 0 && address[2] is 0 or 2) &&
            !(first == 192 && second == 88 && address[2] == 99) &&
            !(first == 198 && second is 18 or 19) &&
            !(first == 198 && second == 51 && address[2] == 100) &&
            !(first == 203 && second == 0 && address[2] == 113) &&
            first < 224;
    }

    private static bool IsPublicIpv6Address(
        IPAddress address,
        ReadOnlySpan<byte> bytes) =>
        !address.Equals(IPAddress.IPv6Any) &&
        !address.Equals(IPAddress.IPv6None) &&
        !address.IsIPv6LinkLocal &&
        !address.IsIPv6SiteLocal &&
        !address.IsIPv6Multicast &&
        // Unspecified/IPv4-compatible and NAT64 special-use space.
        bytes[0] != 0 &&
        !HasPrefix(bytes, [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]) &&
        // Teredo, benchmarking, ORCHID, and documentation networks.
        !HasPrefix(bytes, [0x20, 0x01, 0x00, 0x00]) &&
        !HasPrefix(bytes, [0x20, 0x01, 0x00, 0x02, 0x00, 0x00]) &&
        !(bytes[0] == 0x20 &&
          bytes[1] == 0x01 &&
          bytes[2] == 0x00 &&
          (bytes[3] & 0xf0) is 0x10 or 0x20) &&
        !HasPrefix(bytes, [0x20, 0x01, 0x0d, 0xb8]) &&
        // 6to4 can embed a private IPv4 destination.
        !HasPrefix(bytes, [0x20, 0x02]) &&
        // Current IPv6 documentation and segment-routing special-use blocks.
        !(bytes[0] == 0x3f &&
          bytes[1] == 0xff &&
          (bytes[2] & 0xf0) == 0) &&
        !HasPrefix(bytes, [0x5f, 0x00]) &&
        // RFC4193 unique-local address space (fc00::/7).
        (bytes[0] & 0xfe) != 0xfc;

    private static bool HasPrefix(
        ReadOnlySpan<byte> address,
        ReadOnlySpan<byte> prefix) =>
        address.StartsWith(prefix);
}
