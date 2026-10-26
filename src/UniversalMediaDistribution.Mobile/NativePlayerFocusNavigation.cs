namespace UniversalMediaDistribution.Mobile;

public sealed record NativePlayerFocusTarget(int Id, bool IsVisible, bool IsEnabled);
public sealed record NativePlayerFocusLink(int Id, int UpId, int DownId);

/// <summary>Deterministic D-pad order inside a player panel, excluding unavailable actions.</summary>
public static class NativePlayerFocusNavigation
{
    public static IReadOnlyList<NativePlayerFocusLink> Build(IEnumerable<NativePlayerFocusTarget> targets)
    {
        var available = targets.Where(t => t.IsVisible && t.IsEnabled).Select(t => t.Id).ToArray();
        return available.Select((id, index) => new NativePlayerFocusLink(
            id, available[Math.Max(0, index - 1)], available[Math.Min(available.Length - 1, index + 1)]))
            .ToArray();
    }
}
