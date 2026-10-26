using System.Runtime.InteropServices;
using UniversalMediaDistribution.Mobile;

namespace UniversalMediaDistribution.Mobile.Tests;

public sealed class AndroidUpdateTargetTests
{
    [Theory]
    [InlineData(Architecture.Arm)]
    [InlineData(Architecture.Arm64)]
    public void ArmTouchDevicesUseArmPackage(Architecture architecture)
    {
        var result = AndroidUpdateTarget.ResolveRuntimeIdentifier(
            isTelevision: false,
            architecture);

        Assert.Equal(AndroidUpdateTarget.Arm, result);
    }

    [Theory]
    [InlineData(Architecture.Arm)]
    [InlineData(Architecture.Arm64)]
    public void ArmTelevisionsUseArmTvPackage(Architecture architecture)
    {
        var result = AndroidUpdateTarget.ResolveRuntimeIdentifier(
            isTelevision: true,
            architecture);

        Assert.Equal(AndroidUpdateTarget.ArmTv, result);
    }

    [Theory]
    [InlineData(Architecture.X86, false)]
    [InlineData(Architecture.X64, false)]
    [InlineData(Architecture.X86, true)]
    [InlineData(Architecture.X64, true)]
    public void X86DevicesUseCombinedX86Package(
        Architecture architecture,
        bool isTelevision)
    {
        var result = AndroidUpdateTarget.ResolveRuntimeIdentifier(
            isTelevision,
            architecture);

        Assert.Equal(AndroidUpdateTarget.X86AndX64, result);
    }

    [Fact]
    public void UnknownArchitectureFallsBackToLegacyUniversalPackage()
    {
        var result = AndroidUpdateTarget.ResolveRuntimeIdentifier(
            isTelevision: false,
            Architecture.Wasm);

        Assert.Equal(AndroidUpdateTarget.LegacyUniversal, result);
    }
}
