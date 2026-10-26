using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UniversalMediaDistribution.Desktop.Web;

internal sealed partial class BrowserAudioSession : IDisposable
{
    private readonly IAudioSessionControl _control;
    private readonly IAudioSessionVolume _volume;
    private bool _mutedByRelay;
    public uint ProcessId { get; }
    public string DeviceId { get; }

    private BrowserAudioSession(IAudioSessionControl control, uint processId, string deviceId)
    {
        _control = control;
        _volume = (IAudioSessionVolume)control;
        ProcessId = processId;
        DeviceId = deviceId;
    }

    public static IAudioDeviceEnumerator OpenEnumerator()
    {
        Marshal.ThrowExceptionForHR(AudioInterop.CoCreateInstance(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"),
            0, 23, typeof(IAudioDeviceEnumerator).GUID, out var pointer));
        return AudioInterop.Take<IAudioDeviceEnumerator>(pointer);
    }

    public static BrowserAudioSession? Find(IAudioDeviceEnumerator enumerator, uint browserProcessId)
    {
        var descendants = GetDescendants(browserProcessId);
        Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(0, 1, out var devicesPointer));
        var devices = AudioInterop.Take<IAudioDeviceCollection>(devicesPointer);
        try
        {
            Marshal.ThrowExceptionForHR(devices.GetCount(out var count));
            for (uint i = 0; i < count; i++)
            {
                Marshal.ThrowExceptionForHR(devices.Item(i, out var devicePointer));
                var device = AudioInterop.Take<IAudioDevice>(devicePointer);
                try
                {
                    Marshal.ThrowExceptionForHR(device.Activate(typeof(IAudioSessionManager).GUID, 23, 0, out var managerPointer));
                    var manager = AudioInterop.Take<IAudioSessionManager>(managerPointer);
                    try
                    {
                        Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out var sessionsPointer));
                        var sessions = AudioInterop.Take<IAudioSessionEnumerator>(sessionsPointer);
                        try
                        {
                            Marshal.ThrowExceptionForHR(sessions.GetCount(out var sessionCount));
                            for (var j = 0; j < sessionCount; j++)
                            {
                                Marshal.ThrowExceptionForHR(sessions.GetSession(j, out var sessionPointer));
                                var control = AudioInterop.Take<IAudioSessionControl>(sessionPointer);
                                var retained = false;
                                try
                                {
                                    // S_OK only: never take ownership of a cross-process session.
                                    if (control.GetProcessId(out var pid) != 0 || !descendants.Contains(pid) ||
                                        pid == Environment.ProcessId || control.GetState(out var state) < 0 || state != 1)
                                        continue;
                                    var volume = (IAudioSessionVolume)control;
                                    Marshal.ThrowExceptionForHR(volume.GetMute(out var muted));
                                    if (muted != 0) continue; // Respect a pre-existing user mute.
                                    Marshal.ThrowExceptionForHR(device.GetId(out var deviceId));
                                    BrowserAudioSession result;
                                    try { result = new BrowserAudioSession(control, pid, Marshal.PtrToStringUni(deviceId)!); }
                                    finally { Marshal.FreeCoTaskMem(deviceId); }
                                    retained = true;
                                    return result;
                                }
                                finally { if (!retained) AudioInterop.Release(control); }
                            }
                        }
                        finally { AudioInterop.Release(sessions); }
                    }
                    finally { AudioInterop.Release(manager); }
                }
                finally { AudioInterop.Release(device); }
            }
            return null;
        }
        finally { AudioInterop.Release(devices); }
    }

    public bool IsActive => _control.GetState(out var state) >= 0 && state == 1;

    public float Volume
    {
        get
        {
            Marshal.ThrowExceptionForHR(_volume.GetMasterVolume(out var volume));
            return volume;
        }
    }

    public void Mute()
    {
        Marshal.ThrowExceptionForHR(_volume.SetMute(1, Guid.Empty));
        _mutedByRelay = true;
    }

    public void Dispose()
    {
        try
        {
            if (_mutedByRelay) _volume.SetMute(0, Guid.Empty);
        }
        finally { AudioInterop.Release(_control); }
    }

    private static unsafe HashSet<uint> GetDescendants(uint root)
    {
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entry = new ProcessEntry { Size = (uint)sizeof(ProcessEntry) };
            var parents = new Dictionary<uint, uint>();
            if (Process32FirstW(snapshot, ref entry) != 0)
                do { parents[entry.ProcessId] = entry.ParentProcessId; }
                while (Process32NextW(snapshot, ref entry) != 0);
            return SelectDescendants(root, parents);
        }
        finally { CloseHandle(snapshot); }
    }

    internal static HashSet<uint> SelectDescendants(uint root, IReadOnlyDictionary<uint, uint> parents)
    {
        if (root == 0 || root == Environment.ProcessId || !parents.ContainsKey(root)) return [];
        var result = new HashSet<uint> { root };
        bool added;
        do
        {
            added = false;
            foreach (var (pid, parent) in parents)
                if (pid != Environment.ProcessId && result.Contains(parent)) added |= result.Add(pid);
        } while (added);
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nuint DefaultHeap;
        public uint ModuleId, Threads, ParentProcessId;
        public int Priority;
        public uint Flags;
        public fixed char ExeFile[260];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [LibraryImport("kernel32.dll")]
    private static partial int Process32FirstW(nint snapshot, ref ProcessEntry entry);
    [LibraryImport("kernel32.dll")]
    private static partial int Process32NextW(nint snapshot, ref ProcessEntry entry);
    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);
}
