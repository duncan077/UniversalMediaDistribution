using Avalonia.Media.Imaging;

namespace UniversalMediaDistribution.UI.ViewModels;

/// <summary>
/// Holds recently shown artwork so a virtualized list does not decode the same
/// image again every time a row scrolls back into view. Bounded and
/// least-recently-used, so a long catalog cannot grow it without limit.
/// </summary>
internal static class DecodedArtworkCache
{
    /// <summary>
    /// Roughly two screens of poster cards plus a long chapter list of
    /// thumbnails.
    /// </summary>
    internal const int Capacity = 96;

    /// <summary>
    /// Hard decoded-pixel budget. Android uses a smaller cache to leave room
    /// for the WebView, video decoder, and GPU resources.
    /// </summary>
    internal static long MaximumBytes => ImageMemoryPolicy.ArtworkCacheBytes;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, LinkedListNode<Entry>> Entries =
        new(StringComparer.Ordinal);
    private static readonly LinkedList<Entry> Order = new();
    private static long _bytes;

    internal static string CreateKey(string reference, int decodeWidth) =>
        $"{decodeWidth}{reference}";

    internal static Bitmap? TryTake(string key)
    {
        lock (Gate)
        {
            if (!Entries.Remove(key, out var node))
            {
                return null;
            }

            Order.Remove(node);
            _bytes -= node.Value.Bytes;
            return node.Value.Image;
        }
    }

    /// <summary>
    /// Hands an image back after its view detached. The caller must not use the
    /// image afterwards: the cache owns it and may dispose it on eviction.
    /// </summary>
    internal static void Return(string key, Bitmap image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var bytes = EstimateBytes(image);
        List<Bitmap>? evicted = null;
        lock (Gate)
        {
            if (Entries.Remove(key, out var existing))
            {
                Order.Remove(existing);
                _bytes -= existing.Value.Bytes;

                // Two holders of the same decoded image can both hand it back
                // under the same key. Disposing here would then destroy the
                // very image about to be cached, and the next TryTake would
                // return it dead.
                if (!ReferenceEquals(existing.Value.Image, image))
                {
                    existing.Value.Image.Dispose();
                }
            }

            var node = Order.AddFirst(new Entry(key, image, bytes));
            Entries[key] = node;
            _bytes += bytes;

            // The byte budget is a hard limit, including a single large image.
            while (Entries.Count > 0 &&
                   (Entries.Count > Capacity || _bytes > MaximumBytes))
            {
                var last = Order.Last!;
                Order.RemoveLast();
                Entries.Remove(last.Value.Key);
                _bytes -= last.Value.Bytes;
                (evicted ??= []).Add(last.Value.Image);
            }
        }

        if (evicted is null)
        {
            return;
        }

        foreach (var evictedImage in evicted)
        {
            evictedImage.Dispose();
        }
    }

    internal static void Clear()
    {
        Bitmap[] images;
        lock (Gate)
        {
            images = Order.Select(static entry => entry.Image).ToArray();
            Order.Clear();
            Entries.Clear();
            _bytes = 0;
        }

        foreach (var image in images)
        {
            image.Dispose();
        }
    }

    internal static int Count
    {
        get
        {
            lock (Gate)
            {
                return Entries.Count;
            }
        }
    }

    internal static long ByteCount
    {
        get
        {
            lock (Gate)
            {
                return _bytes;
            }
        }
    }

    /// <summary>
    /// Decoded footprint, which is pixels rather than the encoded file size.
    /// </summary>
    private static long EstimateBytes(Bitmap image)
    {
        var size = image.PixelSize;
        return size.Width <= 0 || size.Height <= 0
            ? 0
            : (long)size.Width * size.Height * 4;
    }

    private readonly record struct Entry(string Key, Bitmap Image, long Bytes);
}
