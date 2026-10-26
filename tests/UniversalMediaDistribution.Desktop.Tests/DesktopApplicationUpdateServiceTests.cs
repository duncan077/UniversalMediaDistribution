using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.UI.Updates;

namespace UniversalMediaDistribution.Desktop.Tests;

public sealed class DesktopApplicationUpdateServiceTests
{
    private static readonly string ExecutableName =
        OperatingSystem.IsWindows()
            ? "UniversalMediaDistribution.Desktop.exe"
            : "UniversalMediaDistribution.Desktop";

    [Fact]
    public async Task CheckThenExplicitUpdate_StagesThePayloadAndAsksForRestart()
    {
        var scope = TestScope.Create();
        try
        {
            var package = CreateReleaseArchive(
                [(ExecutableName, "new build"), ("libvlc.dll", "runtime")]);
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var manifest = CreateManifest(signingKey, package, "2.0.0");
            var installDirectory = scope.CreateInstallDirectory();
            var configuredInstallDirectory =
                installDirectory + Path.DirectorySeparatorChar;
            DesktopUpdateHandoff? handoff = null;
            var service = new DesktopApplicationUpdateService(
                CreateOptions(
                    scope.DirectoryPath,
                    signingKey,
                    "1.0.0",
                    configuredInstallDirectory),
                remoteServerSettingsStore: null,
                new HttpClient(new UpdateHandler(manifest, package)),
                value =>
                {
                    handoff = value;
                    return true;
                });

            var update = Assert.IsType<ApplicationUpdateInfo>(
                await service.CheckForUpdateAsync());

            Assert.Equal("2.0.0", update.Version);
            Assert.Null(handoff);

            var result = await service.DownloadAndApplyAsync(update);

            Assert.Equal(
                ApplicationUpdateOutcome.RestartRequired,
                result.Outcome);
            var staged = Assert.IsType<DesktopUpdateHandoff>(handoff);
            Assert.Equal(installDirectory, staged.InstallDirectory);
            Assert.False(staged.RequiresElevation);
            Assert.Equal(
                "new build",
                await File.ReadAllTextAsync(
                    Path.Combine(staged.PayloadDirectory, ExecutableName)));
            Assert.True(File.Exists(staged.ScriptPath));
            // Nothing may touch the running install before the app exits.
            Assert.Equal(
                "old build",
                await File.ReadAllTextAsync(
                    Path.Combine(installDirectory, ExecutableName)));
            var script = await File.ReadAllTextAsync(staged.ScriptPath);
            Assert.Contains(
                Environment.ProcessId.ToString(),
                script,
                StringComparison.Ordinal);
            Assert.Contains(
                staged.PayloadDirectory,
                script,
                StringComparison.Ordinal);
            if (OperatingSystem.IsWindows())
            {
                Assert.DoesNotContain(
                    $"robocopy \"{configuredInstallDirectory}\"",
                    script,
                    StringComparison.Ordinal);
                Assert.Contains(
                    $"robocopy \"{installDirectory}\"",
                    script,
                    StringComparison.Ordinal);
            }
        }
        finally
        {
            scope.Dispose();
        }
    }

