using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.UI.Updates;

namespace UniversalMediaDistribution.Desktop;

/// <param name="ServiceBaseUriOverride">
/// Normally null: the update server is the optional server the user connected
/// in Settings, the same one Android uses. An override exists for self-hosted
/// deployments that publish releases somewhere other than their sync server.
/// </param>
internal sealed record DesktopUpdateOptions(
    Uri? ServiceBaseUriOverride,
    string Channel,
    string RuntimeIdentifier,
    string SigningKeyId,
    byte[] SigningPublicKey,
    string CurrentVersion,
    string StagingDirectory,
    string InstallDirectory)
{
    private const string ServiceUrlSetting =
        "UMD_UPDATE_SERVICE_URL";
    private const string SigningPublicKeySetting =
        "UMD_UPDATE_SIGNING_PUBLIC_KEY";
    private const string SigningKeyIdSetting =
        "UMD_UPDATE_SIGNING_KEY_ID";
    private const string ChannelSetting =
        "UMD_UPDATE_CHANNEL";

    public static DesktopUpdateOptions? FromEnvironment(
        string applicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        var serviceUrl = ResolveSetting(ServiceUrlSetting);
        var signingKey = ResolveSetting(SigningPublicKeySetting);
        var signingKeyId = ResolveSetting(SigningKeyIdSetting);
        // The signing key stays a build-time pin. Desktop updates replace the
        // running executables, so the payload signature is the only thing
        // standing between a hostile server and code execution; Android can do
        // without it because the system installer enforces the APK signature.
        if (string.IsNullOrWhiteSpace(signingKey) ||
            string.IsNullOrWhiteSpace(signingKeyId))
        {
            return null;
        }

        Uri? serviceUri = null;
        if (!string.IsNullOrWhiteSpace(serviceUrl) &&
            (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out serviceUri) ||
             !IsSecureEndpoint(serviceUri)))
        {
            throw new InvalidOperationException(
                "UMD_UPDATE_SERVICE_URL must be an HTTPS URL.");
        }

        byte[] publicKey;
        try
        {
            publicKey = Convert.FromBase64String(signingKey);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "UMD_UPDATE_SIGNING_PUBLIC_KEY must be Base64-encoded.",
                exception);
        }

        using (var ecdsa = ECDsa.Create())
        {
            try
            {
                ecdsa.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
                if (bytesRead != publicKey.Length ||
                    ecdsa.KeySize != 256)
                {
                    throw new CryptographicException(
                        "The update key must be an ECDSA P-256 public key.");
                }
            }
            catch (CryptographicException exception)
            {
                throw new InvalidOperationException(
                    "UMD_UPDATE_SIGNING_PUBLIC_KEY is not a valid ECDSA P-256 public key.",
                    exception);
            }
        }

        var channel = ResolveSetting(ChannelSetting) ?? "stable";
        return new DesktopUpdateOptions(
            serviceUri is null ? null : EnsureTrailingSlash(serviceUri),
            NormalizeSegment(channel, nameof(channel)),
            NormalizeSegment(
                RuntimeInformation.RuntimeIdentifier,
                nameof(RuntimeIdentifier)),
            signingKeyId.Trim(),
            publicKey,
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ??
                "0.0.0",
            Path.Combine(
                Path.GetFullPath(applicationDataDirectory),
                "updates"),
            Path.GetFullPath(AppContext.BaseDirectory));
    }

    /// <summary>
    /// Environment values remain useful for local and self-hosted builds. An
    /// official release instead carries its public trust configuration as
    /// assembly metadata, so every user's Windows installation can check for
    /// updates without machine-specific environment variables.
    /// </summary>
    internal static string? ResolveSetting(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var environmentValue = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue;
        }

        return typeof(DesktopUpdateOptions).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute =>
                string.Equals(
                    attribute.Key,
                    name,
                    StringComparison.Ordinal))
            ?.Value;
    }

    internal static bool IsSecureEndpoint(Uri uri) =>
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

    internal static string NormalizeSegment(
        string value,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > 64 ||
            normalized.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '-')))
        {
            throw new ArgumentException(
                "Only ASCII letters, digits, dots, and hyphens are allowed.",
                parameterName);
        }

        return normalized;
    }

    internal static Uri EnsureTrailingSlash(Uri uri)
    {
        var value = uri.AbsoluteUri;
        return value.EndsWith('/')
            ? uri
            : new Uri(value + '/', UriKind.Absolute);
    }
}

/// <summary>
/// Describes the staged update handed to the external helper that replaces the
/// running application's files once it has exited.
/// </summary>
internal sealed record DesktopUpdateHandoff(
    string ScriptPath,
    string PayloadDirectory,
    string InstallDirectory,
    string ExecutablePath,
    bool RequiresElevation);

