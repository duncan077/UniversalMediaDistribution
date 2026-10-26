using System.Runtime.InteropServices;
using UniversalMediaDistribution.Playback.LibVLC;

namespace UniversalMediaDistribution.Desktop.Tests;

public sealed class RendererPlaybackTransitionTests
{
    [Theory]
    [InlineData("living-room")]
    [InlineData(null)]
    public async Task SuccessfulSwitchKeepsRequestedDestination(string? destination)
    {
        var attempts = new List<string?>();
        var result = await RendererPlaybackTransition.RunAsync(destination, target =>
        {
            attempts.Add(target);
            return Task.CompletedTask;
        });

        Assert.Equal(new[] { destination }, attempts);
        Assert.Equal(destination, result.RendererId);
        Assert.True(result.IsPlaying);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task FailedRemoteSwitchRestoresLocalPlaybackAndPreservesError()
    {
        var failure = new IOException("Receiver unavailable");
        var attempts = new List<string?>();
        var result = await RendererPlaybackTransition.RunAsync("living-room", target =>
        {
            attempts.Add(target);
            return target is null ? Task.CompletedTask : Task.FromException(failure);
        });

        Assert.Equal(new string?[] { "living-room", null }, attempts);
        Assert.Null(result.RendererId);
        Assert.True(result.IsPlaying);
        Assert.Same(failure, result.Error);
    }

    [Fact]
    public async Task FailedRecoveryReportsBothFailuresWithoutRetryLoop()
    {
        var remoteFailure = new IOException("Receiver unavailable");
        var localFailure = new IOException("Source expired");
        var attempts = new List<string?>();
        var result = await RendererPlaybackTransition.RunAsync("living-room", target =>
        {
            attempts.Add(target);
            return Task.FromException(target is null ? localFailure : remoteFailure);
        });

        Assert.Equal(new string?[] { "living-room", null }, attempts);
        Assert.Null(result.RendererId);
        Assert.False(result.IsPlaying);
        var error = Assert.IsType<AggregateException>(result.Error);
        Assert.Equal(new Exception[] { remoteFailure, localFailure }, error.InnerExceptions);
    }

    [Fact]
    public async Task FailedLocalSwitchDoesNotRetryTheSameOutput()
    {
        var failure = new IOException("Source expired");
        var count = 0;
        var result = await RendererPlaybackTransition.RunAsync(null, _ =>
        {
            count++;
            return Task.FromException(failure);
        });

        Assert.Equal(1, count);
        Assert.False(result.IsPlaying);
        Assert.Same(failure, result.Error);
    }

    [Fact]
    public void WirelessDisplayInputMatchesWindowsAbiAndReleasesShortcutKeys()
    {
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf<WindowsWirelessDisplay.Input>());
        Assert.Equal(IntPtr.Size == 8 ? 8 : 4,
            Marshal.OffsetOf<WindowsWirelessDisplay.Input>(nameof(WindowsWirelessDisplay.Input.Data)).ToInt32());
        var inputs = WindowsWirelessDisplay.CreateShortcut();
        Assert.All(inputs, input => Assert.Equal(1u, input.Type));
        Assert.Equal(new ushort[] { 0x5B, 0x4B, 0x4B, 0x5B }, inputs.Select(input => input.Data.Keyboard.Key));
        Assert.Equal(new uint[] { 0, 0, 2, 2 }, inputs.Select(input => input.Data.Keyboard.Flags));
    }
}
