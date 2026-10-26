using System.Buffers;

namespace UniversalMediaDistribution.UI.ViewModels;

internal static class ArtworkDownloadBuffer
{
    internal const int MemoryThreshold = 256 * 1024;
    private const int CopyBufferSize = 32 * 1024;

    internal static async Task<Stream> CopyAsync(
        Stream source, long maximumBytes, CancellationToken cancellationToken)
    {
        // Most posters stay small. Larger originals spill to a seekable file
        // instead of growing and copying multi-megabyte managed arrays.
        Stream result = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (result.Length + read > maximumBytes)
                    throw new InvalidDataException("Artwork exceeds its download size limit.");

                if (result is MemoryStream && result.Length + read > MemoryThreshold)
                {
                    var file = new FileStream(
                        Path.Combine(Path.GetTempPath(), $"umd-artwork-{Guid.NewGuid():N}.tmp"),
                        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                        CopyBufferSize, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                    try
                    {
                        result.Position = 0;
                        await result.CopyToAsync(file, CopyBufferSize, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        await file.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                    await result.DisposeAsync().ConfigureAwait(false);
                    result = file;
                }

                await result.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            result.Position = 0;
            return result;
        }
        catch
        {
            await result.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