internal sealed partial class DesktopApplicationUpdateService :
    IApplicationUpdateService
{
    private const string SignatureAlgorithm = "ECDSA-P256-SHA256";
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);
    private static readonly string[] ArchiveExtensions =
    [
        ".zip",
        ".tar.gz",
        ".tgz"
    ];

    private readonly DesktopUpdateOptions _options;
    private readonly IRemoteServerSettingsStore? _remoteServerSettingsStore;
    private readonly HttpClient _httpClient;
    private readonly Func<DesktopUpdateHandoff, bool> _startUpdateHelper;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private ReleaseManifest? _latestManifest;
    private RememberedServiceUri? _rememberedServiceUri;

    public DesktopApplicationUpdateService(
        DesktopUpdateOptions options,
        IRemoteServerSettingsStore? remoteServerSettingsStore = null,
        HttpClient? httpClient = null,
        Func<DesktopUpdateHandoff, bool>? startUpdateHelper = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _remoteServerSettingsStore = remoteServerSettingsStore;
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _startUpdateHelper = startUpdateHelper ?? StartUpdateHelper;
        Directory.CreateDirectory(_options.StagingDirectory);
    }

    public bool IsAvailable => true;

    public ApplicationUpdateInstallKind InstallKind =>
        ApplicationUpdateInstallKind.ReplaceAndRestart;

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
                DesktopUpdateOptions.EnsureTrailingSlash(uri)));
    }

    /// <summary>
    /// The update server is the optional server connected in Settings, so a
    /// desktop install follows the same address as its sync and catalog, and
    /// nothing has to be configured twice. An environment override wins when
    /// one is set.
    /// </summary>
    private async ValueTask<Uri?> ResolveServiceUriAsync()
    {
        if (_options.ServiceBaseUriOverride is { } configured)
        {
            return configured;
        }

        if (Volatile.Read(ref _rememberedServiceUri) is { } remembered)
        {
            return remembered.Value;
        }

        if (_remoteServerSettingsStore is null)
        {
            return null;
        }

        var settings = await _remoteServerSettingsStore.LoadAsync();
        RememberServiceUrl(settings.Normalize().ServiceUrl);
        return Volatile.Read(ref _rememberedServiceUri)?.Value;
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
                // No server connected in Settings and no override, so there is
                // nowhere to ask; the app simply has no update to offer.
                _latestManifest = null;
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(CheckTimeout);
            var endpoint = new Uri(
                serviceUri,
                $"api/v1/releases/{_options.Channel}/" +
                $"{_options.RuntimeIdentifier}/latest");
            using var response = await _httpClient.GetAsync(
                endpoint,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (response.StatusCode is System.Net.HttpStatusCode.NotFound)
            {
                _latestManifest = null;
                return null;
            }

            response.EnsureSuccessStatusCode();
            EnsureSecureResponse(response);
            await using var stream = await response.Content.ReadAsStreamAsync(
                timeout.Token);
            var manifest = await System.Text.Json.JsonSerializer.DeserializeAsync(
                stream,
                DesktopUpdateJsonContext.Default.ReleaseManifest,
                timeout.Token) ??
                throw new InvalidDataException(
                    "The update server returned an empty manifest.");
            ValidateManifest(manifest);
            if (CompareVersions(
                    manifest.Version,
                    _options.CurrentVersion) <= 0)
            {
                _latestManifest = null;
                return null;
            }

            _latestManifest = manifest;
            return ToUpdateInfo(manifest);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<ApplicationUpdateApplyResult>
        DownloadAndApplyAsync(
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

            var versionDirectory = Path.Combine(
                _options.StagingDirectory,
                DesktopUpdateOptions.NormalizeSegment(
                    manifest.Version.TrimStart('v', 'V'),
                    nameof(manifest.Version)));
            Directory.CreateDirectory(versionDirectory);
            var fileName = GetArchiveFileName(manifest.DownloadUri);
            var finalPath = Path.GetFullPath(
                Path.Combine(versionDirectory, fileName));
            var temporaryPath = finalPath + "." +
                Guid.CreateVersion7().ToString("N") +
                ".tmp";

            try
            {
                using var response = await _httpClient.GetAsync(
                    manifest.DownloadUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();
                EnsureSecureResponse(response);
                if (response.Content.Headers.ContentLength is { } length &&
                    length != manifest.SizeBytes)
                {
                    throw new InvalidDataException(
                        "The update package size does not match its manifest.");
                }

                var hash = await DownloadAndHashAsync(
                    response,
                    temporaryPath,
                    manifest.SizeBytes,
                    progress,
                    cancellationToken);
                VerifyPackage(manifest, hash);
                File.Move(temporaryPath, finalPath, overwrite: true);
                progress?.Report(1);

                var handoff = StagePayload(finalPath, versionDirectory);
                if (!_startUpdateHelper(handoff))
                {
                    throw new InvalidOperationException(
                        "The update helper could not be started.");
                }

                return new ApplicationUpdateApplyResult(
                    finalPath,
                    ApplicationUpdateOutcome.RestartRequired);
            }
            catch
            {
                TryDelete(temporaryPath);
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<byte[]> DownloadAndHashAsync(
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
            var bytesRead = await source.ReadAsync(
                buffer,
                cancellationToken);
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

    private void ValidateManifest(ReleaseManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version) ||
            CompareVersions(manifest.Version, "0.0.0") < 0 ||
            !string.Equals(
                manifest.Channel,
                _options.Channel,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                manifest.RuntimeIdentifier,
                _options.RuntimeIdentifier,
                StringComparison.OrdinalIgnoreCase) ||
            !DesktopUpdateOptions.IsSecureEndpoint(manifest.DownloadUri) ||
            manifest.Sha256.Length != 64 ||
            !manifest.Sha256.All(Uri.IsHexDigit) ||
            manifest.SizeBytes <= 0 ||
            !string.Equals(
                manifest.SigningKeyId,
                _options.SigningKeyId,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.SignatureAlgorithm,
                SignatureAlgorithm,
                StringComparison.Ordinal) ||
            !TryDecodeSignature(manifest.Signature, out _))
        {
            throw new InvalidDataException(
                "The update manifest is invalid or is signed by an untrusted key.");
        }
    }

    private void VerifyPackage(
        ReleaseManifest manifest,
        byte[] hash)
    {
        var expectedHash = Convert.FromHexString(manifest.Sha256);
        if (!CryptographicOperations.FixedTimeEquals(hash, expectedHash))
        {
            throw new CryptographicException(
                "The update package checksum is invalid.");
        }

        if (!TryDecodeSignature(manifest.Signature, out var signature))
        {
            throw new CryptographicException(
                "The update package signature is invalid.");
        }

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(
            _options.SigningPublicKey,
            out _);
        if (!ecdsa.VerifyHash(
                hash,
                signature,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw new CryptographicException(
                "The update package signature could not be verified.");
        }
    }

    private static ApplicationUpdateInfo ToUpdateInfo(
        ReleaseManifest manifest) =>
        new(
            manifest.Version,
            manifest.SizeBytes,
            manifest.PublishedAt,
            manifest.ReleaseNotes);

    private static int CompareVersions(
        string candidate,
        string current)
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
        var prerelease = separator >= 0 &&
                         normalized[separator] == '-'
            ? normalized[(separator + 1)..].Split('+')[0]
            : null;
        if (!Version.TryParse(coreValue, out var core) ||
            core.Major < 0 ||
            core.Minor < 0)
        {
            throw new InvalidDataException(
                $"'{value}' is not a supported release version.");
        }

        return new ParsedVersion(core, prerelease);
    }

    private void EnsureSecureResponse(HttpResponseMessage response)
    {
        var responseUri = response.RequestMessage?.RequestUri;
        if (responseUri is null ||
            !DesktopUpdateOptions.IsSecureEndpoint(responseUri))
        {
            throw new HttpRequestException(
                "The update server redirected to an insecure endpoint.");
        }
    }

    internal static string GetArchiveFileName(Uri downloadUri)
    {
        var fileName = Uri.UnescapeDataString(
            Path.GetFileName(downloadUri.LocalPath));
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.Length > 160 ||
            fileName.Any(character =>
                Path.GetInvalidFileNameChars().Contains(character)) ||
            !ArchiveExtensions.Any(extension =>
                fileName.EndsWith(
                    extension,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "The update manifest does not reference a supported release archive.");
        }

        return fileName;
    }

    private static bool TryDecodeSignature(
        string value,
        out byte[] signature)
    {
        signature = new byte[64];
        return !string.IsNullOrWhiteSpace(value) &&
               Convert.TryFromBase64String(
                   value,
                   signature,
                   out var bytesWritten) &&
               bytesWritten == signature.Length;
    }

    private static bool StartUpdateHelper(DesktopUpdateHandoff handoff)
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{handoff.ScriptPath}\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(handoff.ScriptPath),
                // A read-only install directory needs an administrator token,
                // which Windows asks the user to approve separately.
                Verb = handoff.RequiresElevation ? "runas" : string.Empty
            }
            : new ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = $"\"{handoff.ScriptPath}\"",
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(handoff.ScriptPath)
            };
        try
        {
            return Process.Start(startInfo) is not null;
        }
        catch (System.ComponentModel.Win32Exception exception)
            when (exception.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException(
                "The administrator prompt was dismissed, so the update was not applied.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A failed temporary-file cleanup is harmless and retried next time.
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
        string SigningKeyId,
        string SignatureAlgorithm,
        string Signature,
        string? ReleaseNotes);

    private sealed record RememberedServiceUri(Uri? Value);

    private readonly record struct ParsedVersion(
        Version Core,
        string? Prerelease)
    {
        public bool IsPrerelease =>
            !string.IsNullOrWhiteSpace(Prerelease);
    }

    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(ReleaseManifest))]
    private sealed partial class DesktopUpdateJsonContext :
        JsonSerializerContext;
}
