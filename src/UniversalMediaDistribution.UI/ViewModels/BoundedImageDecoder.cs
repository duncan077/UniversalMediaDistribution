using System.Buffers;
using Avalonia;
using Avalonia.Media.Imaging;

namespace UniversalMediaDistribution.UI.ViewModels;

internal static class BoundedImageDecoder
{
    internal static Bitmap Decode(Stream stream, int requestedWidth, long maximumBytes)
    {
        // All callers buffer downloads or open a seekable disk cache. Inspect
        // dimensions before asking a native decoder to allocate pixel storage.
        var origin = stream.Position;
        var header = ArrayPool<byte>.Shared.Rent(ImageDimensionReader.MaximumHeaderBytes);
        PixelSize? size;
        try
        {
            var read = stream.ReadAtLeast(header.AsSpan(0, ImageDimensionReader.MaximumHeaderBytes),
                ImageDimensionReader.MaximumHeaderBytes, throwOnEndOfStream: false);
            size = ImageDimensionReader.TryRead(header.AsSpan(0, read));
        }
        finally
        {
            stream.Position = origin;
            ArrayPool<byte>.Shared.Return(header);
        }

        if (size is not { } dimensions)
            throw new InvalidDataException("Image dimensions could not be read safely.");

        var width = GetDecodeWidth(dimensions, requestedWidth, maximumBytes);
        // Never retry a failed scaled decode at full resolution: that can turn
        // a recoverable image error into an out-of-memory process crash.
        var bitmap = Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.MediumQuality);
        if ((long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4 > maximumBytes)
        {
            bitmap.Dispose();
            throw new InvalidDataException("The decoded image exceeds its memory budget.");
        }
        return bitmap;
    }

    internal static int GetDecodeWidth(PixelSize size, int requestedWidth, long maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedWidth);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 4);
        if (size.Width <= 0 || size.Height <= 0)
            throw new InvalidDataException("Invalid image dimensions.");

        // Some codecs need source-sized temporary storage even for a scaled
        // decode. Reject extreme inputs before invoking native code.
        if ((long)size.Width * size.Height > 32L * 1024 * 1024)
            throw new InvalidDataException("The source image has too many pixels.");

        var ratio = (double)size.Height / size.Width;
        var width = Math.Min(size.Width, requestedWidth);
        width = Math.Min(width, (int)Math.Min(int.MaxValue, Math.Sqrt(maximumBytes / (4 * ratio))));
        while (width > 0 && width * Math.Ceiling(width * ratio) * 4 > maximumBytes)
            width--;
        if (width < 1)
            throw new InvalidDataException("The image cannot fit its memory budget.");
        return width;
    }
}
