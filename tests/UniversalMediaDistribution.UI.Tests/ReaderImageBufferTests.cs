using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class ReaderImageBufferTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(4194304)]
    public async Task LargePagesUseTemporaryStorageEvenWithoutADeclaredLength(long declaredLength)
    {
        var bytes = new byte[4194304];
        new Random(42).NextBytes(bytes);
        using var source = new MemoryStream(bytes);
        var result = await ReaderPageViewModel.BufferAsync(source, declaredLength, CancellationToken.None);
        var file = Assert.IsType<FileStream>(result);
        var path = file.Name;
        try
        {
            Assert.Equal(0, result.Position);
            Assert.Equal(bytes.Length, result.Length);
            // Decoders seek back to the start after reading the image header.
            result.Position = bytes.Length - 31;
            var tail = new byte[31];
            await result.ReadExactlyAsync(tail);
            Assert.Equal(bytes[^31..], tail);
            result.Position = 0;
            Assert.Equal(bytes[0], result.ReadByte());
        }
        finally
        {
            await result.DisposeAsync();
        }
        Assert.False(File.Exists(path));
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task OversizedDeclaredPageIsRejectedBeforeReading()
    {
        using var source = new MemoryStream(new byte[1]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ReaderPageViewModel.BufferAsync(source, ImageMemoryPolicy.EncodedReaderBytes + 1, CancellationToken.None));
        Assert.Equal(0, source.Position);
    }
}
