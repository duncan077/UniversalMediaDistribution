using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UniversalMediaDistribution.ContentFiltering.Subscriptions;

public sealed record FilterListState
{
    public string? ETag { get; init; }

    public string? LastModified { get; init; }

    public DateTimeOffset FetchedUtc { get; init; }

    /// <summary>The list's own <c>! Expires:</c> hint, when it gave one.</summary>
    public TimeSpan? Expires { get; init; }
}

public sealed record FilterCacheManifest
{
    /// <summary>
    /// The ruleset format the cached blob was written with. A mismatch means
    /// recompile from the cached lists, not re-download.
    /// </summary>
    public int RulesetFormatVersion { get; init; }

    public Dictionary<string, FilterListState> Lists { get; init; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(FilterCacheManifest))]
internal sealed partial class FilterCacheJsonContext : JsonSerializerContext;

/// <summary>
/// Everything the filtering engine keeps on disk: the compiled ruleset, the
/// raw lists it was compiled from, and what is known about each list's
/// freshness.
///
/// Raw lists are kept gzip-compressed. A television box has a few gigabytes of
/// flash and this application also stores torrent data, so fifteen megabytes
/// of filter text is not affordable — but discarding it entirely would mean
/// re-downloading everything after a format change, which is worse. Compressed
/// they cost around a tenth of that.
///
/// Every write lands through a temporary file and a rename, so a process that
/// dies mid-write leaves the previous ruleset intact rather than a truncated
/// one.
/// </summary>
public sealed class FilterRulesetStore
{
    private const string RulesetPrefix = "ruleset-";
    private const string RulesetSuffix = ".bin";
    private const string ManifestFileName = "manifest.json";
    private const string ListsDirectoryName = "lists";

    private readonly string _directory;
    private readonly string _listsDirectory;

    public FilterRulesetStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _listsDirectory = Path.Combine(directory, ListsDirectoryName);
    }

    public string Directory => _directory;

    /// <summary>
    /// Adopts the ruleset compiled last time, preferring a memory-mapped view
    /// so the bytes stay file-backed and evictable instead of sitting on the
    /// managed heap. A plain read is the fallback wherever mapping is refused.
    ///
    /// The newest generation that actually loads wins. An older one is used
    /// only if the newest is unreadable, which beats running with no rules at
    /// all.
    /// </summary>
    public bool TryLoadRuleset(out CompiledRuleset ruleset)
    {
        ruleset = CompiledRuleset.Empty;
        foreach (var (generation, path) in Generations())
        {
            try
            {
                if (MappedRulesetBlob.TryMap(path, out var mapped) &&
                    CompiledRuleset.TryLoad(mapped, out ruleset, isMapped: true) ||
                    CompiledRuleset.TryLoad(File.ReadAllBytes(path), out ruleset))
                {
                    Prune(generation);
                    return true;
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Try the generation before it.
            }
        }

        return false;
    }

    /// <summary>
    /// Writes a new generation rather than replacing the current file.
    ///
    /// Replacing in place is impossible once a ruleset is mapped: Windows
    /// refuses to rename over a file that has an open section, whatever the
    /// share mode. A fresh name always renames cleanly, readers pick the
    /// highest complete generation, and the previous one is deleted when
    /// nothing holds it any more.
    /// </summary>
    public void SaveRuleset(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        var generation = Generations() is [var (latest, _), ..] ? latest + 1 : 1;
        WriteAtomic(RulesetPath(generation), blob);
        Prune(generation);
    }

    private string RulesetPath(int generation) =>
        Path.Combine(
            _directory,
            $"{RulesetPrefix}{generation}{RulesetSuffix}");

    /// <summary>Existing rulesets, newest first.</summary>
    private List<(int Generation, string Path)> Generations()
    {
        var result = new List<(int, string)>();
        try
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(
                _directory,
                $"{RulesetPrefix}*{RulesetSuffix}"))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (int.TryParse(
                        name.AsSpan(RulesetPrefix.Length),
                        out var generation))
                {
                    result.Add((generation, path));
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                DirectoryNotFoundException)
        {
            return result;
        }

        result.Sort(static (first, second) =>
            second.Item1.CompareTo(first.Item1));
        return result;
    }

    /// <summary>
    /// Removes superseded rulesets. A generation still mapped by this process
    /// refuses to delete on Windows; it is left alone and cleared on a later
    /// run, so the worst case is one stale file per session.
    /// </summary>
    private void Prune(int keep)
    {
        foreach (var (generation, path) in Generations())
        {
            if (generation == keep)
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Still mapped. It costs one file until the next run.
            }
        }
    }

    public FilterCacheManifest LoadManifest()
    {
        var path = Path.Combine(_directory, ManifestFileName);
        try
        {
            if (!File.Exists(path))
            {
                return new FilterCacheManifest();
            }

            return JsonSerializer.Deserialize(
                File.ReadAllText(path),
                FilterCacheJsonContext.Default.FilterCacheManifest) ??
                new FilterCacheManifest();
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                JsonException)
        {
            // A manifest we cannot read costs one extra download, which is a
            // far better outcome than refusing to start.
            return new FilterCacheManifest();
        }
    }

    public void SaveManifest(FilterCacheManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        WriteAtomic(
            Path.Combine(_directory, ManifestFileName),
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                manifest,
                FilterCacheJsonContext.Default.FilterCacheManifest)));
    }

    public string? ReadList(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var path = ListPath(id);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var file = File.OpenRead(path);
            using var decompressor =
                new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(decompressor, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                InvalidDataException)
        {
            return null;
        }
    }

    public void WriteList(string id, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(content);
        using var buffer = new MemoryStream();
        using (var compressor =
            new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            compressor.Write(bytes, 0, bytes.Length);
        }

        System.IO.Directory.CreateDirectory(_listsDirectory);
        WriteAtomic(ListPath(id), buffer.ToArray());
    }

    public void RemoveList(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        try
        {
            File.Delete(ListPath(id));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // A list we cannot delete is wasted space, not a failure.
        }
    }

    private string ListPath(string id) =>
        Path.Combine(_listsDirectory, $"{Sanitize(id)}.txt.gz");

    /// <summary>
    /// Subscription identifiers are ours, not user input, but a cache key that
    /// reaches the file system is never taken on trust.
    /// </summary>
    private static string Sanitize(string id)
    {
        var builder = new StringBuilder(id.Length);
        foreach (var value in id)
        {
            builder.Append(
                char.IsAsciiLetterOrDigit(value) || value is '-' or '_'
                    ? value
                    : '_');
        }

        return builder.ToString();
    }

    private void WriteAtomic(string path, byte[] content)
    {
        System.IO.Directory.CreateDirectory(
            Path.GetDirectoryName(path) ?? _directory);
        var temporary = $"{path}.tmp";
        File.WriteAllBytes(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }
}
