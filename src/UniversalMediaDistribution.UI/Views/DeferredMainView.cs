using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Views;

/// <summary>
/// Gives constrained Android TV devices a responsive first frame while the
/// large, compiled application view is materialized after Android has focused
/// the lightweight loading surface.
/// </summary>
internal sealed class DeferredMainView : UserControl
{
    private readonly MainViewModel _viewModel;
    private MainView? _mainView;

    internal DeferredMainView(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        Focusable = true;
        Content = CreateLoadingContent();
        AddHandler(
            KeyDownEvent,
            DeferredMainView_OnKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(
            KeyUpEvent,
            DeferredMainView_OnKeyUp,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        Loaded += DeferredMainView_OnLoaded;
    }

    private async void DeferredMainView_OnLoaded(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        Loaded -= DeferredMainView_OnLoaded;
        Focus(NavigationMethod.Directional);

        try
        {
            // Allow Android to present and focus the lightweight surface before
            // the full compiled view begins materializing.
            await Dispatcher.UIThread.InvokeAsync(
                static () => { },
                DispatcherPriority.Background);

            // Avalonia controls are dispatcher-owned. Construct the full view
            // here only after the first frame instead of moving it to a worker.
            _mainView = new MainView
            {
                DataContext = _viewModel
            };
            _mainView.UseParentTelevisionKeyRouting();
            Content = _mainView;
        }
        catch (Exception exception)
        {
            Content = CreateErrorContent(exception.Message);
        }
    }

    private void DeferredMainView_OnKeyDown(
        object? sender,
        KeyEventArgs eventArgs)
    {
        _mainView?.HandleTelevisionKeyDown(eventArgs);
    }

    private void DeferredMainView_OnKeyUp(
        object? sender,
        KeyEventArgs eventArgs)
    {
        _mainView?.HandleTelevisionKeyUp(eventArgs);
    }

    private static Control CreateLoadingContent() =>
        new Border
        {
            Background = new SolidColorBrush(Color.Parse("#101520")),
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 18,
                Children =
                {
                    new TextBlock
                    {
                        Text = "UMD",
                        Foreground =
                            new SolidColorBrush(Color.Parse("#F4F7FF")),
                        FontSize = 42,
                        FontWeight = FontWeight.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    new ProgressBar
                    {
                        IsIndeterminate = true,
                        Width = 260,
                        Height = 8
                    },
                    new TextBlock
                    {
                        Text = "Loading your library…",
                        Foreground =
                            new SolidColorBrush(Color.Parse("#B5C0D6")),
                        FontSize = 20,
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                }
            }
        };

    private static Control CreateErrorContent(string message) =>
        new Border
        {
            Padding = new Thickness(40),
            Background = new SolidColorBrush(Color.Parse("#101520")),
            Child = new TextBlock
            {
                Text = $"UMD could not open the interface.\n\n{message}",
                Foreground = new SolidColorBrush(Color.Parse("#FFB4AB")),
                FontSize = 20,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 760
            }
        };
}
