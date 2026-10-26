using System.Buffers;
using System.IO.MemoryMappedFiles;

namespace UniversalMediaDistribution.ContentFiltering.Subscriptions;

/// <summary>
/// Exposes a compiled ruleset file as memory without copying it onto the
/// managed heap.
///
/// On a device with two gigabytes of memory this is the difference between ten
/// megabytes of anonymous memory the low-memory killer counts against the
/// application, and ten megabytes of file-backed pages the kernel can evict and
/// re-read at will. Nothing in a ruleset is ever written, so evicting a page
/// costs a read, not a swap.
///
/// **The mapping is deliberately never unmapped.** A match runs against spans
/// pointing straight into this memory while the engine may already have swapped
/// in a newer ruleset, so releasing the old view on a timer, a finalizer, or a
/// reference count would eventually free memory a request is still reading —
/// which is not an exception, it is a corrupted read or a crash. Acquiring the
/// pointer without ever releasing it pins the handle open for the life of the
/// process. The cost is bounded and cheap: a retired ruleset is a few megabytes
/// of evictable file-backed address space, and refreshes happen a couple of
/// times a session at most.
/// </summary>
internal sealed unsafe class MappedRulesetBlob : MemoryManager<byte>
{
    private readonly byte* _pointer;
    private readonly int _length;

    private MappedRulesetBlob(byte* pointer, int length)
    {
        _pointer = pointer;
        _length = length;
    }

    public static bool TryMap(string path, out ReadOnlyMemory<byte> blob)
    {
        blob = default;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > int.MaxValue)
            {
                return false;
            }

            // FileShare.Delete is what lets the next refresh replace this file
            // while the mapping is still open. Without it, Windows refuses the
            // rename and a ruleset update would fail for the rest of the run.
            var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            var file = MemoryMappedFile.CreateFromFile(
                stream,
                mapName: null,
                capacity: 0,
                MemoryMappedFileAccess.Read,
                HandleInheritability.None,
                leaveOpen: false);
            var accessor = file.CreateViewAccessor(
                0,
                info.Length,
                MemoryMappedFileAccess.Read);

            byte* pointer = null;
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            if (pointer is null)
            {
                return false;
            }

            blob = new MappedRulesetBlob(
                pointer + accessor.PointerOffset,
                (int)info.Length).Memory;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                ArgumentException)
        {
            // A platform or file system that will not map. The caller reads the
            // file the ordinary way instead.
            return false;
        }
    }

    public override Span<byte> GetSpan() => new(_pointer, _length);

    public override MemoryHandle Pin(int elementIndex = 0) =>
        (uint)elementIndex > (uint)_length
            ? throw new ArgumentOutOfRangeException(nameof(elementIndex))
            : new MemoryHandle(_pointer + elementIndex);

    public override void Unpin()
    {
        // The pages are pinned for the life of the process by construction.
    }

    protected override void Dispose(bool disposing)
    {
        // See the class summary: the view outlives every reader on purpose.
    }
}
