namespace UniversalMediaDistribution.Mobile;

/// <summary>
/// Finishes consuming a press whose key-down changed focus. The matching
/// key-up must not activate the newly focused playback control.
/// </summary>
public sealed class NativePlayerKeyPressGuard
{
    private int? _key;

    public void SuppressUntilRelease(int key) => _key = key;

    public bool Consume(int key, bool released)
    {
        if (_key != key) return false;
        if (released) Reset();
        return true;
    }

    public void Reset() => _key = null;
}
