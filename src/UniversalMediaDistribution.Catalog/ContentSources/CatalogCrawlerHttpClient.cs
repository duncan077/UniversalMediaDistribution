using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>Public-only outbound traffic with DNS pinning, explicit redirects and per-host pacing.</summary>
public static class CatalogCrawlerHttpClient
{
    private static readonly AsyncLocal<ProviderPacing?> ProviderInterval = new();

    private sealed class ProviderPacing(TimeSpan interval)
    {
        internal TimeSpan Interval { get; } = interval;
        internal HostQueue Queue { get; } = new();
    }

    /// <summary>Associates all host/backend requests of one async provider worker with its configured cadence.</summary>
    public static IDisposable UseProviderInterval(TimeSpan interval)
    {
        if (interval < TimeSpan.Zero || interval > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(interval));
        var previous = ProviderInterval.Value;
        ProviderInterval.Value = new ProviderPacing(interval);
        return new IntervalScope(previous);
    }

    private sealed class IntervalScope(ProviderPacing? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ProviderInterval.Value = previous;
        }
    }

    public static HttpClient Create(TimeSpan? minimumInterval = null) => new(new PacedHandler(minimumInterval ?? TimeSpan.FromSeconds(2))
    {
        InnerHandler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 1,
            ConnectCallback = ConnectAsync
        }
    })
    { Timeout = TimeSpan.FromSeconds(90) };

    public static bool IsPublicUri(Uri uri) => HosterExtractorBase.IsSafePublicHttpUri(uri) && uri.Port is 80 or 443;

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        Exception? last = null;
        foreach (var address in addresses)
        {
            if (!IsPublicUri(new UriBuilder("http", address.ToString(), context.DnsEndPoint.Port).Uri)) continue;
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception)
            {
                socket.Dispose();
                if (exception is OperationCanceledException) throw;
                last = exception;
            }
        }
        throw new HttpRequestException("No reachable public address for the catalog endpoint.", last);
    }

    private sealed class HostQueue
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal DateTimeOffset NextRequest { get; set; }
    }

    internal sealed class PacedHandler(TimeSpan interval) : DelegatingHandler
    {
        private readonly ConcurrentDictionary<string, HostQueue> _hosts = new(StringComparer.OrdinalIgnoreCase);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not { } uri || !IsPublicUri(uri))
                throw new HttpRequestException("The crawler rejected a non-public endpoint.");
            var provider = ProviderInterval.Value;
            var minimumInterval = provider?.Interval ?? interval;
            var host = _hosts.GetOrAdd(uri.IdnHost, static _ => new HostQueue());
            if (provider is not null) await provider.Queue.Gate.WaitAsync(cancellationToken);
            try
            {
                await host.Gate.WaitAsync(cancellationToken);
                try
                {
                    var next = provider is not null && provider.Queue.NextRequest > host.NextRequest
                        ? provider.Queue.NextRequest : host.NextRequest;
                    var delay = next - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
                    var response = await base.SendAsync(request, cancellationToken);
                    host.NextRequest = DateTimeOffset.UtcNow + minimumInterval;
                    if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                    {
                        var retry = response.Headers.RetryAfter?.Delta ??
                            (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromHours(1);
                        response.Dispose();
                        throw new CatalogProviderThrottledException(TimeSpan.FromSeconds(Math.Clamp(retry.TotalSeconds, 60, 86_400)));
                    }
                    return response;
                }
                finally { host.NextRequest = host.NextRequest > DateTimeOffset.UtcNow ? host.NextRequest : DateTimeOffset.UtcNow + minimumInterval; host.Gate.Release(); }
            }
            finally
            {
                if (provider is not null)
                {
                    provider.Queue.NextRequest = DateTimeOffset.UtcNow + minimumInterval;
                    provider.Queue.Gate.Release();
                }
            }
        }
    }
}

public sealed class CatalogProviderThrottledException(TimeSpan retryAfter) : HttpRequestException("The catalog provider requested a retry later.")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}
