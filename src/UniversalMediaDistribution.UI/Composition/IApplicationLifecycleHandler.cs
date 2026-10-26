namespace UniversalMediaDistribution.UI.Composition;

public interface IApplicationLifecycleHandler
{
    ValueTask EnterBackgroundAsync(CancellationToken cancellationToken = default);

    ValueTask EnterForegroundAsync(CancellationToken cancellationToken = default);
}
