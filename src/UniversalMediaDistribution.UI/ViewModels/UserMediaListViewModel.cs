using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.UI.Localization;

namespace UniversalMediaDistribution.UI.ViewModels;

public sealed class UserMediaListViewModel(UserMediaList list)
{
    public UserMediaList List { get; } =
        list ?? throw new ArgumentNullException(nameof(list));

    public Guid Id => List.Id;

    public string Name => List.Name;

    public string NameKey => UserMediaList.GetNameKey(List.Name);

    public int ItemCount => List.ItemCount;

    public string DisplayLabel => UiTextCatalog.Format(
        "UserListDisplayLabel",
        Name,
        ItemCount);

    public bool Contains(Guid catalogItemId) =>
        List.Contains(catalogItemId);
}
