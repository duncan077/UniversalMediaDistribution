using System.Reflection;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Skia;
using UniversalMediaDistribution.Reading;
using UniversalMediaDistribution.Torrenting;
using UniversalMediaDistribution.Torrenting.Ingestion;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

[CollectionDefinition("Image memory", DisableParallelization = true)]
public sealed class ImageMemoryCollection;

[Collection("Image memory")]
public sealed class ImageMemoryTests
{
    static ImageMemoryTests() => SkiaPlatform.Initialize();

    [Theory]
    [InlineData(1000, 1500, 300, 4194304)]
    [InlineData(1000, 30000, 2400, 16777216)]
    [InlineData(200, 300, 960, 4194304)]
    [InlineData(1, 1000000, 300, 4194304)]
    public void DecodeFitsPixelBudgetWithoutUpscaling(int width, int height, int requested, long budget)
    {
        var result = BoundedImageDecoder.GetDecodeWidth(new PixelSize(width, height), requested, budget);
        Assert.InRange(result, 1, Math.Min(requested, width));
        Assert.True(result * Math.Ceiling((double)height / width * result) * 4 <= budget);
    }

    [Fact]
    public void ExtremeSourceDimensionsAreRejectedBeforeNativeDecode() =>
        Assert.Throws<InvalidDataException>(() =>
            BoundedImageDecoder.GetDecodeWidth(new PixelSize(int.MaxValue, int.MaxValue), 300, 4194304));

    [Fact]
    public void NativeDecodeHonorsBothWidthAndPixelBudget()
    {
        using var source = CreateBitmap(100, 3000);
        using var encoded = new MemoryStream();
        source.Save(encoded, PngBitmapEncoderOptions.Default);
        encoded.Position = 0;
        using var decoded = BoundedImageDecoder.Decode(encoded, 300, 128 * 1024);
        Assert.True(decoded.PixelSize.Width <= 100);
        Assert.True((long)decoded.PixelSize.Width * decoded.PixelSize.Height * 4 <= 128 * 1024);
    }

    [Fact]
    public void InvalidImageDoesNotFallBackToUnboundedDecode()
    {
        using var source = new MemoryStream([1, 2, 3, 4]);
        Assert.Throws<InvalidDataException>(() => BoundedImageDecoder.Decode(source, 300, 4194304));
    }

    [Fact]
    public void OversizedCacheEntryIsDisposedEvenWhenItIsTheOnlyEntry()
    {
        DecodedArtworkCache.Clear();
        var height = checked((int)(DecodedArtworkCache.MaximumBytes / (4096 * 4)) + 1);
        var oversized = CreateBitmap(4096, height);
        DecodedArtworkCache.Return("oversized", oversized);
        Assert.Equal(0, DecodedArtworkCache.Count);
        Assert.Equal(0, DecodedArtworkCache.ByteCount);
        Assert.Null(DecodedArtworkCache.TryTake("oversized"));
        Assert.ThrowsAny<Exception>(() => oversized.Lock());
    }

    [Fact]
    public void CacheEvictsOldestAndClearLeavesCheckedOutImagesUsable()
    {
        DecodedArtworkCache.Clear();
        try
        {
            for (var index = 0; index <= DecodedArtworkCache.Capacity; index++)
                DecodedArtworkCache.Return(index.ToString(), CreateBitmap(16, 16));
            Assert.Equal(DecodedArtworkCache.Capacity, DecodedArtworkCache.Count);
            Assert.Null(DecodedArtworkCache.TryTake("0"));
            using var checkedOut = Assert.IsType<WriteableBitmap>(DecodedArtworkCache.TryTake("1"));
            DecodedArtworkCache.Clear();
            Assert.Equal(0, DecodedArtworkCache.ByteCount);
            using var pixels = checkedOut.Lock();
            Assert.NotEqual(IntPtr.Zero, pixels.Address);
        }
        finally
        {
            DecodedArtworkCache.Clear();
        }
    }

