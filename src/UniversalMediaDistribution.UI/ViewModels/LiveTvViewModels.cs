using CommunityToolkit.Mvvm.Input;
using UniversalMediaDistribution.Catalog.LiveTv;
using UniversalMediaDistribution.Torrenting.Ingestion;
using UniversalMediaDistribution.UI.Filtering;
using UniversalMediaDistribution.UI.Localization;

namespace UniversalMediaDistribution.UI.ViewModels;

public sealed class LiveTvFilterOptionViewModel(
    string? value,
    string displayName) : ViewModelBase
{
    public string? Value { get; } = value;

    public string DisplayName { get; } = displayName;
}

public sealed class LiveTvChannelViewModel
    : ViewModelBase, IDisposable, ILiveTvChannelFilterTarget
{
    public LiveTvChannelViewModel(
        LiveTvChannel channel,
        TorrentMediaSessionService mediaSessionService,
        Func<LiveTvChannelViewModel, Task> playAsync)
    {
        Channel = channel ?? throw new ArgumentNullException(nameof(channel));
        ArgumentNullException.ThrowIfNull(mediaSessionService);
        ArgumentNullException.ThrowIfNull(playAsync);

        if (channel.LogoUri is { } logoUri)
        {
            Artwork = new CatalogArtworkViewModel(
                logoUri.AbsoluteUri,
                torrentContext: null,
                mediaSessionService,
                [new Uri(logoUri.GetLeftPart(UriPartial.Authority))]);
        }

        PlayCommand = new AsyncRelayCommand(() => playAsync(this));
    }

    public LiveTvChannel Channel { get; }

    public string Name => Channel.Name;

    public string? ProviderId => Channel.ProviderId;

    public string? ChannelId => Channel.ChannelId;

    public string ProviderName => Channel.ProviderName;

    public string GroupName =>
        string.IsNullOrWhiteSpace(Channel.Group)
            ? UiTextCatalog.Get("LiveTvOtherGroup")
            : Channel.Group;

    public string MetadataText => $"{ProviderName} · {GroupName}";

    public bool HasArtwork => Artwork is not null;

    public CatalogArtworkViewModel? Artwork { get; }

    public IAsyncRelayCommand PlayCommand { get; }

    public void Dispose()
    {
        Artwork?.Dispose();
        GC.SuppressFinalize(this);
    }
}
