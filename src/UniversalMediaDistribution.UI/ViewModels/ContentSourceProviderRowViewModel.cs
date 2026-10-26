using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.UI.Localization;

namespace UniversalMediaDistribution.UI.ViewModels;

public partial class ContentSourceProviderRowViewModel : ViewModelBase
{
    public ContentSourceProviderRowViewModel(
        ContentSourceProviderConfiguration configuration,
        Func<ContentSourceProviderRowViewModel, Task>? removeAsync = null,
        ApiContentProviderScope apiScope = ApiContentProviderScope.All)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var normalized = configuration.Normalize();
        Id = normalized.Id;
        Kind = normalized.Kind;
        _displayName = normalized.Kind is ContentSourceProviderKind.Manual
            ? UiTextCatalog.Get("ManualProviderName")
            : normalized.DisplayName;
        _isEnabled = normalized.IsEnabled;
        _baseUrl = normalized.BaseUrl;
        ApiScope = apiScope;
        RemoveCommand = new AsyncRelayCommand(
            () => removeAsync?.Invoke(this) ?? Task.CompletedTask);
    }

    public string Id { get; }

    public ContentSourceProviderKind Kind { get; }

    public ApiContentProviderScope ApiScope { get; }

    public IAsyncRelayCommand RemoveCommand { get; }

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private string _baseUrl;

    public string KindLabel => Kind switch
    {
        ContentSourceProviderKind.Manual =>
            UiTextCatalog.Get("ContentSourceManual"),
        ContentSourceProviderKind.TorrentCatalog =>
            UiTextCatalog.Get("ContentSourceTorrentCatalog"),
        ContentSourceProviderKind.Api =>
            UiTextCatalog.Get("ContentSourceApi"),
        ContentSourceProviderKind.LiveTv =>
            UiTextCatalog.Get("ContentSourceLiveTv"),
        _ => UiTextCatalog.Get("Unknown")
    };

    public bool IsManual => Kind is ContentSourceProviderKind.Manual;

    public bool IsLiveTv => Kind is ContentSourceProviderKind.LiveTv;

    public bool CanEditName => Kind is ContentSourceProviderKind.TorrentCatalog;

    public bool CanEditBaseUrl =>
        !IsManual && Kind is not ContentSourceProviderKind.LiveTv;

    public bool ShowsConfigurationFields =>
        Kind is not ContentSourceProviderKind.LiveTv;

    public bool CanDelete => Kind is ContentSourceProviderKind.TorrentCatalog;

    public ContentSourceProviderConfiguration ToConfiguration() =>
        new ContentSourceProviderConfiguration
        {
            Id = Id,
            DisplayName = DisplayName,
            Kind = Kind,
            IsEnabled = IsManual || IsEnabled,
            BaseUrl = BaseUrl
        }.Normalize();
}
