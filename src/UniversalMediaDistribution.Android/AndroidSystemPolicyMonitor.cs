using Android.Content;
using Android.Net;
using Android.OS;
using UniversalMediaDistribution.Mobile;

namespace UniversalMediaDistribution.Android;

internal sealed class AndroidSystemPolicyMonitor(
    Context applicationContext,
    MobileApplicationHost mobileHost) : BroadcastReceiver
{
    private readonly Context _applicationContext =
        applicationContext ?? throw new ArgumentNullException(nameof(applicationContext));
    private readonly MobileApplicationHost _mobileHost =
        mobileHost ?? throw new ArgumentNullException(nameof(mobileHost));
    private bool _registered;

    public void Start()
    {
        if (_registered)
        {
            return;
        }

        var filter = new IntentFilter();
#pragma warning disable CA1422
        filter.AddAction(ConnectivityManager.ConnectivityAction);
#pragma warning restore CA1422
        filter.AddAction(PowerManager.ActionPowerSaveModeChanged);

        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            _applicationContext.RegisterReceiver(this, filter, ReceiverFlags.NotExported);
        }
        else
        {
#pragma warning disable CA1422
            _applicationContext.RegisterReceiver(this, filter);
#pragma warning restore CA1422
        }

        _registered = true;
        PublishCurrentState();
    }

    public void Stop()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            _applicationContext.UnregisterReceiver(this);
        }
        catch (Java.Lang.IllegalArgumentException)
        {
            // Android already detached the process-level receiver.
        }

        _registered = false;
    }

    public override void OnReceive(Context? context, Intent? intent) =>
        PublishCurrentState();

    private void PublishCurrentState()
    {
        var connectivityManager =
            _applicationContext.GetSystemService(Context.ConnectivityService)
                as ConnectivityManager;
        var activeNetwork = connectivityManager?.ActiveNetwork;
        var capabilities = activeNetwork is null
            ? null
            : connectivityManager?.GetNetworkCapabilities(activeNetwork);
        var networkState = new MobileNetworkState(
            IsAvailable:
                capabilities?.HasCapability(NetCapability.Internet) is true &&
                capabilities.HasCapability(NetCapability.Validated),
            IsMetered: connectivityManager?.IsActiveNetworkMetered is true);

        var powerManager =
            _applicationContext.GetSystemService(Context.PowerService) as PowerManager;

        Observe(_mobileHost.SetNetworkStateAsync(networkState));
        Observe(_mobileHost.SetPowerSavingAsync(powerManager?.IsPowerSaveMode is true));
    }

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
                $"Android execution-policy update failed: {exception.Message}");
        }
    }
}
