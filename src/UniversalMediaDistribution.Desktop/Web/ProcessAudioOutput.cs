using System.Runtime.InteropServices;

namespace UniversalMediaDistribution.Desktop.Web;

internal sealed class ProcessAudioOutput : IDisposable
{
    private readonly IAudioClient _client;
    private readonly IAudioRenderClient _render;
    private readonly uint _capacity;
    private IAudioSessionVolume? _volume;
    private readonly PcmRelayBuffer _buffer = new(48000 / 5 * 4);

    private ProcessAudioOutput(IAudioClient client, IAudioRenderClient render, uint capacity)
    {
        _client = client;
        _render = render;
        _capacity = capacity;
    }

    public static ProcessAudioOutput Open(IAudioDevice device)
    {
        Marshal.ThrowExceptionForHR(device.Activate(typeof(IAudioClient).GUID, 23, 0, out var pointer));
        var client = AudioInterop.Take<IAudioClient>(pointer);
        ProcessAudioOutput? result = null;
        try
        {
            // Shared stereo PCM, with Windows resampling to the endpoint mix.
            // GUID_NULL is UMD's default session, also used by native playback.
            Marshal.ThrowExceptionForHR(client.Initialize(0, 0x88000000, 500_000, 0, AudioFormat.StereoPcm, Guid.Empty));
            Marshal.ThrowExceptionForHR(client.GetBufferSize(out var capacity));
            Marshal.ThrowExceptionForHR(client.GetService(typeof(IAudioRenderClient).GUID, out var render));
            result = new ProcessAudioOutput(client, AudioInterop.Take<IAudioRenderClient>(render), capacity);
            Marshal.ThrowExceptionForHR(client.GetService(new Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), out var session));
            var control = AudioInterop.Take<IAudioSessionControl>(session);
            try { Marshal.ThrowExceptionForHR(control.SetDisplayName("UniversalMediaDistribution", Guid.Empty)); }
            finally { AudioInterop.Release(control); }
            Marshal.ThrowExceptionForHR(client.GetService(typeof(IAudioSessionVolume).GUID, out var volume));
            result._volume = AudioInterop.Take<IAudioSessionVolume>(volume);
            Marshal.ThrowExceptionForHR(client.Start());
            return result;
        }
        catch
        {
            if (result is not null) result.Dispose();
            else AudioInterop.Release(client);
            throw;
        }
    }

    public void Add(byte[] data, int count) => _buffer.Write(data.AsSpan(0, count));

    public bool IsAudible
    {
        get
        {
            Marshal.ThrowExceptionForHR(_volume!.GetMute(out var muted));
            Marshal.ThrowExceptionForHR(_volume.GetMasterVolume(out var volume));
            return muted == 0 && volume > 0;
        }
    }

    public unsafe uint Pump()
    {
        Marshal.ThrowExceptionForHR(_client.GetCurrentPadding(out var padding));
        var frames = Math.Min(_capacity - padding, (uint)(_buffer.Count / 4));
        if (frames == 0) return 0;
        Marshal.ThrowExceptionForHR(_render.GetBuffer(frames, out var pointer));
        try { _buffer.Read(new Span<byte>((void*)pointer, checked((int)frames * 4))); }
        finally { Marshal.ThrowExceptionForHR(_render.ReleaseBuffer(frames, 0)); }
        return frames;
    }

    public void Dispose()
    {
        _client.Stop();
        AudioInterop.Release(_volume);
        AudioInterop.Release(_render);
        AudioInterop.Release(_client);
    }
}

// Single audio worker owns this bounded queue. If a device stalls, drop old
// complete stereo frames rather than accumulating an ever-growing A/V delay.
internal sealed class PcmRelayBuffer
{
    private readonly byte[] _data;
    private int _read;
    public int Count { get; private set; }

    public PcmRelayBuffer(int capacity)
    {
        if (capacity <= 0 || capacity % 4 != 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _data = new byte[capacity];
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.Length % 4 != 0) throw new ArgumentException("PCM must contain complete stereo frames.", nameof(data));
        if (data.Length > _data.Length) data = data[^_data.Length..];
        var discard = Math.Max(0, Count + data.Length - _data.Length);
        _read = (_read + discard) % _data.Length;
        Count -= discard;
        var write = (_read + Count) % _data.Length;
        var first = Math.Min(data.Length, _data.Length - write);
        data[..first].CopyTo(_data.AsSpan(write));
        data[first..].CopyTo(_data);
        Count += data.Length;
    }

    public void Read(Span<byte> destination)
    {
        if (destination.Length > Count || destination.Length % 4 != 0)
            throw new ArgumentException("Read must fit buffered complete stereo frames.", nameof(destination));
        var first = Math.Min(destination.Length, _data.Length - _read);
        _data.AsSpan(_read, first).CopyTo(destination);
        _data.AsSpan(0, destination.Length - first).CopyTo(destination[first..]);
        _read = (_read + destination.Length) % _data.Length;
        Count -= destination.Length;
    }
}
