using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using UniversalMediaDistribution.UI.Localization;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Views;

public partial class WatchPartyPanel : UserControl
{
    public WatchPartyPanel()
    {
        InitializeComponent();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty) CopyFeedback.IsVisible = false;
        };
        DataContextChanged += (_, _) => CopyFeedback.IsVisible = false;
    }

    private async void CopyCode_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { HasWatchParty: true } model)
        {
            return;
        }

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                CopyFeedback.Text = UiTextCatalog.Get("WatchPartyCopyFailed");
            }
            else
            {
                await clipboard.SetTextAsync(model.WatchPartyCode);
                CopyFeedback.Text = UiTextCatalog.Get("WatchPartyCodeCopied");
            }
        }
        catch (Exception)
        {
            CopyFeedback.Text = UiTextCatalog.Get("WatchPartyCopyFailed");
        }

        CopyFeedback.IsVisible = true;
    }
}
