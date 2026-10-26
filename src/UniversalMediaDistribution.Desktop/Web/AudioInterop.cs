using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace UniversalMediaDistribution.Desktop.Web;

// WASAPI interfaces in Windows SDK vtable order. Generated COM supports the
// desktop NativeAOT publish, unlike the runtime's built-in COM marshalling.
internal static partial class AudioInterop
{
    private static readonly StrategyBasedComWrappers Wrappers = new();

    public static T Take<T>(nint pointer) where T : class
    {
        try { return (T)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance); }
        finally { Marshal.Release(pointer); }
    }

    public static void Release(object? value)
    {
        if (value is ComObject com) com.FinalRelease();
    }

    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(nint reserved, uint mode);
    [LibraryImport("ole32.dll")]
    internal static partial void CoUninitialize();
    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint result);
    [LibraryImport("Mmdevapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int ActivateAudioInterfaceAsync(string path, in Guid iid, in AudioActivationVariant parameters,
        IAudioActivationHandler handler, out nint operation);
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioActivationParameters
{
    public int Type;
    public uint ProcessId;
    public int Mode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioActivationVariant
{
    public ushort Type, Reserved1, Reserved2, Reserved3;
    public uint Size;
    public nint Data;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct AudioFormat
{
    public ushort Tag, Channels;
    public uint SamplesPerSecond, BytesPerSecond;
    public ushort BlockAlign, BitsPerSample, ExtraSize;

    public static AudioFormat StereoPcm => new()
    {
        Tag = 1,
        Channels = 2,
        SamplesPerSecond = 48000,
        BytesPerSecond = 192000,
        BlockAlign = 4,
        BitsPerSample = 16
    };
}

[GeneratedComInterface, Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
internal partial interface IAudioActivationHandler
{
    [PreserveSig] int ActivateCompleted(IAudioActivationOperation operation);
}

[GeneratedComInterface, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
internal partial interface IAudioAgileObject { }

[GeneratedComInterface, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
internal partial interface IAudioActivationOperation
{
    [PreserveSig] int GetActivateResult(out int result, out nint activated);
}

[GeneratedComInterface, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
internal partial interface IAudioClient
{
    [PreserveSig] int Initialize(int mode, uint flags, long duration, long period, in AudioFormat format, in Guid session);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint frames);
    [PreserveSig] int IsFormatSupported(int mode, nint format, out nint closest);
    [PreserveSig] int GetMixFormat(out nint format);
    [PreserveSig] int GetDevicePeriod(out long normal, out long minimum);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(nint handle);
    [PreserveSig] int GetService(in Guid iid, out nint service);
}

[GeneratedComInterface, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
internal partial interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out nint data, out uint frames, out uint flags, out ulong devicePosition, out ulong performancePosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

[GeneratedComInterface, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
internal partial interface IAudioRenderClient
{
    [PreserveSig] int GetBuffer(uint frames, out nint data);
    [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
}

[GeneratedComInterface, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IAudioDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out nint devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out nint device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out nint device);
}

[GeneratedComInterface, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
internal partial interface IAudioDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out nint device);
}

[GeneratedComInterface, Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IAudioDevice
{
    [PreserveSig] int Activate(in Guid iid, uint context, nint parameters, out nint instance);
    [PreserveSig] int OpenPropertyStore(uint access, out nint store);
    [PreserveSig] int GetId(out nint id);
}

[GeneratedComInterface, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
internal partial interface IAudioSessionManager
{
    [PreserveSig] int GetAudioSessionControl(nint id, uint flags, out nint control);
    [PreserveSig] int GetSimpleAudioVolume(nint id, uint flags, out nint volume);
    [PreserveSig] int GetSessionEnumerator(out nint sessions);
}

[GeneratedComInterface, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
internal partial interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, out nint session);
}

[GeneratedComInterface, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
internal partial interface IAudioSessionControl
{
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName(out nint name);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, in Guid context);
    [PreserveSig] int GetIconPath(out nint path);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, in Guid context);
    [PreserveSig] int GetGroupingParam(out Guid grouping);
    [PreserveSig] int SetGroupingParam(in Guid grouping, in Guid context);
    [PreserveSig] int RegisterAudioSessionNotification(nint events);
    [PreserveSig] int UnregisterAudioSessionNotification(nint events);
    [PreserveSig] int GetSessionIdentifier(out nint id);
    [PreserveSig] int GetSessionInstanceIdentifier(out nint id);
    [PreserveSig] int GetProcessId(out uint pid);
}

[GeneratedComInterface, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
internal partial interface IAudioSessionVolume
{
    [PreserveSig] int SetMasterVolume(float volume, in Guid context);
    [PreserveSig] int GetMasterVolume(out float volume);
    [PreserveSig] int SetMute(int muted, in Guid context);
    [PreserveSig] int GetMute(out int muted);
}

[GeneratedComInterface, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
internal partial interface IAudioSessionMeter
{
    [PreserveSig] int GetPeakValue(out float peak);
}
