using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using UniversalMediaDistribution.UI.Composition;
using UniversalMediaDistribution.UI.Localization;
using UniversalMediaDistribution.UI.ViewModels;
using UniversalMediaDistribution.UI.Views;

namespace UniversalMediaDistribution.UI;

public partial class App : Avalonia.Application
{
    private MainViewModel? _mainViewModel;

    /// <summary>The active session shared with native playback surfaces.</summary>
    public MainViewModel? PlaybackViewModel => _mainViewModel;

    public Task<bool> NavigateBackAsync() =>
        _mainViewModel?.NavigateBackAsync() ??
        Task.FromResult(false);

    public Task ShutdownAsync() =>
        _mainViewModel?.ShutdownAsync() ?? Task.CompletedTask;

    public Task? ShutdownTask => _mainViewModel?.ShutdownTask;

    public bool IsLiveTvRemoteSelectionActive =>
        _mainViewModel is { IsLiveTvRemoteSelectionActive: true, IsEmbeddedMediaOpen: false };

    public bool TryActivateSelectedLiveTvChannel()
    {
        if (_mainViewModel is not
            {
                IsLiveTvPageVisible: true,
                SelectedLiveTvChannel: { } channel
            } ||
            !channel.PlayCommand.CanExecute(null))
        {
            return false;
        }

        channel.PlayCommand.Execute(null);
        return true;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        UiTextCatalog.Apply(Resources);

#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var viewModel = ApplicationComposition.CreateMainViewModel();
        _mainViewModel = viewModel;

        if (((IOptionalFeatureProvider)this).TryGetFeature<IActivatableLifetime>()
            is { } activatableLifetime)
        {
            activatableLifetime.Activated += ActivatableLifetime_OnActivated;
            activatableLifetime.Deactivated += ActivatableLifetime_OnDeactivated;
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel
            };
            // A staged update replaces the files of this install, so the update
            // helper waits for a clean exit before swapping them and relaunching.
            viewModel.RestartRequested += () =>
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => desktop.MainWindow?.Close(),
                    Avalonia.Threading.DispatcherPriority.Background);
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            activity.MainViewFactory = () =>
                viewModel.IsTelevision
                    ? new DeferredMainView(viewModel)
                    : new MainView
                    {
                        DataContext = viewModel
                    };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new MainView
            {
                DataContext = viewModel
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void ActivatableLifetime_OnActivated(
        object? sender,
        ActivatedEventArgs eventArgs)
    {
        try
        {
            if (eventArgs is ProtocolActivatedEventArgs protocol &&
                protocol.Kind is ActivationKind.OpenUri &&
                string.Equals(protocol.Uri.Scheme, "magnet", StringComparison.OrdinalIgnoreCase) &&
                _mainViewModel is not null)
            {
                await _mainViewModel.ReceiveExternalMagnetAsync(protocol.Uri.OriginalString);
            }

            if (eventArgs.Kind is ActivationKind.Background &&
                ApplicationComposition.ApplicationLifecycleHandler is { } lifecycleHandler)
            {
                await lifecycleHandler.EnterForegroundAsync();
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                $"Application activation could not be handled: {exception.Message}");
        }
    }

    private async void ActivatableLifetime_OnDeactivated(
        object? sender,
        ActivatedEventArgs eventArgs)
    {
        if (eventArgs.Kind is not ActivationKind.Background ||
            ApplicationComposition.ApplicationLifecycleHandler is not { } lifecycleHandler)
        {
            return;
        }

        try
        {
            await lifecycleHandler.EnterBackgroundAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                $"Application deactivation could not be handled: {exception.Message}");
        }
    }
}
