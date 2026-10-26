using UniversalMediaDistribution.ContentFiltering.Subscriptions;
using UniversalMediaDistribution.UI.Localization;

namespace UniversalMediaDistribution.UI.ViewModels;

/// <summary>
/// One filtering mode as the settings screen presents it.
/// </summary>
public sealed class ContentFilterModeOptionViewModel(FilteringMode mode)
{
    public FilteringMode Mode => mode;

    public string DisplayName => UiTextCatalog.Get(mode switch
    {
        FilteringMode.Off => "ContentFilterModeOff",
        FilteringMode.Basic => "ContentFilterModeBasic",
        FilteringMode.Complete => "ContentFilterModeComplete",
        _ => "ContentFilterModeStandard"
    });

    public string Description => UiTextCatalog.Get(mode switch
    {
        FilteringMode.Off => "ContentFilterModeOffHint",
        FilteringMode.Basic => "ContentFilterModeBasicHint",
        FilteringMode.Complete => "ContentFilterModeCompleteHint",
        _ => "ContentFilterModeStandardHint"
    });

    public override string ToString() => DisplayName;
}
