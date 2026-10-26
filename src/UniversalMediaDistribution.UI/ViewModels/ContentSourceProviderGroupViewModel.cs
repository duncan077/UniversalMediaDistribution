using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.UI.Localization;

namespace UniversalMediaDistribution.UI.ViewModels;

/// <summary>
/// One kind of content source — the online APIs, the torrent catalogs, Live TV
/// — with its providers underneath it.
/// <para>
/// The settings page used to list sixteen providers as one flat run, which made
/// turning a whole category on or off a sixteen-tap exercise and made it hard to
/// tell at a glance what was even switched on. The rows here are the same
/// instances the flat list holds, so saving still reads one collection.
/// </para>
/// </summary>
public sealed partial class ContentSourceProviderGroupViewModel : ViewModelBase
{
    private readonly ContentSourceProviderKind _kind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandGlyph))]
    private bool _isExpanded = true;

    public ContentSourceProviderGroupViewModel(
        ContentSourceProviderKind kind,
        IEnumerable<ContentSourceProviderRowViewModel> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _kind = kind;
        Providers = new ObservableCollection<ContentSourceProviderRowViewModel>(
            providers);
        foreach (var provider in Providers)
        {
            provider.PropertyChanged += OnProviderChanged;
        }

        ApiCategories = new ObservableCollection<ApiContentProviderCategoryViewModel>(
            _kind is ContentSourceProviderKind.Api
                ? Providers
                    .GroupBy(static provider => NormalizeScope(provider.ApiScope))
                    .OrderBy(static group => ScopeOrder(group.Key))
                    .Select(group =>
                        new ApiContentProviderCategoryViewModel(group.Key, group))
                : []);
    }

    public ObservableCollection<ContentSourceProviderRowViewModel> Providers
    {
        get;
    }

    public ObservableCollection<ApiContentProviderCategoryViewModel>
        ApiCategories
    { get; }

    public bool HasApiCategories => ApiCategories.Count > 0;

    public bool ShowsDirectProviders => !HasApiCategories;

    /// <summary>
    /// Plural, because this names a section rather than tagging a single row.
    /// </summary>
    public string Title => _kind switch
    {
        ContentSourceProviderKind.Manual =>
            UiTextCatalog.Get("ContentSourceGroupManual"),
        ContentSourceProviderKind.TorrentCatalog =>
            UiTextCatalog.Get("ContentSourceGroupTorrentCatalog"),
        ContentSourceProviderKind.Api =>
            UiTextCatalog.Get("ContentSourceGroupApi"),
        ContentSourceProviderKind.LiveTv =>
            UiTextCatalog.Get("ContentSourceGroupLiveTv"),
        _ => UiTextCatalog.Get("Unknown")
    };

    public string ExpandGlyph => IsExpanded ? "▾" : "▸";

    public int EnabledCount =>
        Providers.Count(static provider => provider.IsEnabled);

    public string SummaryText =>
        UiTextCatalog.Format(
            "ContentSourceGroupSummary",
            EnabledCount,
            Providers.Count);

    public bool AreAllEnabled =>
        Providers.Count > 0 && EnabledCount == Providers.Count;

    public string ToggleAllText =>
        UiTextCatalog.Get(
            AreAllEnabled
                ? "ContentSourceGroupDisableAll"
                : "ContentSourceGroupEnableAll");

    /// <summary>
    /// A group of one is its own row already, so the bulk control would only be
    /// a second way to press the same switch.
    /// </summary>
    public bool CanToggleAll => Providers.Count > 1;

    [RelayCommand]
    private void ToggleAll()
    {
        // Read once: flipping the first row would otherwise change the answer
        // for the rest halfway through.
        var enable = !AreAllEnabled;
        foreach (var provider in Providers)
        {
            provider.IsEnabled = enable;
        }
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    private void OnProviderChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not
            nameof(ContentSourceProviderRowViewModel.IsEnabled))
        {
            return;
        }

        OnPropertyChanged(nameof(EnabledCount));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(AreAllEnabled));
        OnPropertyChanged(nameof(ToggleAllText));
    }

    private static ApiContentProviderScope NormalizeScope(
        ApiContentProviderScope scope) =>
        scope switch
        {
            ApiContentProviderScope.LiveAction =>
                ApiContentProviderScope.LiveAction,
            ApiContentProviderScope.Anime =>
                ApiContentProviderScope.Anime,
            ApiContentProviderScope.Manga =>
                ApiContentProviderScope.Manga,
            _ => ApiContentProviderScope.All
        };

    private static int ScopeOrder(ApiContentProviderScope scope) => scope switch
    {
        ApiContentProviderScope.LiveAction => 0,
        ApiContentProviderScope.Anime => 1,
        ApiContentProviderScope.Manga => 2,
        _ => 3
    };
}
