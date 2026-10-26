using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class ArtworkDownloadBufferTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(262144)]
    [InlineData(262145)]
    [InlineData(4194304)]
    public async Task PreservesContentAndSpillsOnlyLargeImages(int length)
    {
        var bytes = new byte[length];
        new Random(42).NextBytes(bytes);
        using var source = new MemoryStream(bytes);
        var result = await ArtworkDownloadBuffer.CopyAsync(source, length, CancellationToken.None);
        var path = (result as FileStream)?.Name;
        try
        {
            Assert.Equal(0, result.Position);
            Assert.Equal(length, result.Length);
            Assert.Equal(length > ArtworkDownloadBuffer.MemoryThreshold, result is FileStream);
            using var copy = new MemoryStream();
            await result.CopyToAsync(copy);
            Assert.Equal(bytes, copy.ToArray());
        }
        finally { await result.DisposeAsync(); }
        if (path is not null) Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task RejectsOversizedInputEvenWithoutAnAdvertisedLength()
    {
        using var source = new MemoryStream(new byte[ArtworkDownloadBuffer.MemoryThreshold * 2]);
        await Assert.ThrowsAsync<InvalidDataException>(() => ArtworkDownloadBuffer.CopyAsync(
            source, ArtworkDownloadBuffer.MemoryThreshold + 1, CancellationToken.None));
    }

    [Fact]
    public async Task CanceledReadDoesNotReturnPartialImage()
    {
        using var source = new MemoryStream(new byte[10]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ArtworkDownloadBuffer.CopyAsync(source, 10, cancellation.Token));
    }
}
