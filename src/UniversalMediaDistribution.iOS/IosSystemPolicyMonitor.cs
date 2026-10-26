using CoreFoundation;
using Foundation;
using Network;
using UniversalMediaDistribution.Mobile;

namespace UniversalMediaDistribution.iOS;

internal sealed class IosSystemPolicyMonitor(
    MobileApplicationHost mobileHost) : IDisposable
{
    private readonly MobileApplicationHost _mobileHost =
        mobileHost ?? throw new ArgumentNullException(nameof(mobileHost));
    private readonly NWPathMonitor _pathMonitor = new();
    private readonly DispatchQueue _pathQueue =
        new("org.universalmediadistribution.network-policy");
    private NSObject? _powerStateObserver;
    private bool _started;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _pathMonitor.SetQueue(_pathQueue);
        _pathMonitor.SnapshotHandler = path =>
            Observe(_mobileHost.SetNetworkStateAsync(new MobileNetworkState(
                IsAvailable: path.Status is NWPathStatus.Satisfied,
                IsMetered: path.IsExpensive)));
        _pathMonitor.Start();

        _powerStateObserver = NSProcessInfo.Notifications.ObservePowerStateDidChange(
            (_, _) => PublishPowerState());
        PublishPowerState();
        _started = true;
    }

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        _pathMonitor.Cancel();
        _powerStateObserver?.Dispose();
        _powerStateObserver = null;
        _pathMonitor.Dispose();
        _pathQueue.Dispose();
        _started = false;
    }

    private void PublishPowerState() =>
        Observe(_mobileHost.SetPowerSavingAsync(
            NSProcessInfo.ProcessInfo.LowPowerModeEnabled));

    private static void Observe(ValueTask operation)
    {
        if (!operation.IsCompletedSuccessfully)
        {
            _ = ObserveAsync(operation);
        }
    }

    private static async Task ObserveAsync(ValueTask operation)
    {
        try
        {
            await operation;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                $"iOS execution-policy update failed: {exception.Message}");
        }
    }
}