    [Fact]
    public async Task ArchiveWrappedInAVersionFolder_IsUnwrapped()
    {
        var scope = TestScope.Create();
        try
        {
            var package = CreateReleaseArchive(
            [
                ($"umd-2.0.0-win-x64/{ExecutableName}", "new build"),
                ("umd-2.0.0-win-x64/app.dll", "library")
            ]);
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var manifest = CreateManifest(signingKey, package, "2.0.0");
            DesktopUpdateHandoff? handoff = null;
            var service = new DesktopApplicationUpdateService(
                CreateOptions(
                    scope.DirectoryPath,
                    signingKey,
                    "1.0.0",
                    scope.CreateInstallDirectory()),
                remoteServerSettingsStore: null,
                new HttpClient(new UpdateHandler(manifest, package)),
                value =>
                {
                    handoff = value;
                    return true;
                });
            var update = Assert.IsType<ApplicationUpdateInfo>(
                await service.CheckForUpdateAsync());

            await service.DownloadAndApplyAsync(update);

            var staged = Assert.IsType<DesktopUpdateHandoff>(handoff);
            Assert.True(File.Exists(
                Path.Combine(staged.PayloadDirectory, ExecutableName)));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [Fact]
    public async Task ArchiveWithoutTheApplication_IsRejected()
    {
        var scope = TestScope.Create();
        try
        {
            var package = CreateReleaseArchive(
                [("notes.txt", "no application here")]);
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var manifest = CreateManifest(signingKey, package, "2.0.0");
            var started = false;
            var service = new DesktopApplicationUpdateService(
                CreateOptions(
                    scope.DirectoryPath,
                    signingKey,
                    "1.0.0",
                    scope.CreateInstallDirectory()),
                remoteServerSettingsStore: null,
                new HttpClient(new UpdateHandler(manifest, package)),
                _ =>
                {
                    started = true;
                    return true;
                });
            var update = Assert.IsType<ApplicationUpdateInfo>(
                await service.CheckForUpdateAsync());

            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.DownloadAndApplyAsync(update).AsTask());

            Assert.False(started);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [Fact]
    public async Task ArchiveEntryEscapingThePayload_IsRejected()
    {
        var scope = TestScope.Create();
        try
        {
            var package = CreateReleaseArchive(
            [
                (ExecutableName, "new build"),
                ("../escaped.txt", "should never be written")
            ]);
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var manifest = CreateManifest(signingKey, package, "2.0.0");
            var started = false;
            var service = new DesktopApplicationUpdateService(
                CreateOptions(
                    scope.DirectoryPath,
                    signingKey,
                    "1.0.0",
                    scope.CreateInstallDirectory()),
                remoteServerSettingsStore: null,
                new HttpClient(new UpdateHandler(manifest, package)),
                _ =>
                {
                    started = true;
                    return true;
                });
            var update = Assert.IsType<ApplicationUpdateInfo>(
                await service.CheckForUpdateAsync());

            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.DownloadAndApplyAsync(update).AsTask());

            Assert.False(started);
            Assert.Empty(
                Directory.EnumerateFiles(
                    scope.DirectoryPath,
                    "escaped.txt",
                    SearchOption.AllDirectories));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [Fact]
    public async Task DownloadWithChangedPayload_IsRejectedBeforeStaging()
    {
        var scope = TestScope.Create();
        try
        {
            var signedPackage = CreateReleaseArchive(
                [(ExecutableName, "expected build")]);
            var changedPackage = CreateReleaseArchive(
                [(ExecutableName, "tampered build")]);
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var manifest = CreateManifest(
                signingKey,
                signedPackage,
                "2.0.0");
            var started = false;
            var service = new DesktopApplicationUpdateService(
                CreateOptions(
                    scope.DirectoryPath,
                    signingKey,
                    "1.0.0",
                    scope.CreateInstallDirectory()),
                remoteServerSettingsStore: null,
                new HttpClient(new UpdateHandler(manifest, changedPackage)),
                _ =>
                {
                    started = true;
                    return true;
                });
            var update = Assert.IsType<ApplicationUpdateInfo>(
                await service.CheckForUpdateAsync());

            await Assert.ThrowsAnyAsync<Exception>(
                () => service.DownloadAndApplyAsync(update).AsTask());

            Assert.False(started);
            Assert.Empty(
                Directory.EnumerateFiles(
                    scope.DirectoryPath,
                    "*.zip",
                    SearchOption.AllDirectories));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [Fact]
    public async Task ManifestPointingAtAnInstaller_IsRejected()
    {
        var scope = TestScope.Create();
        try
        {
            var package = CreateReleaseArchive(
                [(ExecutableName, "new build")]);
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var manifest = CreateManifest(
                signingKey,
                package,
                "2.0.0",
                downloadUri:
                    "https://updates.example/packages/UMD-Setup.exe");
            var service = new DesktopApplicationUpdateService(
                CreateOptions(
                    scope.DirectoryPath,
                    signingKey,
                    "1.0.0",
                    scope.CreateInstallDirectory()),
                remoteServerSettingsStore: null,
                new HttpClient(new UpdateHandler(manifest, package)),
                _ => true);
            var update = Assert.IsType<ApplicationUpdateInfo>(
                await service.CheckForUpdateAsync());

            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.DownloadAndApplyAsync(update).AsTask());
        }
        finally
        {
            scope.Dispose();
        }
    }

    [Fact]
    public async Task OlderRelease_DoesNotPrompt()
    {
        var scope = TestScope.Create();
        try
        {
            var package = CreateReleaseArchive(
                [(ExecutableName, "older build")]);
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var manifest = CreateManifest(signingKey, package, "1.4.9");
            var service = new DesktopApplicationUpdateService(
                CreateOptions(
                    scope.DirectoryPath,
                    signingKey,
                    "1.5.0",
                    scope.CreateInstallDirectory()),
                remoteServerSettingsStore: null,
                new HttpClient(new UpdateHandler(manifest, package)),
                _ => true);

            var update = await service.CheckForUpdateAsync();

            Assert.Null(update);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [Fact]
    public void ReadOnlyInstallDirectory_IsDetected()
    {
        var scope = TestScope.Create();
        try
        {
            var writable = scope.CreateInstallDirectory();

            Assert.True(
                DesktopApplicationUpdateService.IsDirectoryWritable(writable));
            Assert.False(
                DesktopApplicationUpdateService.IsDirectoryWritable(
                    Path.Combine(writable, "missing-directory")));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [Fact]
    public async Task TheConnectedServerIsUsedAsTheUpdateServer()
    {
        var scope = TestScope.Create();
        try
        {
            var package = CreateReleaseArchive([(ExecutableName, "new build")]);
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var manifest = CreateManifest(signingKey, package, "2.0.0");
            var handler = new UpdateHandler(manifest, package);
            var service = new DesktopApplicationUpdateService(
                CreateOptions(
                    scope.DirectoryPath,
                    signingKey,
                    "1.0.0",
                    scope.CreateInstallDirectory(),
                    serviceBaseUriOverride: null),
                new InMemoryRemoteServerSettingsStore(
                    new RemoteServerSettings
                    {
                        ServiceUrl = "https://media.example.com/"
                    }),
                new HttpClient(handler));

            var update = Assert.IsType<ApplicationUpdateInfo>(
                await service.CheckForUpdateAsync());

            Assert.Equal("2.0.0", update.Version);
            Assert.Equal(
                "https://media.example.com/api/v1/releases/stable/win-x64/latest",
                handler.LastRequestUri?.AbsoluteUri);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [Fact]
    public async Task ALoadedServerUrlIsRememberedAfterTheDatabaseBecomesUnavailable()
    {
        var scope = TestScope.Create();
        try
        {
            var package = CreateReleaseArchive([(ExecutableName, "new build")]);
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var manifest = CreateManifest(signingKey, package, "2.0.0");
            var handler = new UpdateHandler(manifest, package);
            var settingsStore = new LoadOnceRemoteServerSettingsStore(
                new RemoteServerSettings
                {
                    ServiceUrl = "https://remembered.example.com/"
                });
            var service = new DesktopApplicationUpdateService(
                CreateOptions(
                    scope.DirectoryPath,
                    signingKey,
                    "1.0.0",
                    scope.CreateInstallDirectory(),
                    serviceBaseUriOverride: null),
                settingsStore,
                new HttpClient(handler));

            await service.CheckForUpdateAsync();
            await service.CheckForUpdateAsync();

            Assert.Equal(1, settingsStore.LoadCount);
            Assert.Equal(
                "https://remembered.example.com/api/v1/releases/stable/win-x64/latest",
                handler.LastRequestUri?.AbsoluteUri);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [Fact]
    public async Task ASettingsTextboxUrlAvoidsAUnavailableDatabase()
    {
        var scope = TestScope.Create();
        try
        {
            var package = CreateReleaseArchive([(ExecutableName, "new build")]);
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var manifest = CreateManifest(signingKey, package, "2.0.0");
            var handler = new UpdateHandler(manifest, package);
            var settingsStore = new ThrowingRemoteServerSettingsStore();
            var service = new DesktopApplicationUpdateService(
                CreateOptions(
                    scope.DirectoryPath,
                    signingKey,
                    "1.0.0",
                    scope.CreateInstallDirectory(),
                    serviceBaseUriOverride: null),
                settingsStore,
                new HttpClient(handler));
            service.RememberServiceUrl("https://typed.example.com");

            var update = await service.CheckForUpdateAsync();

            Assert.NotNull(update);
            Assert.Equal(0, settingsStore.LoadCount);
            Assert.Equal(
                "https://typed.example.com/api/v1/releases/stable/win-x64/latest",
                handler.LastRequestUri?.AbsoluteUri);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [Fact]
    public async Task WithoutAConnectedServerThereIsNoUpdateToOffer()
    {
        var scope = TestScope.Create();
        try
        {
            var package = CreateReleaseArchive([(ExecutableName, "new build")]);
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var manifest = CreateManifest(signingKey, package, "2.0.0");
            var handler = new UpdateHandler(manifest, package);
            var service = new DesktopApplicationUpdateService(
                CreateOptions(
                    scope.DirectoryPath,
                    signingKey,
                    "1.0.0",
                    scope.CreateInstallDirectory(),
                    serviceBaseUriOverride: null),
                new InMemoryRemoteServerSettingsStore(
                    new RemoteServerSettings { ServiceUrl = string.Empty }),
                new HttpClient(handler));

            Assert.Null(await service.CheckForUpdateAsync());
            // Nothing was asked of the network at all.
            Assert.Null(handler.LastRequestUri);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [Fact]
    public async Task AnEnvironmentOverrideWinsOverTheConnectedServer()
    {
        var scope = TestScope.Create();
        try
        {
            var package = CreateReleaseArchive([(ExecutableName, "new build")]);
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var manifest = CreateManifest(signingKey, package, "2.0.0");
            var handler = new UpdateHandler(manifest, package);
            var service = new DesktopApplicationUpdateService(
                CreateOptions(
                    scope.DirectoryPath,
                    signingKey,
                    "1.0.0",
                    scope.CreateInstallDirectory()),
                new InMemoryRemoteServerSettingsStore(
                    new RemoteServerSettings
                    {
                        ServiceUrl = "https://media.example.com/"
                    }),
                new HttpClient(handler));

            await service.CheckForUpdateAsync();

            Assert.StartsWith(
                "https://updates.example/",
                handler.LastRequestUri?.AbsoluteUri,
                StringComparison.Ordinal);
        }
        finally
        {
            scope.Dispose();
        }
    }

    private static DesktopUpdateOptions CreateOptions(
        string directory,
        ECDsa signingKey,
        string currentVersion,
        string installDirectory,
        string? serviceBaseUriOverride = "https://updates.example/") =>
        new(
            serviceBaseUriOverride is null
                ? null
                : new Uri(serviceBaseUriOverride),
            "stable",
            "win-x64",
            "test-key",
            signingKey.ExportSubjectPublicKeyInfo(),
            currentVersion,
            Path.Combine(directory, "updates"),
            installDirectory);

    private static byte[] CreateReleaseArchive(
        (string Path, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(
            buffer,
            ZipArchiveMode.Create,
            leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(path);
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return buffer.ToArray();
    }

    private static string CreateManifest(
        ECDsa signingKey,
        byte[] package,
        string version,
        string downloadUri =
            "https://updates.example/packages/umd-win-x64.zip")
    {
        var hash = SHA256.HashData(package);
        var signature = signingKey.SignHash(
            hash,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return JsonSerializer.Serialize(new
        {
            version,
            channel = "stable",
            runtimeIdentifier = "win-x64",
            downloadUri,
            sha256 = Convert.ToHexString(hash),
            sizeBytes = package.LongLength,
            publishedAt = DateTimeOffset.UtcNow,
            signingKeyId = "test-key",
            signatureAlgorithm = "ECDSA-P256-SHA256",
            signature = Convert.ToBase64String(signature),
            releaseNotes = "Test update"
        });
    }

    private sealed class UpdateHandler(
        string manifest,
        byte[] package) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri ??= request.RequestUri;
            var response = request.RequestUri?.AbsolutePath.EndsWith(
                "/latest",
                StringComparison.Ordinal) is true
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        manifest,
                        Encoding.UTF8,
                        "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(package)
                };
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class LoadOnceRemoteServerSettingsStore(
        RemoteServerSettings settings) : IRemoteServerSettingsStore
    {
        public int LoadCount { get; private set; }

        public ValueTask<RemoteServerSettings> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            if (LoadCount > 1)
            {
                throw new InvalidOperationException(
                    "The database is no longer available.");
            }

            return ValueTask.FromResult(settings);
        }

        public ValueTask SaveAsync(
            RemoteServerSettings value,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class ThrowingRemoteServerSettingsStore :
        IRemoteServerSettingsStore
    {
        public int LoadCount { get; private set; }

        public ValueTask<RemoteServerSettings> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            throw new InvalidOperationException(
                "The database is unavailable.");
        }

        public ValueTask SaveAsync(
            RemoteServerSettings settings,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
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

        /// <summary>
        /// Stands in for the directory the running application lives in.
        /// </summary>
        public string CreateInstallDirectory()
        {
            var path = Path.Combine(DirectoryPath, "install");
            Directory.CreateDirectory(path);
            File.WriteAllText(
                Path.Combine(path, ExecutableName),
                "old build");
            return path;
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
