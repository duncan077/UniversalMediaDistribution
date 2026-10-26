using PDFtoImage;
using UniversalMediaDistribution.Torrenting;

namespace UniversalMediaDistribution.Reading.Pdfium;

public sealed class PdfiumPdfRenderer : IPdfRenderer
{
    private const long MaximumPdfBytes = 256L * 1024 * 1024;
    private const long MaximumRenderedPngBytes = 64L * 1024 * 1024;
    private const int MaximumPageCount = 4096;
    private const int MaximumRenderedWidth = 1600;
    private const int MaximumRenderedHeight = 4096;

    public bool IsAvailable =>
        OperatingSystem.IsWindows() ||
        OperatingSystem.IsLinux() ||
        OperatingSystem.IsMacOS();

    public async ValueTask<IPdfDocument> OpenAsync(
        PdfDocumentSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        EnsureSupportedPlatform();
        if (source.Length is <= 0 or > MaximumPdfBytes)
        {
            throw new InvalidOperationException(
                "The PDF is empty or exceeds the 256 MiB reader limit.");
        }

        await using var readHandle = await source.OpenReadAsync(cancellationToken);
        var stream = readHandle.Stream ??
            throw new InvalidOperationException("The torrent engine did not provide a PDF stream.");
        var content = await ReadExactAsync(stream, source.Length, cancellationToken);
        ValidateHeader(content);

        try
        {
            var pageCount = GetPageCount(content);
            if (pageCount is <= 0 or > MaximumPageCount)
            {
                throw new InvalidDataException(
                    $"The PDF must contain between 1 and {MaximumPageCount} pages.");
            }

            var pageSizes = GetPageSizes(content);
            if (pageSizes.Length != pageCount)
            {
                throw new InvalidDataException(
                    "The PDF page count does not match its page-size table.");
            }

            var pages = pageSizes
                .Select(static (size, position) =>
                    CreatePage(position, size.Width, size.Height))
                .ToArray();
            return new PdfiumDocument(content, pages);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidDataException(
                "The PDF could not be opened. It may be corrupt, password-protected, or unsupported.",
                exception);
        }
    }

    private static int GetPageCount(byte[] content)
    {
        if (OperatingSystem.IsWindows() ||
            OperatingSystem.IsLinux() ||
            OperatingSystem.IsMacOS())
        {
            return Conversion.GetPageCount(content);
        }

        throw new PlatformNotSupportedException();
    }

    private static System.Drawing.SizeF[] GetPageSizes(byte[] content)
    {
        if (OperatingSystem.IsWindows() ||
            OperatingSystem.IsLinux() ||
            OperatingSystem.IsMacOS())
        {
            return Conversion.GetPageSizes(content).ToArray();
        }

        throw new PlatformNotSupportedException();
    }

    private static void RenderPage(
        Stream destination,
        byte[] content,
        PdfPageDescriptor page,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows() ||
            OperatingSystem.IsLinux() ||
            OperatingSystem.IsMacOS())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Conversion.SavePng(
                destination,
                content,
                page.Position,
                password: null,
                new RenderOptions
                {
                    Width = page.PixelWidth,
                    Height = page.PixelHeight,
                    WithAspectRatio = false,
                    WithAnnotations = true,
                    WithFormFill = true,
                    UseTiling = true
                });
            return;
        }

        throw new PlatformNotSupportedException();
    }

    private static PdfPageDescriptor CreatePage(int position, float width, float height)
    {
        if (!float.IsFinite(width) ||
            !float.IsFinite(height) ||
            width <= 0 ||
            height <= 0)
        {
            throw new InvalidDataException(
                $"PDF page {position + 1} has invalid dimensions.");
        }

        var scale = Math.Min(
            MaximumRenderedWidth / (double)width,
            MaximumRenderedHeight / (double)height);
        var pixelWidth = Math.Clamp(
            (int)Math.Round(width * scale),
            1,
            MaximumRenderedWidth);
        var pixelHeight = Math.Clamp(
            (int)Math.Round(height * scale),
            1,
            MaximumRenderedHeight);
        return new PdfPageDescriptor(position, pixelWidth, pixelHeight);
    }

    private static void ValidateHeader(ReadOnlySpan<byte> content)
    {
        if (content.Length < 8 ||
            !content.StartsWith("%PDF-"u8) ||
            !char.IsAsciiDigit((char)content[5]) ||
            content[6] != (byte)'.' ||
            !char.IsAsciiDigit((char)content[7]))
        {
            throw new InvalidDataException("The selected file does not have a valid PDF header.");
        }
    }

    private static async ValueTask<byte[]> ReadExactAsync(
        Stream source,
        long declaredLength,
        CancellationToken cancellationToken)
    {
        var content = GC.AllocateUninitializedArray<byte>((int)declaredLength);
        var offset = 0;
        while (offset < content.Length)
        {
            var read = await source.ReadAsync(content.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                throw new InvalidDataException(
                    "The PDF stream ended before its declared torrent length.");
            }

            offset += read;
        }

        var extra = new byte[1];
        if (await source.ReadAsync(extra, cancellationToken) != 0)
        {
            throw new InvalidDataException(
                "The PDF stream exceeds its declared torrent length.");
        }

        return content;
    }

    private static void EnsureSupportedPlatform()
    {
        if (!OperatingSystem.IsWindows() &&
            !OperatingSystem.IsLinux() &&
            !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "The PDFium renderer is available only on supported native desktop platforms.");
        }
    }

    private sealed class PdfiumDocument(
        byte[] content,
        IReadOnlyList<PdfPageDescriptor> pages) : IPdfDocument
    {
        private readonly SemaphoreSlim _renderGate = new(1, 1);
        private bool _disposed;

        public IReadOnlyList<PdfPageDescriptor> Pages { get; } = pages;

        public async ValueTask<IMediaReadHandle> OpenPageReadAsync(
            PdfPageDescriptor page,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(page);
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (page.Position < 0 ||
                page.Position >= Pages.Count ||
                !Equals(Pages[page.Position], page))
            {
                throw new ArgumentException(
                    "The page does not belong to this PDF document.",
                    nameof(page));
            }

            await _renderGate.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var destination = new MemoryStream();
                try
                {
                    await Task.Run(
                        () => RenderPage(destination, content, page, cancellationToken),
                        cancellationToken);
                    if (destination.Length > MaximumRenderedPngBytes)
                    {
                        throw new InvalidDataException(
                            "The rendered PDF page exceeds the 64 MiB image limit.");
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    destination.Position = 0;
                    return new PdfPageReadHandle(destination);
                }
                catch
                {
                    await destination.DisposeAsync();
                    throw;
                }
            }
            finally
            {
                _renderGate.Release();
            }
        }

        public void Dispose()
        {
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    private sealed class PdfPageReadHandle(Stream stream) : IMediaReadHandle
    {
        public Stream? Stream { get; } = stream;

        public Uri? PlaybackUri => null;

        public async ValueTask DisposeAsync()
        {
            if (Stream is not null)
            {
                await Stream.DisposeAsync();
            }
        }
    }
}
