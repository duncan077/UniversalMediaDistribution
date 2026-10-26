using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UniversalMediaDistribution.Playback.LibVLC;

internal static partial class WindowsWirelessDisplay
{
    // Use Windows' documented Win+K shortcut; the OS owns Miracast discovery
    // and connection. INPUT's union must include MOUSEINPUT for correct ABI size.
    internal static Input[] CreateShortcut() =>
        [Key(0x5B), Key(0x4B), Key(0x4B, true), Key(0x5B, true)];

    public static unsafe void Open()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var inputs = CreateShortcut();
        fixed (Input* pointer = inputs)
        {
            if (SendInput((uint)inputs.Length, pointer, sizeof(Input)) == inputs.Length) return;
            var error = Marshal.GetLastPInvokeError();
            // A partial insertion must not leave the Windows key held down.
            _ = SendInput(2, pointer + 2, sizeof(Input));
            throw new Win32Exception(error);
        }
    }

    private static Input Key(ushort key, bool up = false) => new()
    {
        Type = 1,
        Data = new InputData { Keyboard = new KeyboardInput { Key = key, Flags = up ? 2u : 0u } }
    };

    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial uint SendInput(uint count, Input* inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Type;
        public InputData Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputData
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort Key;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }
}
