using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace UniversalMediaDistribution.UI.ViewModels;

internal static class CatalogArtworkCache
{
    private const string CacheDirectoryName = "artwork-cache";

    /// <summary>
    /// A backstop against an unbounded number of tiny files, not the real
    /// budget — <see cref="MaximumCacheBytes"/> is. It has to stay well above
    /// one entry per catalogued item: artwork averages well under a hundred
    /// kilobytes, so a count set near the catalog size evicts posters that are
    /// still on screen while the byte budget is barely touched. A poster
    /// dropped here cannot always be re-fetched, because artwork on an origin
    /// the provider has not discovered yet is not trusted, so the eviction
    /// shows up as a thumbnail that loads once and then disappears.
    /// </summary>
    private const int MaximumCacheFileCount = 4096;

    private const long MaximumCacheBytes = 256L * 1024 * 1024;
    private static int _storesSinceCleanup;

    public static async ValueTask<Bitmap?> TryLoadAsync(
        Uri source,
        long maximumBytes,
        int decodeWidth,
        CancellationToken cancellationToken)
    {
        var path = TryGetCachePath(source);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 32 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length <= 0 || stream.Length > maximumBytes)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            // Cached bytes are the original download, so they are scaled on the
            // way in exactly like a fresh one.
            return await Task.Run(
                () => CatalogArtworkViewModel.DecodeAtWidth(stream, decodeWidth),
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public static async ValueTask StoreAsync(
        Uri source,
        Stream content,
        CancellationToken cancellationToken)
    {
        var path = TryGetCachePath(source);
        if (path is null || content.Length == 0)
        {
            return;
        }

        var temporaryPath =
            $"{path}.{Guid.CreateVersion7():N}.tmp";
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 32 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                content.Position = 0;
                await content.CopyToAsync(output, 32 * 1024, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
            if ((Interlocked.Increment(ref _storesSinceCleanup) & 31) == 0)
            {
                await Task.Run(
                    () => Trim(directory),
                    CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Artwork remains usable in memory when persistent caching is
            // unavailable on the current platform.
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // A stale temporary cache file is harmless.
            }
        }
    }

    private static void Trim(string directory)
    {
        try
        {
            // StoreAsync deletes its own temporary file, but a crash or an
            // Android task-kill between the write and the move leaves one
            // behind. Those never match the "*.image" sweep below, so without
            // this they sit outside the cache budget forever, at up to the
            // full size of an artwork download each.
            var staleBefore = DateTime.UtcNow - TimeSpan.FromHours(1);
            foreach (var orphan in new DirectoryInfo(directory)
                         .EnumerateFiles("*.tmp", SearchOption.TopDirectoryOnly))
            {
                if (orphan.LastWriteTimeUtc >= staleBefore)
                {
                    continue;
                }

                try
                {
                    orphan.Delete();
                }
                catch
                {
                    // A write may still be in flight on another thread.
                }
            }

            var files = new DirectoryInfo(directory)
                .EnumerateFiles("*.image", SearchOption.TopDirectoryOnly)
                .OrderByDescending(static file => file.LastWriteTimeUtc)
                .ToArray();
            long retainedBytes = 0;
            for (var index = 0; index < files.Length; index++)
            {
                var file = files[index];
                if (index < MaximumCacheFileCount &&
                    retainedBytes + file.Length <= MaximumCacheBytes)
                {
                    retainedBytes += file.Length;
                    continue;
                }

                try
                {
                    file.Delete();
                }
                catch
                {
                    // Another artwork request may currently be reading it.
                }
            }
        }
        catch
        {
            // Cache cleanup must never make artwork loading fail.
        }
    }

    private static string? TryGetCachePath(Uri source)
    {
        if (!source.IsAbsoluteUri ||
            source.Scheme is not ("http" or "https") ||
            source.UserInfo.Length > 0)
        {
            return null;
        }

        try
        {
            var applicationData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(applicationData))
            {
                return null;
            }

            var hash = Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(source.AbsoluteUri)));
            return Path.Combine(
                applicationData,
                "UniversalMediaDistribution",
                CacheDirectoryName,
                $"{hash}.image");
        }
        catch
        {
            return null;
        }
    }
}
