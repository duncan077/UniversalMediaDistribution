using UniversalMediaDistribution.Torrenting;

namespace UniversalMediaDistribution.Torrenting.MonoTorrent.Tests;

public sealed class MonoTorrentDownloadStorageTests
{
    [Fact]
    public async Task DeleteStoredData_RemovesOnlyManagedFileAndEmptyFolder()
    {
        var root = CreateTemporaryDirectory();
        var containingDirectory = Path.Combine(
            root,
            "downloads",
            "Example");
        var filePath = Path.Combine(containingDirectory, "chapter.cbz");
        Directory.CreateDirectory(containingDirectory);
        await File.WriteAllTextAsync(filePath, "content");
        await using var engine = new MonoTorrentEngine(root);
        try
        {
            await engine.DeleteStoredDataAsync(
                new TorrentSessionStorage(
                [
                    new TorrentStoredFile(
                        filePath,
                        filePath,
                        new FileInfo(filePath).Length)
                ]));

            Assert.False(File.Exists(filePath));
            Assert.False(Directory.Exists(containingDirectory));
            Assert.True(Directory.Exists(Path.Combine(root, "downloads")));
        }
        finally
        {
            await engine.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteStoredData_RejectsPathOutsideManagedDirectory()
    {
        var root = CreateTemporaryDirectory();
        var outsideDirectory = CreateTemporaryDirectory();
        var outsideFile = Path.Combine(outsideDirectory, "keep.txt");
        await File.WriteAllTextAsync(outsideFile, "keep");
        await using var engine = new MonoTorrentEngine(root);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.DeleteStoredDataAsync(
                    new TorrentSessionStorage(
                    [
                        new TorrentStoredFile(
                            outsideFile,
                            outsideFile,
                            new FileInfo(outsideFile).Length)
                    ])).AsTask());

            Assert.True(File.Exists(outsideFile));
        }
        finally
        {
            await engine.DisposeAsync();
            Directory.Delete(root, recursive: true);
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "UniversalMediaDistribution.Tests",
            Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
