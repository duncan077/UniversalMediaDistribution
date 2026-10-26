using System.Diagnostics;
using System.Net.Sockets;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Catalog.Sqlite;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.Domain.Reading;
using UniversalMediaDistribution.UI.Remote;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// Walks the sequence a phone performs when the user taps "Sign in": read
/// capabilities, log in, then reconcile device state (download snapshot,
/// restore it into the local database, export, upload). A failure anywhere
/// after the login call used to be reported to the user as a failed sign-in.
/// </summary>
public sealed class MobileRemoteSignInTests
{
    [Fact]
    public async Task FreshPhone_SignsInAndReconcilesAnExistingAccountSnapshot()
    {
        var dataDirectory = CreateTemporaryDirectory("umd-mobile-signin-");
        var desktopDirectory = CreateTemporaryDirectory("umd-desktop-db-");
        var phoneDirectory = CreateTemporaryDirectory("umd-phone-db-");
        var port = ReservePort();
        var serviceUri = new Uri($"http://127.0.0.1:{port}/");
        using var process = StartServer(serviceUri, dataDirectory);
        try
        {
            await WaitUntilReadyAsync(process, serviceUri);

            // A desktop already published this account's snapshot.
            var desktopDatabase = new SqliteCatalogDatabase(
                Path.Combine(desktopDirectory, "catalog.db"));
            var desktopRepository = new SqliteCatalogRepository(
                desktopDatabase);
            await desktopRepository.MergeCatalogAsync(
                TestSyncCatalog.Document());
            var book = Assert.Single(
                await desktopRepository.ListAsync(MediaType.Book));
            var details = Assert.IsType<
                Application.Catalog.CatalogItemDetails>(
                await desktopRepository.GetDetailsAsync(book.Id));
            var desktopState = new SqliteUserMediaStateStore(
                desktopDatabase);
            await desktopState.SaveProgressAsync(
                Assert.Single(details.Units).Identity,
                new MediaProgressState(
                    9,
                    MediaPositionKind.Page,
                    isCompleted: false,
                    DateTimeOffset.UtcNow));
            await desktopState.SaveReaderDisplayModeAsync(
                details.Identity,
                ReaderDisplayMode.Continuous);
            await desktopState.SetFavoriteCatalogItemAsync(
                book.Id,
                isFavorite: true);
            var list = await desktopState.CreateUserListAsync("Read later");
            await desktopState.SetUserListMembershipAsync(
                list.Id,
                book.Id,
                isMember: true);

            using var desktopClient = new HttpRemoteMediaServerClient();
            await desktopClient.GetCapabilitiesAsync(serviceUri);
            await desktopClient.RegisterAsync(
                serviceUri,
                "phone.user",
                "correct horse battery staple",
                "Phone User",
                "Desktop");
            var desktopSync = new SqliteUserSyncStore(
                desktopDatabase,
                desktopRepository);
            await RemoteUserSyncReconciler.SynchronizeAsync(
                desktopClient,
                desktopSync,
                "desktop-device",
                "Desktop");

            // The phone installs fresh and signs in to the same account.
            var phoneDatabase = new SqliteCatalogDatabase(
                Path.Combine(phoneDirectory, "catalog.db"));
            var phoneRepository = new SqliteCatalogRepository(phoneDatabase);
            var phoneSync = new SqliteUserSyncStore(
                phoneDatabase,
                phoneRepository);
            using var phoneClient = new HttpRemoteMediaServerClient();
            await phoneClient.GetCapabilitiesAsync(serviceUri);
            var session = await phoneClient.LoginAsync(
                serviceUri,
                "phone.user",
                "correct horse battery staple",
                "Pixel 8");
            Assert.Equal("Phone User", session.DisplayName);

            var reconciliation =
                await RemoteUserSyncReconciler.SynchronizeAsync(
                    phoneClient,
                    phoneSync,
                    "phone-device",
                    "Pixel 8");

            Assert.NotNull(phoneClient.Session);
            // Downloading the existing state does not need an identical upload.
            Assert.Equal(1, reconciliation.WriteResult.Revision);
            var restoredBook = Assert.Single(
                await phoneRepository.ListAsync(MediaType.Book));
            var phoneState = new SqliteUserMediaStateStore(phoneDatabase);
            Assert.Equal(
                [restoredBook.Id],
                await phoneState.LoadFavoriteCatalogItemIdsAsync());
            Assert.Single(await phoneState.LoadUserListsAsync());
        }
        finally
        {
            await StopServerAsync(process);
            DeleteDirectory(dataDirectory);
            DeleteDirectory(desktopDirectory);
            DeleteDirectory(phoneDirectory);
        }
    }

    private static string CreateTemporaryDirectory(string prefix)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            prefix + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static Process StartServer(
        Uri serviceUri,
        string dataDirectory)
    {
        var repositoryDirectory = FindRepositoryDirectory();
        var configuration = new DirectoryInfo(
            AppContext.BaseDirectory).Parent?.Name ??
            throw new InvalidOperationException(
                "The test build configuration could not be determined.");
        var assemblyPath = Path.Combine(
            repositoryDirectory,
            "src",
            "UniversalMediaDistribution.Server",
            "bin",
            configuration,
            "net11.0",
            "UniversalMediaDistribution.Server.dll");
        Assert.True(
            File.Exists(assemblyPath),
            $"The test server assembly was not built at '{assemblyPath}'.");
        var startInfo = new ProcessStartInfo(
            "dotnet",
            $"\"{assemblyPath}\" --urls {serviceUri.AbsoluteUri}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment["UMD_SERVER_DATA_DIRECTORY"] = dataDirectory;
        startInfo.Environment["UMD_SERVER_ALLOW_REGISTRATION"] = "true";
        return Process.Start(startInfo) ??
            throw new InvalidOperationException(
                "The integration server could not be started.");
    }

    private static string FindRepositoryDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                current.FullName,
                "UniversalMediaDistribution.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            "The repository directory could not be found.");
    }

    private static async Task WaitUntilReadyAsync(
        Process process,
        Uri serviceUri)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(500)
        };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    "The integration server exited before startup.\n" +
                    await process.StandardError.ReadToEndAsync());
            }

            try
            {
                using var response = await client.GetAsync(
                    new Uri(serviceUri, "health"));
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            await Task.Delay(50);
        }

        throw new TimeoutException(
            "The integration server did not become ready.");
    }

    private static async Task StopServerAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync();
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(
            System.Net.IPAddress.Loopback,
            0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
