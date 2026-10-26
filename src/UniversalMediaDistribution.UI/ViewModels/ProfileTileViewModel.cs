using CommunityToolkit.Mvvm.ComponentModel;
using UniversalMediaDistribution.Application.UserState;

namespace UniversalMediaDistribution.UI.ViewModels;

/// <summary>
/// One profile as the picker shows it.
/// </summary>
public sealed partial class ProfileTileViewModel(UserProfile profile)
    : ViewModelBase
{
    /// <summary>
    /// The tints tiles are drawn in, so a profile is recognisable before its
    /// name is read. Assigned by position rather than stored, so a profile
    /// created on one device looks the same on another without the colour
    /// having to travel.
    /// </summary>
    private static readonly string[] AccentKeys =
        ["indigo", "mint", "amber", "rose", "violet"];

    [ObservableProperty]
    private bool _isActive;

    public UserProfile Profile { get; private set; } = profile;

    public Guid Id => Profile.Id;

    public string Name => Profile.Name;

    /// <summary>
    /// The first letter, for the tile. A name that begins with something that
    /// has no uppercase form — an ideograph, an emoji — is shown as it is.
    /// </summary>
    public string Initial =>
        string.IsNullOrWhiteSpace(Name)
            ? "?"
            : char.ConvertFromUtf32(char.ConvertToUtf32(Name.Trim(), 0))
                .ToUpper(UiTextCatalogCulture);

    public string AccentKey =>
        Profile.AccentKey ??
        AccentKeys[Math.Abs(Profile.Id.GetHashCode()) % AccentKeys.Length];

    private static System.Globalization.CultureInfo UiTextCatalogCulture =>
        Localization.UiTextCatalog.CurrentCulture;

    public void Update(UserProfile profile, bool isActive)
    {
        Profile = profile;
        IsActive = isActive;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Initial));
        OnPropertyChanged(nameof(AccentKey));
    }
}
