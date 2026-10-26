namespace UniversalMediaDistribution.Playback.LibVLC;

/// <summary>
/// Tracks the two independent conditions a native-player popup needs:
/// the view must be attached and a media session must still be active.
/// Avalonia popups are separate top-level windows, so parent visibility
/// alone cannot be used to close them.
/// </summary>
internal sealed class PlayerOverlayLifecycle
{
    public bool IsAttached { get; private set; }

    public bool HasActiveMedia { get; private set; }

    public bool ShouldShowOverlay => IsAttached && HasActiveMedia;

    public void Attach() => IsAttached = true;

    public void Detach() => IsAttached = false;

    public void ActivateMedia() => HasActiveMedia = true;

    public void DeactivateMedia() => HasActiveMedia = false;
}
