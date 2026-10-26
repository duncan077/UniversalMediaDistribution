using UniversalMediaDistribution.Torrenting;

namespace UniversalMediaDistribution.Reading;

public sealed record PdfDocumentSource
{
    public PdfDocumentSource(
        Uri? playbackUri,
        long length,
        Func<CancellationToken, ValueTask<IMediaReadHandle>> openReadAsync)
    {
        ArgumentNullException.ThrowIfNull(openReadAsync);
        PlaybackUri = playbackUri;
        Length = length;
        OpenReadAsync = openReadAsync;
    }

    public Uri? PlaybackUri { get; }

    public long Length { get; }

    public Func<CancellationToken, ValueTask<IMediaReadHandle>> OpenReadAsync { get; }
}

public sealed record PdfPageDescriptor(
    int Position,
    int PixelWidth,
    int PixelHeight);

public interface IPdfDocument : IDisposable
{
    IReadOnlyList<PdfPageDescriptor> Pages { get; }

    ValueTask<IMediaReadHandle> OpenPageReadAsync(
        PdfPageDescriptor page,
        CancellationToken cancellationToken = default);
}

public interface IPdfRenderer
{
    bool IsAvailable { get; }

    ValueTask<IPdfDocument> OpenAsync(
        PdfDocumentSource source,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailablePdfRenderer : IPdfRenderer
{
    public bool IsAvailable => false;

    public ValueTask<IPdfDocument> OpenAsync(
        PdfDocumentSource source,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<IPdfDocument>(
            new NotSupportedException("This host has no PDF renderer configured."));
}
