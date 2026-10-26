using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace UniversalMediaDistribution.UI.Views;

public partial class MainWindow : Window
{
    private static readonly TimeSpan GracefulShutdownTimeout =
        TimeSpan.FromSeconds(3);
    private bool _shutdownStarted;
    private bool _shutdownCompleted;

    public MainWindow()
    {
        InitializeComponent();
        if (Environment.GetEnvironmentVariable("UMD_PC_UI_PREVIEW") == "1")
        {
            // Only the explicit PC preview bypasses desktop minimums, so it
            // can exercise phone viewports without changing normal windows.
            MinWidth = 320;
            MinHeight = 240;
        }
        if (Content is MainView mainView)
        {
            // Page changes can remove the control that received KeyDown. Keep
            // the matching KeyUp routed through the window even with no child
            // focused, just as the Android host routes through its parent.
            mainView.UseParentTelevisionKeyRouting();
            AddHandler(KeyDownEvent, (_, args) => mainView.HandleTelevisionKeyDown(args),
                RoutingStrategies.Tunnel, handledEventsToo: true);
            AddHandler(KeyUpEvent, (_, args) => mainView.HandleTelevisionKeyUp(args),
                RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (_shutdownCompleted)
        {
            return;
        }

        eventArgs.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        try
        {
            if (Avalonia.Application.Current is App app)
            {
                await app.ShutdownAsync().WaitAsync(GracefulShutdownTimeout);
            }
        }
        catch (TimeoutException)
        {
            Trace.TraceError(
                $"Application UI shutdown exceeded {GracefulShutdownTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                $"Application UI shutdown failed: {exception.Message}");
        }
        finally
        {
            _shutdownCompleted = true;
            Closing -= OnClosing;
            Close();
        }
    }
}
