using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalMediaDistribution.UI.Localization;
using UniversalMediaDistribution.UI.Remote;

namespace UniversalMediaDistribution.UI.ViewModels;

/// <summary>
/// One accepted friend, with the switch deciding whether they see this
/// account's progress.
/// </summary>
public sealed partial class FriendViewModel : ViewModelBase
{
    private readonly Func<FriendViewModel, bool, Task> _setSharingAsync;
    private readonly Func<FriendViewModel, Task> _removeAsync;
    private bool _isApplyingRemoteState;

    public FriendViewModel(
        RemoteFriend friend,
        Func<FriendViewModel, bool, Task> setSharingAsync,
        Func<FriendViewModel, Task> removeAsync)
    {
        ArgumentNullException.ThrowIfNull(friend);
        _setSharingAsync = setSharingAsync ??
            throw new ArgumentNullException(nameof(setSharingAsync));
        _removeAsync = removeAsync ??
            throw new ArgumentNullException(nameof(removeAsync));
        AccountId = friend.AccountId;
        Username = friend.Username;
        DisplayName = friend.DisplayName;
        Since = friend.Since;
        _sharesProgress = friend.SharesProgress;
        FreshnessLabel = DescribeFreshness(friend.ProgressUpdatedAt);
    }

    /// <summary>
    /// When this friend's progress was last published.
    /// <para>
    /// Said out loud because it is only as recent as their last sync. Without
    /// it a friend who has not synced looks like a friend who has not read
    /// anything, and the two are worth telling apart.
    /// </para>
    /// </summary>
    public string FreshnessLabel { get; }

    private static string DescribeFreshness(DateTimeOffset? updatedAt)
    {
        if (updatedAt is not { } published)
        {
            return UiTextCatalog.Get("FriendProgressNeverShared");
        }

        var age = DateTimeOffset.UtcNow - published;
        if (age < TimeSpan.FromMinutes(2))
        {
            return UiTextCatalog.Get("FriendProgressJustNow");
        }

        if (age < TimeSpan.FromHours(1))
        {
            return UiTextCatalog.Format(
                "FriendProgressMinutesAgo",
                (int)age.TotalMinutes);
        }

        return age < TimeSpan.FromDays(1)
            ? UiTextCatalog.Format(
                "FriendProgressHoursAgo",
                (int)age.TotalHours)
            : UiTextCatalog.Format(
                "FriendProgressDaysAgo",
                (int)age.TotalDays);
    }

    public Guid AccountId { get; }

    public string Username { get; }

    public string DisplayName { get; }

    public DateTimeOffset Since { get; }

    public string UsernameLabel => $"@{Username}";

    /// <summary>
    /// Titles this account and the friend have both made progress in, as the
    /// same poster cards the catalog uses so they can be opened from here.
    /// </summary>
    public ObservableCollection<CatalogCardViewModel> SharedTitles
    {
        get;
    } = [];

    public bool HasSharedTitles => SharedTitles.Count > 0;

    public string SharedTitlesLabel => FavoritesInCommon > 0
        ? UiTextCatalog.Format(
            "FriendCommonWithFavorites",
            SharedTitles.Count,
            FavoritesInCommon)
        : UiTextCatalog.Format("FriendSharedTitlesCount", SharedTitles.Count);

    /// <summary>
    /// How many of the titles in common are marked a favourite on both sides.
    /// Named separately because "we both like this" is a stronger thing to
    /// have in common than "we have both opened it".
    /// </summary>
    public int FavoritesInCommon { get; private set; }

    /// <summary>
    /// What the friend has and this account does not, as lines rather than
    /// poster cards: this device has no copy of these titles, so there is no
    /// card to show and nothing to open.
    /// </summary>
    public ObservableCollection<FriendTitleViewModel> TitlesOnlyTheirs
    {
        get;
    } = [];

    public bool HasTitlesOnlyTheirs => TitlesOnlyTheirs.Count > 0;

    public string TitlesOnlyTheirsLabel => UiTextCatalog.Format(
        "FriendTitlesOnlyTheirs",
        TitlesOnlyTheirs.Count);

    /// <summary>
    /// What this account has and the friend does not — cards, because these
    /// are titles this device holds and can open.
    /// </summary>
    public ObservableCollection<CatalogCardViewModel> TitlesOnlyMine
    {
        get;
    } = [];

    public bool HasTitlesOnlyMine => TitlesOnlyMine.Count > 0;

    public string TitlesOnlyMineLabel => UiTextCatalog.Format(
        "FriendTitlesOnlyMine",
        TitlesOnlyMine.Count);

