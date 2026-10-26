using System.Security.Cryptography;
using UniversalMediaDistribution.Server;

namespace UniversalMediaDistribution.Server.Tests;

public sealed class ReleaseCatalogTests
{
    [Fact]
    public async Task PackageUpload_StreamsChecksAndServesSavedPackage()
    {
        using var scope = TestScope.Create();
        var package = RandomNumberGenerator.GetBytes(32 * 1024);
        var sha256 = Convert.ToHexString(SHA256.HashData(package));
        var catalog = new FilePackageCatalog(
            scope.DirectoryPath,
            maximumPackageBytes: 1024 * 1024);

        var result = await catalog.SaveAsync(
            "stable",
            "win-x64",
            "2.0.0",
            "UMD-Setup.exe",
            new MemoryStream(package, writable: false),
            package.LongLength,
            sha256,
            CancellationToken.None);

        Assert.Equal(sha256, result.Sha256);
        Assert.Equal(package.LongLength, result.SizeBytes);
        Assert.StartsWith("/api/v1/releases/", result.RelativePath);
        var saved = Assert.IsType<StoredPackage>(
            catalog.Get(
                "stable",
                "win-x64",
                "2.0.0",
                "UMD-Setup.exe"));
        Assert.Equal(package, await File.ReadAllBytesAsync(saved.Path));
    }

    [Fact]
    public async Task PackageUpload_WithWrongChecksumLeavesNoPackage()
    {
        using var scope = TestScope.Create();
        var package = RandomNumberGenerator.GetBytes(256);
        var catalog = new FilePackageCatalog(
            scope.DirectoryPath,
            maximumPackageBytes: 1024);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => catalog.SaveAsync(
                "stable",
                "win-x64",
                "2.0.0",
                "UMD-Setup.exe",
                new MemoryStream(package, writable: false),
                package.LongLength,
                new string('0', 64),
                CancellationToken.None).AsTask());

        Assert.Null(
            catalog.Get(
                "stable",
                "win-x64",
                "2.0.0",
                "UMD-Setup.exe"));
        Assert.Empty(
            Directory.EnumerateFiles(
                scope.DirectoryPath,
                "*",
                SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LatestManifest_IsValidatedAndSavedAtomically()
    {
        using var scope = TestScope.Create();
        var catalog = new SqliteReleaseCatalog(scope.DirectoryPath);
        var expected = new ReleaseManifest(
            "2.0.0",
            "stable",
            "win-x64",
            new Uri(
                "https://updates.example/api/v1/releases/stable/win-x64/2.0.0/package/UMD-Setup.exe"),
            new string('A', 64),
            1234,
            DateTimeOffset.UtcNow,
            "production-key",
            "ECDSA-P256-SHA256",
            Convert.ToBase64String(new byte[64]),
            "Release notes");

        await catalog.SaveLatestAsync(
            "stable",
            "win-x64",
            expected,
            CancellationToken.None);
        var actual = await catalog.GetLatestAsync(
            "stable",
            "win-x64",
            CancellationToken.None);

        Assert.Equal(expected, actual);
        Assert.Empty(
            Directory.EnumerateFiles(
                scope.DirectoryPath,
                "*.tmp",
                SearchOption.AllDirectories));
    }

    private sealed class TestScope : IDisposable
    {
        private TestScope(string directoryPath)
        {
            DirectoryPath = directoryPath;
        }

        public string DirectoryPath { get; }

        public static TestScope Create()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "UniversalMediaDistribution.Tests",
                Guid.CreateVersion7().ToString("N"));
            Directory.CreateDirectory(path);
            return new TestScope(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
