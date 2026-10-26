using System.IO.Compression;

namespace UniversalMediaDistribution.Torrenting.Ingestion;

public sealed record PreparedArchiveImagePage(
    int Position,
    int EntryIndex,
    string Path,
    long Length);

public sealed class PreparedImageArchive : IDisposable
{
    private const long MaximumArchiveBytes = 256L * 1024 * 1024;
    private const long MaximumPageBytes = 96L * 1024 * 1024;
    private const long MaximumExpandedBytes = 1024L * 1024 * 1024;
    private const int MaximumPageCount = 4096;
    private const long MaximumCompressionRatio = 200;
    private static readonly HashSet<string> SupportedImageExtensions = new(
        [".avif", ".bmp", ".gif", ".jpeg", ".jpg", ".png", ".webp"],
        StringComparer.OrdinalIgnoreCase);

    private readonly MemoryStream _archiveBuffer;
    private readonly ZipArchive _archive;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly IReadOnlyDictionary<int, ZipArchiveEntry> _entriesByIndex;
    private bool _disposed;

    private PreparedImageArchive(
        PreparedTorrentFile file,
        MemoryStream archiveBuffer,
        ZipArchive archive,
        IReadOnlyList<PreparedArchiveImagePage> pages,
        IReadOnlyDictionary<int, ZipArchiveEntry> entriesByIndex)
    {
        File = file;
        _archiveBuffer = archiveBuffer;
        _archive = archive;
        Pages = pages;
        _entriesByIndex = entriesByIndex;
    }

    public PreparedTorrentFile File { get; }

    public IReadOnlyList<PreparedArchiveImagePage> Pages { get; }

    internal static async ValueTask<PreparedImageArchive> CreateAsync(
        PreparedTorrentFile file,
        IMediaReadHandle readHandle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(readHandle);
        if (file.Length > MaximumArchiveBytes)
        {
            throw new InvalidOperationException("The CBZ archive exceeds the 256 MiB reader limit.");
        }

        var source = readHandle.Stream ??
            throw new InvalidOperationException("The torrent engine did not provide a CBZ stream.");
        var capacity = file.Length is > 0 and <= int.MaxValue ? (int)file.Length : 0;
        var buffer = new MemoryStream(capacity);
        try
        {
            await CopyBoundedAsync(source, buffer, MaximumArchiveBytes, cancellationToken);
            buffer.Position = 0;
            var archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
            try
            {
                var imageEntries = ValidateAndOrderEntries(archive);
                var pages = new List<PreparedArchiveImagePage>(imageEntries.Count);
                var entriesByIndex = new Dictionary<int, ZipArchiveEntry>(imageEntries.Count);

                // Number the archive once. IndexOf per page is a linear scan of
                // the whole entry collection, which turns opening a full-size
                // CBZ into millions of reference comparisons.
                var entryPositions = BuildEntryPositions(archive);
                for (var position = 0; position < imageEntries.Count; position++)
                {
                    var entry = imageEntries[position];
                    var entryIndex = entryPositions[entry];
                    pages.Add(new PreparedArchiveImagePage(
                        position,
                        entryIndex,
                        NormalizePath(entry.FullName),
                        entry.Length));
                    entriesByIndex.Add(entryIndex, entry);
                }

                return new PreparedImageArchive(file, buffer, archive, pages, entriesByIndex);
            }
            catch
            {
                archive.Dispose();
                throw;
            }
        }
        catch
        {
            await buffer.DisposeAsync();
            throw;
        }
    }

    public async ValueTask<IMediaReadHandle> OpenReadAsync(
        PreparedArchiveImagePage page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_entriesByIndex.TryGetValue(page.EntryIndex, out var entry) ||
            !string.Equals(NormalizePath(entry.FullName), page.Path, StringComparison.Ordinal))
        {
            throw new ArgumentException("The page does not belong to this CBZ archive.", nameof(page));
        }