    [Fact]
    public async Task DisposedViewModelsCannotStartNewImageLoads()
    {
        using var document = new DelayedDocument();
        var page = NewPage(document, 0);
        page.Dispose();
        await page.LoadAsync();
        Assert.Equal(0, document.OpenCount);
        var artwork = new CatalogArtworkViewModel("disposed", null, new TorrentMediaSessionService(null));
        artwork.Dispose();
        await artwork.LoadAsync();
        Assert.False(artwork.HasError);
        Assert.False(artwork.IsLoading);
    }

    [Fact]
    public async Task DetachedArtworkCancelsWhileStillQueuedAndCanReattach()
    {
        var gate = Assert.IsType<SemaphoreSlim>(typeof(CatalogArtworkViewModel)
            .GetField("LoadGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
        var slots = gate.CurrentCount;
        for (var index = 0; index < slots; index++)
            await gate.WaitAsync();
        using var artwork = new CatalogArtworkViewModel("queued", null, new TorrentMediaSessionService(null));
        try
        {
            var first = artwork.LoadAsync();
            Assert.False(first.IsCompleted);
            artwork.Release();
            var second = artwork.LoadAsync();
            Assert.NotSame(first, second);
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(artwork.IsLoading);
            artwork.Release();
            await second.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(artwork.HasError);
            Assert.False(artwork.IsLoading);
            Assert.Null(artwork.Image);
        }
        finally
        {
            artwork.Release();
            gate.Release(slots);
        }
    }

    [Fact]
    public async Task ReaderCancelsQueuedPagesBeforeOpeningTheirSource()
    {
        using var document = new DelayedDocument();
        using var first = NewPage(document, 0);
        using var second = NewPage(document, 1);
        using var queued = NewPage(document, 2);
        var firstLoad = first.LoadAsync();
        var secondLoad = second.LoadAsync();
        var queuedLoad = queued.LoadAsync();
        try
        {
            Assert.Equal(2, document.OpenCount);
            queued.Release();
            await queuedLoad.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, document.OpenCount);
            Assert.Null(queued.ErrorMessage);
        }
        finally
        {
            first.Release();
            second.Release();
            document.Complete(0);
            document.Complete(1);
            await Task.WhenAll(firstLoad, secondLoad).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task OlderReaderLoadCannotClearReattachedPagesLoadingState()
    {
        using var document = new DelayedDocument();
        using var page = NewPage(document, 0);
        var first = page.LoadAsync();
        page.Release();
        var second = page.LoadAsync();
        try
        {
            Assert.NotSame(first, second);
            document.Complete(0);
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(page.IsLoading);
            Assert.Null(page.ErrorMessage);
        }
        finally
        {
            page.Release();
            document.Complete(1);
            await second.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.False(page.IsLoading);
        Assert.Null(page.Image);
    }

    private static WriteableBitmap CreateBitmap(int width, int height) => new(
        new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

    private static ReaderPageViewModel NewPage(IPdfDocument document, int index) =>
        new(new PdfPageDescriptor(index, 100, 100), document);

    private sealed class DelayedDocument : IPdfDocument
    {
        private readonly List<TaskCompletionSource<IMediaReadHandle>> _requests = [];
        public int OpenCount => _requests.Count;
        public IReadOnlyList<PdfPageDescriptor> Pages => [];
        public ValueTask<IMediaReadHandle> OpenPageReadAsync(PdfPageDescriptor page, CancellationToken cancellationToken = default)
        {
            // Simulate a source that completes after cancellation was requested.
            var request = new TaskCompletionSource<IMediaReadHandle>(TaskCreationOptions.RunContinuationsAsynchronously);
            _requests.Add(request);
            return new ValueTask<IMediaReadHandle>(request.Task);
        }
        public void Complete(int index) => _requests[index].SetResult(new ReadHandle());
        public void Dispose() { }
    }

    private sealed class ReadHandle : IMediaReadHandle
    {
        public Stream Stream { get; } = new MemoryStream([1, 2, 3]);
        public Uri? PlaybackUri => null;
        public ValueTask DisposeAsync() => Stream.DisposeAsync();
    }
}

