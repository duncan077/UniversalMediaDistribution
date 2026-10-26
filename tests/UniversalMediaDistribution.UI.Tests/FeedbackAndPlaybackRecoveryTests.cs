using Avalonia.Controls;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Application.Metadata;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Catalog;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.UI.Composition;
using UniversalMediaDistribution.UI.Playback;
using UniversalMediaDistribution.UI.Remote;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

[Collection("Web challenge gate")]
public sealed class FeedbackAndPlaybackRecoveryTests
{
    [Fact]
    public async Task ClosingCancelsHosterExtractionBeforeThePlayerStarts()
    {
        var solver = new PendingVideoSolver();
        WebChallengeGate.Configure(solver);
        try
        {
            using var client = new HttpClient();
            var player = new TestPlayer();
            var model = CreateModel(player, resolver: new VideoHosterResolver(client));
            var selection = Selection() with
            {
                VideoSet = new ApiContentVideoSet(ContentUnitKind.Episode, "1", null,
                    [new("waiting", new Uri("https://test.upns.pro/e/video"), kind: ApiContentVideoSourceKind.EmbeddedWeb)])
            };
            var request = model.OpenApiVideosCoreAsync(selection);
            await solver.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await model.CloseMediaPlayerCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(3));
            await request.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(solver.Token.IsCancellationRequested);
            Assert.Empty(player.Opened);
            Assert.False(model.IsEmbeddedMediaOpen);
            Assert.False(model.PlaybackFeedback.HasMessage);
        }
        finally { WebChallengeGate.Configure(null); }
    }

    private sealed class PendingVideoSolver : IWebChallengeSolver
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public ValueTask<WebChallengeClearance?> SolveAsync(Uri endpoint, CancellationToken cancellationToken) =>
            ValueTask.FromResult<WebChallengeClearance?>(null);
        public async ValueTask<ApiContentVideoSource?> ResolveVideoAsync(ApiContentVideoSource source, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return source;
        }
    }

    [Fact]
    public async Task NewProviderAndCloseCancelTheProvidersPendingRequest()
    {
        var item = new CatalogTransferItem
        {
            MediaType = "movie",
            Subtype = "anime_movie",
            Name = "Test series",
            Units = [new() { Kind = "episode", DisplayNumber = "1", SortOrder = 1 }]
        };
        var repository = new InMemoryCatalogRepository(new CatalogTransferDocument { Items = [item] });
        var browse = new CatalogBrowseService(repository);
        var overview = await browse.GetOverviewAsync(MediaType.Movie);
        var details = (await browse.GetDetailsAsync(overview.Items[0].Id))!;
        var providers = new[] { new WaitingProvider("first"), new WaitingProvider("second") };
        var configs = providers.Select(provider => new ContentSourceProviderConfiguration
        {
            Id = provider.Descriptor.Id,
            DisplayName = provider.Descriptor.DisplayName,
            Kind = ContentSourceProviderKind.Api,
            IsEnabled = true,
            BaseUrl = "https://provider.example/"
        }).ToArray();
        using var client = new HttpClient();
        var service = new ContentSourceCatalogService(browse, new CatalogJsonTransferService(repository),
            new InMemoryContentSourceProviderSettingsStore(new() { Providers = configs }), client, providers);
        var player = new TestPlayer();
        var model = CreateModel(player, repository, service);
        var selection = new ApiContentUnitLookupSelection(details, details.Units[0], "Episode 1");
        ApiContentUnitVideoCandidate Candidate(int index) => new(configs[index], new Uri(configs[index].BaseUrl),
            new("test", item), new(item), new(ContentUnitKind.Episode, "1", null), 1, [], false);

        var first = model.SelectApiVideoProviderAsync(selection, Candidate(0));
        await providers[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = model.SelectApiVideoProviderAsync(selection, Candidate(1));
        await providers[1].Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await first.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(providers[0].Token.IsCancellationRequested);
        Assert.True(model.IsBusy);
        await model.CloseMediaPlayerCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(3));
        await second.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(providers[1].Token.IsCancellationRequested);
        Assert.Empty(player.Opened);
        Assert.False(model.IsBusy);
        Assert.False(model.IsEmbeddedMediaOpen);
        Assert.False(model.PlaybackFeedback.HasMessage);
    }

    private sealed class WaitingProvider(string id) : IApiContentProvider
    {
        public ApiContentProviderDescriptor Descriptor { get; } = new(id, id);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(Uri baseUri, string query, MediaType? mediaType = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected search");
        public ValueTask<ApiContentItem> LoadItemAsync(Uri baseUri, ApiContentSearchItem searchItem,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new ApiContentItem(searchItem.PreviewItem));
        public async ValueTask<ApiContentVideoSet?> LoadUnitVideosAsync(Uri baseUri, ApiContentSearchItem searchItem,
            ApiContentUnitRequest unit, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingCancelsStartupWithoutOpeningFallback(bool embedded)
    {
        var player = new PendingPlayer();
        var model = CreateModel(player);
        var pending = model.OpenApiVideosCoreAsync(PendingSelection(embedded));
        await player.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await model.CloseMediaPlayerCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(3));
        await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(player.Token.IsCancellationRequested);
        Assert.Equal(["bad"], player.Opened);
        Assert.False(model.IsEmbeddedMediaOpen);
        Assert.False(model.IsBusy);
        Assert.False(model.PlaybackFeedback.HasMessage);
        Assert.Empty(model.ActiveVideoServers);
    }

    [Fact]
    public async Task SwitchingServerWhileBusyCancelsPreviousStartup()
    {
        var player = new PendingPlayer();
        var model = CreateModel(player);
        var first = model.OpenApiVideosCoreAsync(Selection());
        await player.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(model.IsBusy);
        await model.ActiveVideoServers[1].SelectCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(3));
        await first.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(player.Token.IsCancellationRequested);
        Assert.Equal(["bad", "good"], player.Opened);
        Assert.True(model.ActiveVideoServers[1].IsSelected);
        Assert.True(model.IsEmbeddedMediaOpen);
        Assert.False(model.IsBusy);
        Assert.False(model.PlaybackFeedback.HasMessage);
        await model.CloseMediaPlayerCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task LateFailureFromCanceledServerCannotAffectNewRequest()
    {
        var player = new PendingPlayer { IgnoreBadCancellation = true, HoldGood = true };
        var model = CreateModel(player);
        var first = model.OpenApiVideosCoreAsync(Selection());
        await player.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = model.ActiveVideoServers[1].SelectCommand.ExecuteAsync(null);
        await player.GoodStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var closes = player.Closes;
        player.ReleaseBad.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(model.IsBusy); // Old finally must not clear the new request's busy state.
        Assert.False(model.PlaybackFeedback.HasMessage);
        Assert.Equal(closes, player.Closes);
        player.ReleaseGood.TrySetResult();
        await second.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(model.ActiveVideoServers[1].IsSelected);
        await model.CloseMediaPlayerCommand.ExecuteAsync(null);
    }

    private static ApiContentVideoSelection PendingSelection(bool embedded)
    {
        var selection = Selection();
        return selection with
        {
            VideoSet = new ApiContentVideoSet(ContentUnitKind.Episode, "1", null,
            selection.VideoSet.Sources.Select(source => new ApiContentVideoSource(
                source.Label, source.PlaybackUri,
                kind: embedded ? ApiContentVideoSourceKind.EmbeddedWeb : ApiContentVideoSourceKind.DirectMedia)).ToArray())
        };
    }

    private sealed class PendingPlayer : IPlatformMediaPlayer
    {
        public bool IgnoreBadCancellation { get; init; }
        public bool HoldGood { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource GoodStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBad { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseGood { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Opened { get; } = [];
        public CancellationToken Token { get; private set; }
        public int Closes { get; private set; }
        public bool IsAvailable => true;
        public bool SupportsEmbeddedWeb => true;
        public Control? EmbeddedView => null;
        public ValueTask OpenAsync(PlatformMediaRequest request, CancellationToken cancellationToken = default) =>
            OpenCore(request.InitialVariant.Label, cancellationToken);
        public ValueTask OpenEmbeddedWebAsync(Uri source, string title, Uri? referrer = null, CancellationToken cancellationToken = default) =>
            OpenCore(source.Host.Split('.')[0], cancellationToken);
        private async ValueTask OpenCore(string label, CancellationToken token)
        {
            Opened.Add(label);
            if (label == "bad")
            {
                Token = token;
                Started.TrySetResult();
                if (IgnoreBadCancellation) await ReleaseBad.Task;
                else await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("Late server failure");
            }
            GoodStarted.TrySetResult();
            if (HoldGood) await ReleaseGood.Task.WaitAsync(token);
        }
        public ValueTask<PlatformMediaState> GetStateAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new PlatformMediaState(0, 300, true, false, false));
        public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        {
            Closes++;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ExhaustedServersKeepPlayerTitleAndRetryChoicesUntilExplicitClose()
    {
        var player = new TestPlayer { FailAll = true };
        var model = CreateModel(player);
        await model.OpenApiVideosCoreAsync(Selection());

        Assert.True(model.IsEmbeddedMediaOpen);
        Assert.Equal("Episode 1", model.ActiveMediaTitle);
        Assert.Equal(2, model.ActiveVideoServers.Count);
        Assert.All(model.ActiveVideoServers, server => Assert.False(server.IsSelected));
        Assert.True(model.PlaybackFeedback.HasMessage);
        Assert.False(model.CatalogFeedback.HasMessage);
        Assert.False(model.HasStatus);

        await model.CloseMediaPlayerCommand.ExecuteAsync(null);
        Assert.False(model.IsEmbeddedMediaOpen);
        Assert.Empty(model.ActiveVideoServers);
        Assert.False(model.PlaybackFeedback.HasMessage);
    }

    [Fact]
    public async Task FailedSwitchCanRetryTheSameServerWithoutLeavingThePlayer()
    {
        var player = new TestPlayer();
        var model = CreateModel(player);
        await model.OpenApiVideosCoreAsync(Selection());
        Assert.Equal("good", player.Opened.Last());

        await model.ActiveVideoServers[0].SelectCommand.ExecuteAsync(null);
        Assert.True(model.IsEmbeddedMediaOpen);
        Assert.True(model.PlaybackFeedback.HasMessage);
        Assert.Equal(2, model.ActiveVideoServers.Count);

        player.FailBad = false;
        await model.ActiveVideoServers[0].SelectCommand.ExecuteAsync(null);
        Assert.True(model.IsEmbeddedMediaOpen);
        Assert.False(model.PlaybackFeedback.HasMessage);
        Assert.True(model.ActiveVideoServers[0].IsSelected);
        await model.CloseMediaPlayerCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task InitialFailureTriesAnotherFeedWithoutHidingThePlayer()
    {
        var model = CreateModel(new TestPlayer());
        var visibility = new List<bool>();
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(model.IsEmbeddedMediaOpen))
                visibility.Add(model.IsEmbeddedMediaOpen);
        };

        await model.OpenApiVideosCoreAsync(Selection());
        Assert.Equal([true], visibility);
        Assert.True(model.ActiveVideoServers[1].IsSelected);
        Assert.False(model.HasError);
        Assert.False(model.HasStatus);
        await model.CloseMediaPlayerCommand.ExecuteAsync(null);
    }

    [Fact]
    public void ErrorsStayWithTheirActionAndPlaybackErrorsRevealControls()
    {
        var model = CreateModel(new TestPlayer());
        model.SetError(model.AccountFeedback, "Sign in failed");
        model.SetError(model.SourcesFeedback, "Provider failed");
        model.SetError(model.SourcesFeedback, null);
        Assert.Equal("Sign in failed", model.AccountFeedback.Message);
        Assert.False(model.SourcesFeedback.HasMessage);
        Assert.False(model.CatalogFeedback.HasMessage);

        model.IsEmbeddedMediaOpen = true;
        model.IsPlayerFullscreen = true;
        model.StatusMessage = "Saved";
        model.SetError(model.PlaybackFeedback, "Decoder failed");
        Assert.False(model.IsPlayerFullscreen);
        Assert.True(model.IsEmbeddedMediaOpen);
        Assert.False(model.HasStatus);
        Assert.Same(model.PlaybackFeedback, model.LatestErrorTarget);
    }

    [Fact]
    public async Task RuntimeFailureStaysOpenButExplicitNativeExitReturnsToDetails()
    {
        var model = CreateModel(new TestPlayer());
        await model.OpenApiVideosCoreAsync(Selection());

        model.ApplyPlaybackStateFeedback(new(42, 300, false, false, true, "Decoder failed"));
        Assert.True(model.IsEmbeddedMediaOpen);
        Assert.Equal(2, model.ActiveVideoServers.Count);
        Assert.True(model.PlaybackFeedback.HasMessage);

        model.ApplyPlaybackStateFeedback(new(42, 300, false, false, true));
        Assert.False(model.IsEmbeddedMediaOpen);
        Assert.Empty(model.ActiveVideoServers);
        Assert.False(model.PlaybackFeedback.HasMessage);
        await model.CloseMediaPlayerCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task NewToastSurvivesOlderTimeoutAndThenExpires()
    {
        var model = CreateModel(new TestPlayer());
        model.StatusMessage = "Saved";
        await Task.Delay(1700);
        model.StatusMessage = "Saved";
        await Task.Delay(1700);
        Assert.True(model.HasStatus);
        await Task.Delay(1700);
        Assert.False(model.HasStatus);
    }

    private static ApiContentVideoSelection Selection()
    {
        var item = new CatalogContentIdentity(Guid.NewGuid(), "item");
        var episode = new CatalogContentIdentity(item.CatalogItemId, "episode:1", ContentUnitKind.Episode, "1");
        return new(episode, item, "Episode 1", new ApiContentVideoSet(
            ContentUnitKind.Episode, "1", null,
            [new("bad", new Uri("https://bad.test/video.mp4")),
             new("good", new Uri("https://good.test/video.mp4"))]));
    }

    // Unused services are absent: no database, network or native window is started.
    private static MainViewModel CreateModel(IPlatformMediaPlayer player,
        InMemoryCatalogRepository? repository = null, ContentSourceCatalogService? contentService = null,
        VideoHosterResolver? resolver = null)
    {
        repository ??= new InMemoryCatalogRepository();
        var settings = new InMemoryMetadataProviderSettingsStore();
        return new MainViewModel(
            new CatalogBrowseService(repository), null!, null,
            new CatalogMetadataRefreshService(repository, settings, []),
            settings, null!, new InMemoryUserMediaStateStore(), null!, null!,
            player, [], RuntimeProfile.Desktop, contentService!, null!, null!, null!,
            null!, null!, null, new HttpRemoteMediaServerClient(), videoHosterResolver: resolver);
    }

    private sealed class TestPlayer : IPlatformMediaPlayer
    {
        public bool FailAll { get; set; }
        public bool FailBad { get; set; } = true;
        public List<string> Opened { get; } = [];
        public bool IsAvailable => true;
        public Control? EmbeddedView => null;

        public ValueTask OpenAsync(PlatformMediaRequest request, CancellationToken cancellationToken = default)
        {
            Opened.Add(request.InitialVariant.Label);
            return FailAll || FailBad && request.InitialVariant.Label == "bad"
                ? ValueTask.FromException(new InvalidOperationException("Feed unavailable"))
                : ValueTask.CompletedTask;
        }

        public ValueTask<PlatformMediaState> GetStateAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new PlatformMediaState(42, 300, true, false, false));

        public ValueTask CloseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