    /// <summary>
    /// Replaces the whole comparison. A collection change is not a property
    /// change, so what depends on the counts is republished here.
    /// </summary>
    internal void SetComparison(
        IEnumerable<CatalogCardViewModel> inCommon,
        int favoritesInCommon,
        IEnumerable<FriendTitleViewModel> onlyTheirs,
        IEnumerable<CatalogCardViewModel> onlyMine)
    {
        FavoritesInCommon = favoritesInCommon;
        Replace(SharedTitles, inCommon);
        Replace(TitlesOnlyTheirs, onlyTheirs);
        Replace(TitlesOnlyMine, onlyMine);
        OnPropertyChanged(nameof(HasSharedTitles));
        OnPropertyChanged(nameof(SharedTitlesLabel));
        OnPropertyChanged(nameof(FavoritesInCommon));
        OnPropertyChanged(nameof(HasTitlesOnlyTheirs));
        OnPropertyChanged(nameof(TitlesOnlyTheirsLabel));
        OnPropertyChanged(nameof(HasTitlesOnlyMine));
        OnPropertyChanged(nameof(TitlesOnlyMineLabel));

        static void Replace<T>(
            ObservableCollection<T> target,
            IEnumerable<T> items)
        {
            target.Clear();
            foreach (var item in items)
            {
                target.Add(item);
            }
        }
    }

    /// <summary>
    /// Whether this friend sees this account's progress. Writing it sends the
    /// change; a failed send puts the switch back rather than leaving it
    /// showing something the server never accepted.
    /// </summary>
    [ObservableProperty]
    private bool _sharesProgress;

    partial void OnSharesProgressChanged(bool value)
    {
        if (_isApplyingRemoteState)
        {
            return;
        }

        _ = _setSharingAsync(this, value);
    }

    /// <summary>
    /// Puts the switch where the server says it is, without sending it back.
    /// </summary>
    internal void RevertSharing(bool value)
    {
        _isApplyingRemoteState = true;
        try
        {
            SharesProgress = value;
        }
        finally
        {
            _isApplyingRemoteState = false;
        }
    }

    [RelayCommand]
    private Task RemoveAsync() => _removeAsync(this);
}

/// <summary>
/// One title in a friend comparison that this device does not hold, so it can
/// only be named and described rather than opened.
/// </summary>
public sealed partial class FriendTitleViewModel : ViewModelBase
{
    private readonly Func<FriendTitleViewModel, Task>? _searchAsync;

    public FriendTitleViewModel(
        RemoteFriendComparisonTitle title,
        Func<FriendTitleViewModel, Task>? searchAsync = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        _searchAsync = searchAsync;
        Title = CleanTitle(title.Title);
        IsFavorite = title.FriendFavorite;
        Detail = BuildDetail(title);
        MediaType = ReadMediaType(title.ItemKey);
        Subtype = title.Subtype;
    }

    /// <summary>
    /// Which part of the catalog the title belongs to, taken from the key's
    /// own prefix — so looking for it starts where it would live.
    /// </summary>
    public string MediaType { get; }

    public string? Subtype { get; }

    public bool CanSearch => _searchAsync is not null;

    /// <summary>
    /// Goes looking for the title. These are the ones this device does not
    /// hold, so the only useful thing a click can do is start the search that
    /// would find them.
    /// </summary>
    [RelayCommand]
    private Task SearchAsync() =>
        _searchAsync?.Invoke(this) ?? Task.CompletedTask;

    private static string ReadMediaType(string itemKey)
    {
        var separator = itemKey.IndexOf(':');
        return separator > 0
            ? itemKey[..separator]
            : string.Empty;
    }

    /// <summary>
    /// A friend whose device has not published since titles began travelling
    /// leaves the server nothing to name a title with but its key, which is
    /// the media type, a colon and the name in upper case. Showing that raw
    /// is worse than showing the name it contains.
    /// </summary>
    private static string CleanTitle(string title)
    {
        var separator = title.IndexOf(':');
        return separator >= 0 && separator < title.Length - 1
            ? title[(separator + 1)..]
            : title;
    }

    public string Title { get; }

    public bool IsFavorite { get; }

    /// <summary>
    /// How far they reached, or that they only marked it — one string,
    /// because the word order differs by language.
    /// </summary>
    public string Detail { get; }

