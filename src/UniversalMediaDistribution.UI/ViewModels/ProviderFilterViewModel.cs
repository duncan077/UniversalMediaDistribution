using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.UI.Localization;

namespace UniversalMediaDistribution.UI.ViewModels;

public sealed class ProviderFilterViewModel
{
    public const string LocalProviderKey = "content-source:local";
    public const string OnlineProviderKey = "content-source:online";

    public ProviderFilterViewModel(CatalogContributor? contributor)
        : this(
            contributor?.StableKey,
            contributor?.Name ?? UiTextCatalog.Get("AllProviders"),
            contributor is null
                ? ContentProviderFilterKind.All
                : ContentProviderFilterKind.Named,
            contributor)
    {
    }

    private ProviderFilterViewModel(
        string? providerKey,
        string displayName,
        ContentProviderFilterKind kind,
        CatalogContributor? contributor = null)
    {
        Contributor = contributor;
        ContributorKey = providerKey;
        Kind = kind;
        DisplayName = displayName;
        Label = displayName;
    }

    public static ProviderFilterViewModel Local() =>
        new(
            LocalProviderKey,
            UiTextCatalog.Get("LocalProvider"),
            ContentProviderFilterKind.Local);

    public static ProviderFilterViewModel Online() =>
        new(
            OnlineProviderKey,
            UiTextCatalog.Get("OnlineProvider"),
            ContentProviderFilterKind.Online);

    public static ProviderFilterViewModel Named(string displayName) =>
        new(
            CreateNamedProviderKey(displayName),
            displayName.Trim(),
            ContentProviderFilterKind.Named);

    public static string CreateNamedProviderKey(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        return "organization:" +
               NameNormalizer.Normalize(displayName);
    }

    public CatalogContributor? Contributor { get; }

    public string? ContributorKey { get; }

    public ContentProviderFilterKind Kind { get; }

    public string DisplayName { get; }

    public string Label { get; }

    public bool IsAllProviders =>
        Kind is ContentProviderFilterKind.All;
}

public enum ContentProviderFilterKind
{
    All = 0,
    Local = 1,
    Online = 2,
    Named = 3
}
