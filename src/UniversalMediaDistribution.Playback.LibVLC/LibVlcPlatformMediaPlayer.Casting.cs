using Avalonia.Threading;
using LibVLCSharp.Shared;
using UniversalMediaDistribution.UI.Diagnostics;
using UniversalMediaDistribution.UI.Localization;

namespace UniversalMediaDistribution.Playback.LibVLC;

public sealed partial class LibVlcPlatformMediaPlayer
{
    private RendererDiscoverer? _rendererDiscovery;
    private EventHandler<RendererDiscovererItemAddedEventArgs>? _rendererAddedHandler;
    private EventHandler<RendererDiscovererItemDeletedEventArgs>? _rendererDeletedHandler;
    private readonly Dictionary<string, RendererItem> _renderers = [];
    // Keep callback-owned references through any in-flight Stop/Play. A removed
    // device may still be the target of a switch awaiting native teardown.
    private readonly List<RendererItem> _rendererReferences = [];
    private string? _rendererTarget;
    private string? _selectedRendererId;
    private bool _castSwitching;
    private int _castDiscoveryGeneration;
    private (double Position, double? Duration, bool WasPlaying)? _castRecovery;

    private void ConfigureCasting()
    {
        if (!OperatingSystem.IsWindows()) return;
        _view!.CastDiscoveryRequested += OnCastDiscoveryRequested;
        _view.CastSelectionRequested += OnCastSelectionRequested;
    }

    private async void OnCastDiscoveryRequested(object? sender, EventArgs args)
    {
        if (_disposed || _castSwitching) return;
        await _playbackGate.WaitAsync();
        try
        {
            if (_disposed || _request is null) return;
            if (_rendererDiscovery is not null)
            {
                RefreshCastDevices();
                return;
            }

            _view!.SetCastMessage(UiTextCatalog.Get("CastSearchHint"));
            var discovery = new RendererDiscoverer(_libVlc!);
            _rendererDiscovery = discovery;
            // The event sender belongs to LibVLCSharp's event manager. Capture
            // the discovery session explicitly to reject queued stale callbacks.
            _rendererAddedHandler = (_, eventArgs) => OnRendererAdded(discovery, eventArgs);
            _rendererDeletedHandler = (_, eventArgs) => OnRendererDeleted(discovery, eventArgs);
            discovery.ItemAdded += _rendererAddedHandler;
            discovery.ItemDeleted += _rendererDeletedHandler;
            if (!await Task.Run(discovery.Start))
                throw new InvalidOperationException(UiTextCatalog.Get("CastDiscoveryFailed"));
            RefreshCastDevices();
            _ = ShowEmptyDiscoveryAsync(++_castDiscoveryGeneration);
        }
        catch (Exception exception)
        {
            await StopRendererDiscoveryAsync();
            _view?.SetCastMessage(UiTextCatalog.Get("CastDiscoveryFailed"), error: true);
            ApplicationDiagnosticLog.Warning("playback.cast-discovery", exception.Message);
        }
        finally { _playbackGate.Release(); }
    }

