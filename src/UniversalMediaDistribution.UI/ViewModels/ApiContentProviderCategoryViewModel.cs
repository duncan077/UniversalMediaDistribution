using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.UI.Localization;

namespace UniversalMediaDistribution.UI.ViewModels;

/// <summary>
/// One media category inside the API-provider section in Settings.
/// </summary>
public sealed partial class ApiContentProviderCategoryViewModel : ViewModelBase
{
    private readonly ApiContentProviderScope _scope;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandGlyph))]
    private bool _isExpanded = true;

    public ApiContentProviderCategoryViewModel(
        ApiContentProviderScope scope,
        IEnumerable<ContentSourceProviderRowViewModel> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _scope = scope;
        Providers = new ObservableCollection<ContentSourceProviderRowViewModel>(
            providers);
        foreach (var provider in Providers)
        {
            provider.PropertyChanged += OnProviderChanged;
        }
    }

    public ObservableCollection<ContentSourceProviderRowViewModel> Providers
    {
        get;
    }

    public string Title => _scope switch
    {
        ApiContentProviderScope.LiveAction =>
            UiTextCatalog.Get("ApiProviderCategoryMoviesSeries"),
        ApiContentProviderScope.Anime =>
            UiTextCatalog.Get("ApiProviderCategoryAnime"),
        ApiContentProviderScope.Manga =>
            UiTextCatalog.Get("ApiProviderCategoryBooksManga"),
        _ => UiTextCatalog.Get("ApiProviderCategoryAllMedia")
    };

    public string ExpandGlyph => IsExpanded ? "▾" : "▸";

    public int EnabledCount =>
        Providers.Count(static provider => provider.IsEnabled);

    public string SummaryText => UiTextCatalog.Format(
        "ContentSourceGroupSummary",
        EnabledCount,
        Providers.Count);

    public bool AreAllEnabled =>
        Providers.Count > 0 && EnabledCount == Providers.Count;

    public string ToggleAllText => UiTextCatalog.Get(
        AreAllEnabled
            ? "ContentSourceGroupDisableAll"
            : "ContentSourceGroupEnableAll");

    public bool CanToggleAll => Providers.Count > 1;

    [RelayCommand]
    private void ToggleAll()
    {
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
}
