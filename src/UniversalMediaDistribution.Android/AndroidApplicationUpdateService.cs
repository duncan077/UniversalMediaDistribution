using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Android.Content;
using Android.Content.PM;
using Android.Provider;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Mobile;
using UniversalMediaDistribution.UI.Updates;
using AndroidUri = Android.Net.Uri;

namespace UniversalMediaDistribution.Android;

/// <summary>
/// Sideloaded update flow for builds distributed outside the Play Store. The
/// release endpoint is derived from the UMD server the user configured, the
/// download is checked against the manifest hash, and the install itself is
/// performed by Android, which asks the user to confirm and refuses any package
/// that is not signed with the installed app's certificate.
/// </summary>
internal sealed partial class AndroidApplicationUpdateService(
    Context context,
    IRemoteServerSettingsStore remoteServerSettingsStore,
    string currentVersion,
    string runtimeIdentifier,
    string channel = "stable") : IApplicationUpdateService
{
    private const string ApkMimeType =
        "application/vnd.android.package-archive";
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _httpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private ReleaseManifest? _latestManifest;
    private RememberedServiceUri? _rememberedServiceUri;

    public bool IsAvailable => true;

    public ApplicationUpdateInstallKind InstallKind =>
        ApplicationUpdateInstallKind.SystemInstaller;

    public void RememberServiceUrl(string? serviceUrl)
    {
        if (string.IsNullOrWhiteSpace(serviceUrl))
        {
            Volatile.Write(
                ref _rememberedServiceUri,
                new RememberedServiceUri(null));
            return;
        }

        if (!Uri.TryCreate(
                serviceUrl.Trim(),
                UriKind.Absolute,
                out var uri) ||
            !RemoteServerSettings.IsSecureServiceUri(uri))
        {
            throw new ArgumentException(
                "The optional server URL must use HTTPS. HTTP is allowed only for loopback development.",
                nameof(serviceUrl));
        }

        Volatile.Write(
            ref _rememberedServiceUri,
            new RememberedServiceUri(
                uri.AbsoluteUri.EndsWith('/')
                    ? uri
                    : new Uri(uri.AbsoluteUri + '/', UriKind.Absolute)));
    }

    public async ValueTask<ApplicationUpdateInfo?> CheckForUpdateAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var serviceUri = await ResolveServiceUriAsync();
            if (serviceUri is null)
            {
                _latestManifest = null;
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(CheckTimeout);
            var manifest = await FetchManifestAsync(
                serviceUri,
                runtimeIdentifier,
                timeout.Token);
            if (manifest is null &&
                !string.Equals(
                    runtimeIdentifier,
                    AndroidUpdateTarget.LegacyUniversal,
                    StringComparison.OrdinalIgnoreCase))
            {
                // New split builds remain compatible with servers that have
                // not published per-device manifests yet. The production
                // publisher also keeps this legacy target for one bridge
                // release so older universal clients are not stranded.
                manifest = await FetchManifestAsync(
                    serviceUri,
                    AndroidUpdateTarget.LegacyUniversal,
                    timeout.Token);
            }

            if (manifest is null)
            {
                _latestManifest = null;
                return null;
            }

            if (CompareVersions(manifest.Version, currentVersion) <= 0)
            {
                _latestManifest = null;
                return null;
            }

            _latestManifest = manifest;
            return new ApplicationUpdateInfo(
                manifest.Version,
                manifest.SizeBytes,
                manifest.PublishedAt,
                manifest.ReleaseNotes);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<ApplicationUpdateApplyResult> DownloadAndApplyAsync(
        ApplicationUpdateInfo update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var manifest = _latestManifest;
            if (manifest is null ||
                !string.Equals(
                    manifest.Version,
                    update.Version,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Check for updates again before downloading.");
            }

            var directory = Path.Combine(
                context.CacheDir?.AbsolutePath ??
                    throw new InvalidOperationException(
                        "Android did not provide a cache directory."),
                "updates");
            Directory.CreateDirectory(directory);
            var apkPath = Path.Combine(
                directory,
                $"umd-{SanitizePathSegment(manifest.RuntimeIdentifier)}-" +
                $"{SanitizeVersion(manifest.Version)}.apk");
            var temporaryPath = apkPath + ".tmp";
            TryDelete(temporaryPath);
            try
            {
                using var response = await _httpClient.GetAsync(
                    manifest.DownloadUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();
                var hash = await DownloadAndHashAsync(
                    response,
                    temporaryPath,
                    manifest.SizeBytes,
                    progress,
                    cancellationToken);
                if (!CryptographicOperations.FixedTimeEquals(
                        hash,
                        Convert.FromHexString(manifest.Sha256)))
                {
                    throw new CryptographicException(
                        "The update package checksum is invalid.");
                }

                File.Move(temporaryPath, apkPath, overwrite: true);
                progress?.Report(1);
            }
            catch
            {
                TryDelete(temporaryPath);
                throw;
            }

            // Android 8 and newer require the user to allow this app to install
            // packages before the installer will even open.
            if (OperatingSystem.IsAndroidVersionAtLeast(26) &&
                context.PackageManager is { } packageManager &&
                !packageManager.CanRequestPackageInstalls())
            {
                RequestInstallPermission();
                return new ApplicationUpdateApplyResult(
                    apkPath,
                    ApplicationUpdateOutcome.PermissionRequired);
            }

            StartSystemInstaller(apkPath);
            return new ApplicationUpdateApplyResult(
                apkPath,
                ApplicationUpdateOutcome.InstallerRequested);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async ValueTask<Uri?> ResolveServiceUriAsync()
    {
        if (Volatile.Read(ref _rememberedServiceUri) is { } remembered)
        {
            return remembered.Value;
        }

        var settings = await remoteServerSettingsStore.LoadAsync();
        RememberServiceUrl(settings.Normalize().ServiceUrl);
        return Volatile.Read(ref _rememberedServiceUri)?.Value;
    }

    private async ValueTask<ReleaseManifest?> FetchManifestAsync(
        Uri serviceUri,
        string requestedRuntimeIdentifier,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(
            serviceUri,
            $"api/v1/releases/{channel}/" +
            $"{Uri.EscapeDataString(requestedRuntimeIdentifier)}/latest");
        using var response = await _httpClient.GetAsync(
            endpoint,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        var manifest = await JsonSerializer.DeserializeAsync(
            stream,
            AndroidUpdateJsonContext.Default.ReleaseManifest,
            cancellationToken) ??
            throw new InvalidDataException(
                "The update server returned an empty manifest.");
        ValidateManifest(manifest, requestedRuntimeIdentifier);
        return manifest;
    }

    /// <summary>
    /// Opens the screen where the user allows this app to install packages.
    /// Android TV builds frequently omit the per-app screen, so the device
    /// security settings are used as a fallback.
    /// </summary>
    private void RequestInstallPermission()
    {
        var candidates = new List<Intent>();
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            candidates.Add(new Intent(
                Settings.ActionManageUnknownAppSources,
                AndroidUri.Parse("package:" + context.PackageName)));
            candidates.Add(
                new Intent(Settings.ActionManageUnknownAppSources));
        }

        candidates.Add(new Intent(Settings.ActionSecuritySettings));
        candidates.Add(new Intent(Settings.ActionSettings));
        foreach (var intent in candidates)
        {
            try
            {
                intent.AddFlags(ActivityFlags.NewTask);
                context.StartActivity(intent);
                return;
            }
            catch (ActivityNotFoundException)
            {
                // Try the next, broader settings screen.
            }
            finally
            {
                intent.Dispose();
            }
        }
    }

    private void StartSystemInstaller(string apkPath)
    {
        var authority = context.PackageName + ".updates";
        var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(
            context,
            authority,
            new Java.IO.File(apkPath));
        using var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(uri, ApkMimeType);
        intent.AddFlags(
            ActivityFlags.NewTask |
            ActivityFlags.GrantReadUriPermission);
        try
        {
            context.StartActivity(intent);
        }
        catch (ActivityNotFoundException exception)
        {
            throw new InvalidOperationException(
                "This device has no package installer, so the downloaded " +
                $"update must be installed manually from '{apkPath}'.",
                exception);
        }
    }

    private static async Task<byte[]> DownloadAndHashAsync(
        HttpResponseMessage response,
        string destinationPath,
        long expectedSize,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        await using var source = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long totalBytes = 0;
        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytes += bytesRead;
            if (totalBytes > expectedSize)
            {
                throw new InvalidDataException(
                    "The update package is larger than its manifest.");
            }

            hash.AppendData(buffer.AsSpan(0, bytesRead));
            await destination.WriteAsync(
                buffer.AsMemory(0, bytesRead),
                cancellationToken);
            progress?.Report(
                Math.Clamp((double)totalBytes / expectedSize, 0, 1));
        }

        await destination.FlushAsync(cancellationToken);
        if (totalBytes != expectedSize)
        {
            throw new InvalidDataException(
                "The update package is incomplete.");
        }

        return hash.GetHashAndReset();
    }

    private void ValidateManifest(
        ReleaseManifest manifest,
        string expectedRuntimeIdentifier)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version) ||
            !string.Equals(
                manifest.Channel,
                channel,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                manifest.RuntimeIdentifier,
                expectedRuntimeIdentifier,
                StringComparison.OrdinalIgnoreCase) ||
            !IsSecureDownload(manifest.DownloadUri) ||
            manifest.Sha256.Length != 64 ||
            !manifest.Sha256.All(Uri.IsHexDigit) ||
            manifest.SizeBytes <= 0 ||
            !manifest.DownloadUri.AbsolutePath.EndsWith(
                ".apk",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The update manifest is invalid or does not reference an APK.");
        }
    }

    private static bool IsSecureDownload(Uri uri) =>
        uri.IsAbsoluteUri &&
        (string.Equals(
             uri.Scheme,
             Uri.UriSchemeHttps,
             StringComparison.OrdinalIgnoreCase) ||
         (string.Equals(
              uri.Scheme,
              Uri.UriSchemeHttp,
              StringComparison.OrdinalIgnoreCase) &&
          uri.IsLoopback));

    private static string SanitizeVersion(string version)
    {
        var normalized = new string(
            version
                .Trim()
                .TrimStart('v', 'V')
                .Where(static character =>
                    char.IsAsciiLetterOrDigit(character) ||
                    character is '.' or '-')
                .ToArray());
        return normalized.Length == 0 ? "update" : normalized;
    }

    private static string SanitizePathSegment(string value)
    {
        var normalized = new string(
            value
                .Trim()
                .Where(static character =>
                    char.IsAsciiLetterOrDigit(character) ||
                    character is '.' or '-')
                .ToArray());
        return normalized.Length == 0 ? "android" : normalized;
    }

    private static int CompareVersions(string candidate, string current)
    {
        var candidateVersion = ParseVersion(candidate);
        var currentVersion = ParseVersion(current);
        var coreComparison = candidateVersion.Core.CompareTo(
            currentVersion.Core);
        if (coreComparison != 0)
        {
            return coreComparison;
        }

        if (candidateVersion.IsPrerelease == currentVersion.IsPrerelease)
        {
            return string.Compare(
                candidateVersion.Prerelease,
                currentVersion.Prerelease,
                StringComparison.OrdinalIgnoreCase);
        }

        return candidateVersion.IsPrerelease ? -1 : 1;
    }

    private static ParsedVersion ParseVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().TrimStart('v', 'V');
        var separator = normalized.IndexOfAny(['-', '+']);
        var coreValue = separator < 0
            ? normalized
            : normalized[..separator];
        var prerelease = separator >= 0 && normalized[separator] == '-'
            ? normalized[(separator + 1)..].Split('+')[0]
            : null;
        if (!Version.TryParse(coreValue, out var core))
        {
            throw new InvalidDataException(
                $"'{value}' is not a supported release version.");
        }

        return new ParsedVersion(core, prerelease);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A stale temporary file is replaced on the next attempt.
        }
    }

    private sealed record ReleaseManifest(
        string Version,
        string Channel,
        string RuntimeIdentifier,
        Uri DownloadUri,
        string Sha256,
        long SizeBytes,
        DateTimeOffset PublishedAt,
        string? ReleaseNotes);

    private sealed record RememberedServiceUri(Uri? Value);

    private readonly record struct ParsedVersion(
        Version Core,
        string? Prerelease)
    {
        public bool IsPrerelease => !string.IsNullOrWhiteSpace(Prerelease);
    }

    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(ReleaseManifest))]
    private sealed partial class AndroidUpdateJsonContext :
        JsonSerializerContext;
}
