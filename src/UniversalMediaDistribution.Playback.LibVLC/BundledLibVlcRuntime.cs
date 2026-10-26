using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UniversalMediaDistribution.Playback.LibVLC;

internal static partial class BundledLibVlcRuntime
{
    internal static void Configure()
    {
        if (!OperatingSystem.IsLinux()) return;
        var pluginDirectory = Path.Combine(AppContext.BaseDirectory, "libvlc", "plugins");
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "libvlc.so"))) return;
        if (!Directory.Exists(pluginDirectory))
            throw new InvalidOperationException("The bundled LibVLC plugins are missing. Reinstall the complete Linux package.");
        // libvlc.so is resolved beside the application; its relative RUNPATH
        // resolves the bundled dependency closure without LD_LIBRARY_PATH.
        Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", pluginDirectory);
        // On Unix, .NET's environment changes are not reflected in libc's
        // environ. VLC reads getenv(), so set its native value as well.
        if (SetEnvironmentVariable("VLC_PLUGIN_PATH", pluginDirectory, 1) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int SetEnvironmentVariable(string name, string value, int overwrite);
}