    private async Task ShowEmptyDiscoveryAsync(int generation)
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        if (!_disposed && generation == _castDiscoveryGeneration && _rendererDiscovery is not null &&
            !_castSwitching && _renderers.Count == 0)
            _view?.SetCastMessage(UiTextCatalog.Get("CastNoDevices"));
    }

    private void OnRendererAdded(object? sender, RendererDiscovererItemAddedEventArgs args)
    {
        var item = args.RendererItem; // LibVLCSharp has already retained it.
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || !ReferenceEquals(sender, _rendererDiscovery))
            {
                item.Dispose();
                return;
            }
            var id = item.NativeReference.ToString();
            if (_renderers.ContainsKey(id)) { item.Dispose(); return; }
            _rendererReferences.Add(item);
            _renderers[id] = item;
            RefreshCastDevices();
            if (!_castSwitching) _view?.SetCastMessage(UiTextCatalog.Get("CastChooseDevice"));
        });
    }

    private void OnRendererDeleted(object? sender, RendererDiscovererItemDeletedEventArgs args)
    {
        var id = args.RendererItem.NativeReference.ToString();
        args.RendererItem.Dispose();
        Dispatcher.UIThread.Post(async () =>
        {
            if (_disposed || !ReferenceEquals(sender, _rendererDiscovery)) return;
            _renderers.Remove(id);
            RefreshCastDevices();
            if (id == _selectedRendererId && !_castSwitching)
            {
                await SwitchCastOutputAsync(null);
                _view?.SetCastMessage(UiTextCatalog.Get("CastDeviceLost"), error: true);
            }
        });
    }

    private void RefreshCastDevices()
    {
        var audio = _request?.InitialVariant.IsAudio is true;
        var devices = _renderers
            .Where(pair => audio ? pair.Value.CanRenderAudio : pair.Value.CanRenderVideo)
            .Select(pair => new CastDeviceOption(pair.Key, pair.Value.Name))
            .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .Prepend(new CastDeviceOption(null, UiTextCatalog.Get("CastLocal")))
            .ToArray();
        _view?.SetCastDevices(devices, _selectedRendererId);
    }

    private async void OnCastSelectionRequested(object? sender, string? id) =>
        await SwitchCastOutputAsync(id);

    private async Task SwitchCastOutputAsync(string? id)
    {
        if (_disposed || _castSwitching) return;
        _castSwitching = true;
        _view?.SetCastBusy(true);
        await _playbackGate.WaitAsync();
        try
        {
            if (_disposed || _request is not { } request || _selectedVariantIndex < 0) return;
            if (id is not null && !_renderers.ContainsKey(id))
            {
                _view?.SetCastMessage(UiTextCatalog.Get("CastDeviceLost"), error: true);
                return;
            }
            if (id == _selectedRendererId && !_isClosed && _mediaPlayer!.State != VLCState.Error) return;

            var index = _selectedVariantIndex;
            var variant = _variants[index];
            var player = _mediaPlayer!;
            var position = _castRecovery?.Position ?? Math.Max(0, player.Time) / 1000d;
            var duration = _castRecovery?.Duration ?? (player.Length > 0 ? player.Length / 1000d : (double?)null);
            var playing = _castRecovery?.WasPlaying ?? player.IsPlaying;
            _view?.SetCastMessage(UiTextCatalog.Get("CastConnecting"));

            var result = await RendererPlaybackTransition.RunAsync(id, async target =>
            {
                await CloseCurrentMediaAsync();
                var renderer = target is null ? null : _renderers.GetValueOrDefault(target)
                    ?? throw new InvalidOperationException(UiTextCatalog.Get("CastDeviceLost"));
                if (!await Task.Run(() => player.SetRenderer(renderer)))
                    throw new InvalidOperationException(UiTextCatalog.Get("CastConnectFailed"));
                _rendererTarget = target;
                await OpenVariantCoreAsync(request, variant, index, position, playing, CancellationToken.None);
                if (target is not null && !_renderers.ContainsKey(target))
                    throw new InvalidOperationException(UiTextCatalog.Get("CastDeviceLost"));
            });

            _selectedRendererId = result.RendererId;
            _view?.SetCastDestination(result.RendererId is { } selected
                ? _renderers.GetValueOrDefault(selected)?.Name : null);
            if (result.Error is not null)
            {
                // A failed cast does not poison the local playback monitor.
                ApplicationDiagnosticLog.Warning("playback.cast-switch", result.Error.Message);
                _view?.SetCastMessage(UiTextCatalog.Get(result.IsPlaying
                    ? "CastFailedResumedLocal" : "CastRestoreFailed"), error: true);
                if (!result.IsPlaying)
                {
                    // Keep progress and the monitor alive while the picker
                    // offers another attempt, even though native media closed.
                    _castRecovery = (position, duration, playing);
                    _view?.SetMedia(request.Title, variant.IsAudio,
                        _variants.Select(item => item.Label).ToArray(), index);
                    _view?.SuspendPlayerAccess();
                }
            }
            else _view?.SetCastMessage(UiTextCatalog.Get("CastChooseDevice"));
        }
        catch (Exception exception)
        {
            ApplicationDiagnosticLog.Warning("playback.cast-switch", exception.Message);
            _view?.SetCastMessage(UiTextCatalog.Get("CastConnectFailed"), error: true);
        }
        finally
        {
            _castSwitching = false;
            _view?.SetCastBusy(false);
            RefreshCastDevices();
            _playbackGate.Release();
        }
    }

    private async Task StopRendererDiscoveryAsync()
    {
        ++_castDiscoveryGeneration;
        var discovery = _rendererDiscovery;
        _rendererDiscovery = null;
        if (discovery is null) return;
        discovery.ItemAdded -= _rendererAddedHandler;
        discovery.ItemDeleted -= _rendererDeletedHandler;
        _rendererAddedHandler = null;
        _rendererDeletedHandler = null;
        try { await Task.Run(discovery.Stop); }
        finally { discovery.Dispose(); }
    }

    private async Task EndCastingAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (_mediaPlayer is not null) await Task.Run(() => _mediaPlayer.SetRenderer(null));
        _rendererTarget = _selectedRendererId = null;
        _castRecovery = null;
        await StopRendererDiscoveryAsync();
        _renderers.Clear();
        foreach (var item in _rendererReferences) item.Dispose();
        _rendererReferences.Clear();
        _view?.ResetCasting();
    }
}
