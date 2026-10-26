using System.Runtime.InteropServices;

namespace UniversalMediaDistribution.Mobile;

/// <summary>
/// Maps the Android process and device profile to the release target used by
/// the self-updater. These identifiers are release-catalog keys rather than
/// .NET runtime identifiers: one APK can intentionally contain both 32-bit and
/// 64-bit ABIs from the same CPU family.
/// </summary>
public static class AndroidUpdateTarget
{
    public const string LegacyUniversal = "android";
    public const string X86AndX64 = "android-x86-x64";
    public const string Arm = "android-arm";
    public const string ArmTv = "android-tv-arm";

    public static string ResolveRuntimeIdentifier(
        bool isTelevision,
        Architecture processArchitecture) =>
        processArchitecture switch
        {
            Architecture.Arm or Architecture.Arm64 =>
                isTelevision ? ArmTv : Arm,
            Architecture.X86 or Architecture.X64 => X86AndX64,
            _ => LegacyUniversal
        };
}