        await _readGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var capacity = entry.Length is > 0 and <= int.MaxValue ? (int)entry.Length : 0;
            var destination = new MemoryStream(capacity);
            try
            {
                await using var entryStream = entry.Open();
                await CopyBoundedAsync(
                    entryStream,
                    destination,
                    MaximumPageBytes,
                    cancellationToken);
                if (destination.Length != entry.Length)
                {
                    throw new InvalidDataException(
                        $"CBZ page '{page.Path}' did not expand to its declared length.");
                }

                destination.Position = 0;
                return new ArchivePageReadHandle(destination);
            }
            catch
            {
                await destination.DisposeAsync();
                throw;
            }
        }
        finally
        {
            _readGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Same reasoning as PreparedEpubBook.Dispose: drain the in-flight page
        // read before the shared archive and buffer go away.
        _readGate.Wait();
        try
        {
            _archive.Dispose();
            _archiveBuffer.Dispose();
        }
        finally
        {
            _readGate.Release();
        }
    }

    private static Dictionary<ZipArchiveEntry, int> BuildEntryPositions(ZipArchive archive)
    {
        var positions = new Dictionary<ZipArchiveEntry, int>(archive.Entries.Count);
        for (var index = 0; index < archive.Entries.Count; index++)
        {
            positions[archive.Entries[index]] = index;
        }

        return positions;
    }

    private static IReadOnlyList<ZipArchiveEntry> ValidateAndOrderEntries(ZipArchive archive)
    {
        var entries = archive.Entries
            .Where(static entry =>
                entry.Name.Length > 0 &&
                SupportedImageExtensions.Contains(Path.GetExtension(entry.Name)))
            .ToArray();
        if (entries.Length == 0)
        {
            throw new InvalidDataException("The CBZ archive contains no supported image pages.");
        }

        if (entries.Length > MaximumPageCount)
        {
            throw new InvalidDataException($"The CBZ archive exceeds {MaximumPageCount} image pages.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;
        foreach (var entry in entries)
        {
            var path = NormalizePath(entry.FullName);
            if (!paths.Add(path))
            {
                throw new InvalidDataException($"The CBZ archive contains duplicate page path '{path}'.");
            }

            if (entry.Length <= 0 || entry.Length > MaximumPageBytes)
            {
                throw new InvalidDataException(
                    $"CBZ page '{path}' has an invalid or oversized expanded length.");
            }

            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > MaximumExpandedBytes)
            {
                throw new InvalidDataException("The CBZ archive exceeds the 1 GiB expanded-content limit.");
            }

            // A zero compressed length cannot be reasoned about, so it is
            // refused rather than skipped past. PreparedEpubBook applies the
            // same rule to the same untrusted torrent payloads.
            if (entry.CompressedLength <= 0 ||
                entry.Length / entry.CompressedLength > MaximumCompressionRatio)
            {
                throw new InvalidDataException(
                    $"CBZ page '{path}' exceeds the allowed compression ratio.");
            }
        }

        Array.Sort(entries, static (left, right) =>
            NaturalPathComparer.Instance.Compare(left.FullName, right.FullName));
        return entries;
    }

    private static string NormalizePath(string value)
    {
        var path = value.Replace('\\', '/').Trim('/');
        var segments = path.Split('/', StringSplitOptions.None);
        if (path.Length == 0 ||
            Path.IsPathRooted(value) ||
            segments.Any(static segment =>
                segment.Length == 0 || segment is "." or ".." || segment.Contains(':') ||
                segment.Any(char.IsControl)))
        {
            throw new InvalidDataException("The CBZ archive contains an unsafe page path.");
        }

        return string.Join('/', segments);
    }

    private static async ValueTask CopyBoundedAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var copyBuffer = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(copyBuffer, cancellationToken);
            if (read == 0)
            {
                return;
            }

            if (destination.Length + read > maximumBytes)
            {
                throw new InvalidDataException("The CBZ data exceeds its configured reader limit.");
            }

            await destination.WriteAsync(copyBuffer.AsMemory(0, read), cancellationToken);
        }
    }

    private sealed class ArchivePageReadHandle(Stream stream) : IMediaReadHandle
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

    private sealed class NaturalPathComparer : IComparer<string>
    {
        public static NaturalPathComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            var leftIndex = 0;
            var rightIndex = 0;
            while (leftIndex < left.Length && rightIndex < right.Length)
            {
                if (char.IsAsciiDigit(left[leftIndex]) && char.IsAsciiDigit(right[rightIndex]))
                {
                    var numeric = CompareNumber(
                        left,
                        ref leftIndex,
                        right,
                        ref rightIndex);
                    if (numeric != 0)
                    {
                        return numeric;
                    }

                    continue;
                }

                var character = char.ToUpperInvariant(left[leftIndex])
                    .CompareTo(char.ToUpperInvariant(right[rightIndex]));
                if (character != 0)
                {
                    return character;
                }

                leftIndex++;
                rightIndex++;
            }

            return (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
        }

        private static int CompareNumber(
            string left,
            ref int leftIndex,
            string right,
            ref int rightIndex)
        {
            var leftStart = leftIndex;
            var rightStart = rightIndex;
            while (leftIndex < left.Length && char.IsAsciiDigit(left[leftIndex]))
            {
                leftIndex++;
            }

            while (rightIndex < right.Length && char.IsAsciiDigit(right[rightIndex]))
            {
                rightIndex++;
            }

            var leftSignificant = leftStart;
            while (leftSignificant < leftIndex - 1 && left[leftSignificant] == '0')
            {
                leftSignificant++;
            }

            var rightSignificant = rightStart;
            while (rightSignificant < rightIndex - 1 && right[rightSignificant] == '0')
            {
                rightSignificant++;
            }

            var lengthComparison = (leftIndex - leftSignificant)
                .CompareTo(rightIndex - rightSignificant);
            if (lengthComparison != 0)
            {
                return lengthComparison;
            }

            var valueComparison = string.Compare(
                left,
                leftSignificant,
                right,
                rightSignificant,
                leftIndex - leftSignificant,
                StringComparison.Ordinal);
            return valueComparison != 0
                ? valueComparison
                : (leftIndex - leftStart).CompareTo(rightIndex - rightStart);
        }
    }
}
