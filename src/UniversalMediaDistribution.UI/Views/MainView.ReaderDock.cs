using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using UniversalMediaDistribution.UI.Localization;

namespace UniversalMediaDistribution.UI.Views;

public partial class MainView
{
    private bool _readerFullscreenRequested;

    private void ReaderFullscreenButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _readerFullscreenRequested = !_readerFullscreenRequested;
        UpdatePlayerFullscreenPresentation();
    }

    private void UpdateReaderFullscreenLabel()
    {
        var label = UiTextCatalog.Get(_readerFullscreenRequested ? "PlayerExitFullscreen" : "PlayerEnterFullscreen");
        Avalonia.Automation.AutomationProperties.SetName(ReaderFullscreenButton, label);
        ToolTip.SetTip(ReaderFullscreenButton, label);
    }

    private bool HandleReaderDockKey(KeyEventArgs e)
    {
        if (_navigationViewModel is not
            {
                IsReaderOpen: true, IsSettingsOpen: false,
                IsDonationPromptVisible: false, IsUpdatePromptVisible: false
            }) return false;
        if (e.Key == Key.F11 || (_readerFullscreenRequested && e.Key == Key.Escape))
        {
            ReaderFullscreenButton_OnClick(this, e);
            e.Handled = true;
            return true;
        }
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        var buttons = ReaderChapterNavigationBanner.GetVisualDescendants().OfType<Button>()
            .Where(button => button.IsEffectivelyVisible && button.IsEffectivelyEnabled).ToArray();
        if (_isTelevision && e.Key == Key.Down && WithinReaderControl(focused, ReaderPages) &&
            _readerScrollViewer is { } scroll && IsReaderScrolledToEnd(scroll))
        {
            buttons.FirstOrDefault()?.Focus(NavigationMethod.Directional);
            e.Handled = true;
            return true;
        }
        if (!WithinReaderControl(focused, ReaderChapterNavigationBanner)) return false;
        if (e.Key is Key.Left or Key.Right)
        {
            var index = Array.FindIndex(buttons, button => ReferenceEquals(button, focused));
            if (buttons.Length > 0)
                buttons[(index + (e.Key == Key.Left ? -1 : 1) + buttons.Length) % buttons.Length].Focus(NavigationMethod.Directional);
        }
        else if (_isTelevision && e.Key is Key.Up or Key.Down)
        {
            if (_readerViewModel?.IsContinuous is true) ReaderPages.Focus(NavigationMethod.Directional);
            else ReaderPagedScroll.Focus(NavigationMethod.Directional);
        }
        else if (_isTelevision && e.Key is Key.Enter or Key.Space && focused is Button button)
        {
            _readerSettingsActivationKey = e.Key;
            if (button.Command?.CanExecute(button.CommandParameter) is true) button.Command.Execute(button.CommandParameter);
            else button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        else return false;
        e.Handled = true;
        return true;
    }
}