    private static string BuildDetail(RemoteFriendComparisonTitle title)
    {
        if (!title.FriendSeen)
        {
            return UiTextCatalog.Get("FriendTitleFavoriteOnly");
        }

        var unit = title.DisplayNumber?.Trim();
        if (string.IsNullOrEmpty(unit))
        {
            return UiTextCatalog.Get(
                title.IsCompleted
                    ? "FriendTitleFinished"
                    : "FriendTitleStarted");
        }

        if (string.Equals(
                title.UnitKind,
                "episode",
                StringComparison.OrdinalIgnoreCase))
        {
            return title.SeasonNumber is { } season
                ? UiTextCatalog.Format(
                    "FriendTitleEpisodeInSeason",
                    season,
                    unit)
                : UiTextCatalog.Format("FriendTitleEpisode", unit);
        }

        return UiTextCatalog.Format("FriendTitleChapter", unit);
    }
}

/// <summary>
/// How far one friend has reached in the title being looked at, phrased for
/// the item page.
/// </summary>
public sealed class FriendProgressViewModel
{
    public FriendProgressViewModel(RemoteFriendProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        DisplayName = progress.DisplayName;
        UpdatedAt = progress.UpdatedAt;
        Summary = BuildSummary(progress);
    }

    public string DisplayName { get; }

    public DateTimeOffset UpdatedAt { get; }

    /// <summary>
    /// The whole line, already naming the friend — "Ana read up to chapter
    /// 75". One string rather than parts, because the word order between the
    /// name and the place differs by language.
    /// </summary>
    public string Summary { get; }

    private static string BuildSummary(RemoteFriendProgress progress)
    {
        var unit = progress.DisplayNumber?.Trim();

        // No unit means the progress is the whole title — a film, say — and
        // there IsCompleted really does mean finished.
        if (string.IsNullOrEmpty(unit))
        {
            return UiTextCatalog.Format(
                progress.IsCompleted
                    ? "FriendProgressFinished"
                    : "FriendProgressStarted",
                progress.DisplayName);
        }

        // With a unit it must not be read that way. The reader sets
        // IsCompleted on reaching the last page of the chapter in hand, so
        // someone who finished chapter one carries it, and reporting that as
        // having finished the manga is how this first went wrong. How far they
        // reached is the honest answer and needs no flag.
        if (string.Equals(
                progress.UnitKind,
                "episode",
                StringComparison.OrdinalIgnoreCase))
        {
            return progress.SeasonNumber is { } season
                ? UiTextCatalog.Format(
                    "FriendProgressEpisodeInSeason",
                    progress.DisplayName,
                    season,
                    unit)
                : UiTextCatalog.Format(
                    "FriendProgressEpisode",
                    progress.DisplayName,
                    unit);
        }

        return UiTextCatalog.Format(
            "FriendProgressChapter",
            progress.DisplayName,
            unit);
    }
}

/// <summary>
/// A friendship that has been asked for and not yet answered, in either
/// direction.
/// </summary>
public sealed partial class FriendRequestViewModel : ViewModelBase
{
    private readonly Func<FriendRequestViewModel, Task> _acceptAsync;
    private readonly Func<FriendRequestViewModel, Task> _declineAsync;

    public FriendRequestViewModel(
        RemoteFriendRequest request,
        Func<FriendRequestViewModel, Task> acceptAsync,
        Func<FriendRequestViewModel, Task> declineAsync)
    {
        ArgumentNullException.ThrowIfNull(request);
        _acceptAsync = acceptAsync ??
            throw new ArgumentNullException(nameof(acceptAsync));
        _declineAsync = declineAsync ??
            throw new ArgumentNullException(nameof(declineAsync));
        AccountId = request.AccountId;
        Username = request.Username;
        DisplayName = request.DisplayName;
        IsIncoming = request.IsIncoming;
        CreatedAt = request.CreatedAt;
    }

    public Guid AccountId { get; }

    public string Username { get; }

    public string DisplayName { get; }

    public bool IsIncoming { get; }

    public DateTimeOffset CreatedAt { get; }

    public string UsernameLabel => $"@{Username}";

    /// <summary>
    /// Only an incoming request can be accepted; an outgoing one is waiting on
    /// the other person.
    /// </summary>
    public bool CanAccept => IsIncoming;

    public string StatusLabel => UiTextCatalog.Get(
        IsIncoming
            ? "FriendRequestIncoming"
            : "FriendRequestOutgoing");

    /// <summary>
    /// Declining an incoming request and withdrawing an outgoing one are the
    /// same operation, so the button says which one it is.
    /// </summary>
    public string DismissLabel => UiTextCatalog.Get(
        IsIncoming
            ? "FriendRequestDecline"
            : "FriendRequestWithdraw");

    [RelayCommand]
    private Task AcceptAsync() => _acceptAsync(this);

    [RelayCommand]
    private Task DismissAsync() => _declineAsync(this);
}
