namespace UniversalMediaDistribution.Mobile;

public sealed record MobileExecutionPolicy
{
    public static MobileExecutionPolicy Default { get; } = new();

    public bool PauseWhenBackgrounded { get; init; } = true;

    public bool PauseWhenOffline { get; init; } = true;

    public bool PauseOnMeteredNetwork { get; init; }

    public bool PauseInPowerSavingMode { get; init; } = true;
}

public readonly record struct MobileNetworkState(
    bool IsAvailable,
    bool IsMetered);
