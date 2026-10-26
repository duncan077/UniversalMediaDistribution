using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalMediaDistribution.UI.Localization;

namespace UniversalMediaDistribution.UI.ViewModels;

/// <summary>
/// One shelf on the home page: a heading, a preview of the cards filed under
/// it, and a way to reach the rest on the section's own page.
/// </summary>
public sealed class CatalogSectionViewModel : ObservableObject
{
    private int _totalCount;

    private bool _leadsBeyondTheShelf;

    /// <param name="leadsBeyondTheShelf">
    /// Whether the destination holds more than the shelf can be counted to
    /// hold. A genre row opens a search that reaches the providers, so it is
    /// worth offering even when every local match is already on the row.
    /// </param>
    public CatalogSectionViewModel(
        string title,
        IEnumerable<CatalogCardViewModel> items,
        int totalCount,
        IRelayCommand? seeAllCommand,
        bool leadsBeyondTheShelf = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(items);
        Title = title;
        Items = new ObservableCollection<CatalogCardViewModel>(items);
        _totalCount = Math.Max(totalCount, Items.Count);
        SeeAllCommand = seeAllCommand;
        _leadsBeyondTheShelf = leadsBeyondTheShelf;
    }

    public string Title { get; private set; }

    public ObservableCollection<CatalogCardViewModel> Items { get; }

    public IRelayCommand? SeeAllCommand { get; private set; }

    public string CountText =>
        UiTextCatalog.Format("HomeSectionCount", _totalCount);

    /// <summary>
    /// Offered when there is a destination and something there the shelf is
    /// not already showing — either titles it trimmed, or, for a genre row,
    /// whatever the providers hold that this library does not.
    /// </summary>
    public bool CanSeeAll =>
        SeeAllCommand is not null &&
        (_leadsBeyondTheShelf || _totalCount > Items.Count);

    public void Update(
        string title,
        IReadOnlyList<CatalogCardViewModel> items,
        int totalCount,
        IRelayCommand? seeAllCommand,
        bool leadsBeyondTheShelf = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(items);
        _leadsBeyondTheShelf = leadsBeyondTheShelf;
        if (!string.Equals(Title, title, StringComparison.Ordinal))
        {
            Title = title;
            OnPropertyChanged(nameof(Title));
        }

        if (!ReferenceEquals(SeeAllCommand, seeAllCommand))
        {
            SeeAllCommand = seeAllCommand;
            OnPropertyChanged(nameof(SeeAllCommand));
        }

        ObservableCollectionSynchronizer.Synchronize(Items, items);
        var normalizedCount = Math.Max(totalCount, Items.Count);
        if (_totalCount != normalizedCount)
        {
            _totalCount = normalizedCount;
            OnPropertyChanged(nameof(CountText));
        }

        OnPropertyChanged(nameof(CanSeeAll));
    }
}
