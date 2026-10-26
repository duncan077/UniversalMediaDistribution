using CommunityToolkit.Mvvm.Input;
using UniversalMediaDistribution.UI.Localization;

namespace UniversalMediaDistribution.UI.ViewModels;

public sealed class ApiContentProviderOptionViewModel
{
    public ApiContentProviderOptionViewModel(
        string providerName,
        double titleSimilarity,
        Func<Task> selectAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(selectAsync);
        ProviderName = providerName.Trim();
        MatchText = UiTextCatalog.Format(
            "ProviderTitleMatch",
            titleSimilarity);
        SelectCommand = new AsyncRelayCommand(selectAsync);
    }

    public string ProviderName { get; }

    public string MatchText { get; }

    public IAsyncRelayCommand SelectCommand { get; }
}
