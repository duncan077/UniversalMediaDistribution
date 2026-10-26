using Avalonia.Controls;
using Avalonia.Platform;

namespace UniversalMediaDistribution.UI.Playback;

/// <summary>Optional host audio output for an embedded browser's subprocess audio.</summary>
public interface INativeWebViewAudioRelay : IAsyncDisposable
{
    void Attach(NativeWebView view, IPlatformHandle? handle);
    void Detach();
}
