using Avalonia.Threading;
using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.Android;

public sealed partial class AndroidWebChallengeSolver
{
    public async ValueTask<ApiContentVideoSource?> ResolveVideoAsync(ApiContentVideoSource source, CancellationToken cancellationToken)
    {
        await LoadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(() => AndroidCurrentActivity.Current is { } activity
                ? VideoChallengeActivity.ShowAsync(activity, source, cancellationToken)
                : Task.FromResult<ApiContentVideoSource?>(null));
        }
        finally { LoadGate.Release(); }
    }
}
