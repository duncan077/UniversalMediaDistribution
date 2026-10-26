using System.Net;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class ApiContentProviderConcurrencyTests
{
    private static readonly Uri BaseUri = new("https://example.test/");

    [Fact]
    public void LimitsAreThreeProvidersAndFourRequestsPerProvider()
    {
        Assert.Equal(
            3,
            ApiContentProviderConcurrencyCoordinator
                .MaximumConcurrentProviderOperations);
        Assert.Equal(
            4,
            ApiContentProviderConcurrencyCoordinator
                .MaximumConcurrentRequestsPerProvider);
    }

    [Fact]
    public async Task CoordinatorAllowsOnlyThreeProviderOperationsAtOnce()
    {
        var tracker = new ProviderOperationTracker();
        var coordinator = new ApiContentProviderConcurrencyCoordinator();
        var providers = Enumerable.Range(0, 6)
            .Select(index => coordinator.Coordinate(
                new BlockingProvider($"provider-{index}", tracker)))
            .ToArray();

        var searches = providers
            .Select(provider => provider.SearchAsync(
                    BaseUri,
                    "query")
                .AsTask())
            .ToArray();

        await tracker.ThirdEntry.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, tracker.Active);
        Assert.False(tracker.FourthEntry.Task.IsCompleted);

        tracker.ReleaseAll();
        await Task.WhenAll(searches).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(6, tracker.Entered);
        Assert.Equal(3, tracker.MaximumActive);
        Assert.Equal(0, tracker.Active);
    }

    [Fact]
    public async Task ProvidersHaveIndependentFourRequestQueues()
    {
        var handler = new CountingHandler();
        using var httpClient = new HttpClient(handler);
        var coordinator = new ApiContentProviderConcurrencyCoordinator();
        var first = new QueueingProvider("first-provider", httpClient);
        var second = new QueueingProvider("second-provider", httpClient);
        var firstSearch = coordinator.Coordinate(first)
            .SearchAsync(BaseUri, "query")
            .AsTask();
        var secondSearch = coordinator.Coordinate(second)
            .SearchAsync(BaseUri, "query")
            .AsTask();

        await Task.WhenAll(
                first.FourRequestsSent,
                second.FourRequestsSent)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(8, handler.RequestCount);
        Assert.False(firstSearch.IsCompleted);
        Assert.False(secondSearch.IsCompleted);

        first.ReleaseOne();
        second.ReleaseOne();
        await Task.WhenAll(firstSearch, secondSearch)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(10, handler.RequestCount);
    }

    [Fact]
    public async Task CanceledQueuedRequestDoesNotConsumeCapacity()
    {
        var handler = new CountingHandler();
        using var httpClient = new HttpClient(handler);
        var queue = new ApiProviderRequestQueue(4);
        using var requestScope = ApiProviderRequestContext.Enter(queue);
        var responses = new List<HttpResponseMessage>();

        try
        {
            for (var index = 0; index < 4; index++)
            {
                using var request = CreateRequest(index);
                responses.Add(await ApiProviderHttp.SendAsync(
                    httpClient,
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    CancellationToken.None));
            }

            using var cancellationSource = new CancellationTokenSource();
            using var canceledRequest = CreateRequest(4);
            var canceledSend = ApiProviderHttp.SendAsync(
                httpClient,
                canceledRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationSource.Token);

            Assert.False(canceledSend.IsCompleted);
            cancellationSource.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await canceledSend);
            Assert.Equal(4, handler.RequestCount);

            responses[0].Dispose();
            responses.RemoveAt(0);
            using var replacementRequest = CreateRequest(5);
            responses.Add(await ApiProviderHttp.SendAsync(
                    httpClient,
                    replacementRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal(5, handler.RequestCount);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    private static HttpRequestMessage CreateRequest(int index) =>
        new(HttpMethod.Get, new Uri(BaseUri, $"request-{index}"));

    private sealed class BlockingProvider(
        string id,
        ProviderOperationTracker tracker) : IApiContentProvider
    {
        public ApiContentProviderDescriptor Descriptor { get; } =
            new(id, id);

        public async ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(
            Uri baseUri,
            string query,
            MediaType? mediaType = null,
            CancellationToken cancellationToken = default)
        {
            await tracker.EnterAsync(cancellationToken);
            return [];
        }

        public ValueTask<ApiContentItem> LoadItemAsync(
            Uri baseUri,
            ApiContentSearchItem searchItem,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class QueueingProvider(
        string id,
        HttpClient httpClient) : IApiContentProvider
    {
        private readonly TaskCompletionSource _fourRequestsSent = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseOne = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ApiContentProviderDescriptor Descriptor { get; } =
            new(id, id);

        internal Task FourRequestsSent => _fourRequestsSent.Task;

        internal void ReleaseOne() => _releaseOne.TrySetResult();

        public async ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(
            Uri baseUri,
            string query,
            MediaType? mediaType = null,
            CancellationToken cancellationToken = default)
        {
            var responses = new List<HttpResponseMessage>();
            try
            {
                for (var index = 0; index < 4; index++)
                {
                    using var request = CreateRequest(index);
                    responses.Add(await ApiProviderHttp.SendAsync(
                        httpClient,
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken));
                }

                _fourRequestsSent.TrySetResult();
                using var fifthRequest = CreateRequest(4);
                var fifthSend = ApiProviderHttp.SendAsync(
                    httpClient,
                    fifthRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                await _releaseOne.Task.WaitAsync(cancellationToken);
                responses[0].Dispose();
                responses.RemoveAt(0);
                responses.Add(await fifthSend);
                return [];
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }
        }

        public ValueTask<ApiContentItem> LoadItemAsync(
            Uri baseUri,
            ApiContentSearchItem searchItem,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ProviderOperationTracker
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        private int _entered;
        private int _maximumActive;

        internal int Active => Volatile.Read(ref _active);

        internal int Entered => Volatile.Read(ref _entered);

        internal int MaximumActive => Volatile.Read(ref _maximumActive);

        internal TaskCompletionSource ThirdEntry { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource FourthEntry { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal async Task EnterAsync(CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            SetMaximum(active);
            var entered = Interlocked.Increment(ref _entered);
            if (entered >= 3)
            {
                ThirdEntry.TrySetResult();
            }

            if (entered >= 4)
            {
                FourthEntry.TrySetResult();
            }

            try
            {
                await _release.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        internal void ReleaseAll() => _release.TrySetResult();

        private void SetMaximum(int active)
        {
            var observed = Volatile.Read(ref _maximumActive);
            while (active > observed)
            {
                var previous = Interlocked.CompareExchange(
                    ref _maximumActive,
                    active,
                    observed);
                if (previous == observed)
                {
                    return;
                }

                observed = previous;
            }
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _requestCount;

        internal int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1])
            });
        }
    }
}
