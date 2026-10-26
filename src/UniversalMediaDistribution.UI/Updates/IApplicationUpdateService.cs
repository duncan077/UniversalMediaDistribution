namespace UniversalMediaDistribution.UI.Updates;

public sealed record ApplicationUpdateInfo(
    string Version,
    long SizeBytes,
    DateTimeOffset PublishedAt,
    string? ReleaseNotes);

/// <summary>
/// How a platform applies an update the user has agreed to.
/// </summary>
public enum ApplicationUpdateInstallKind
{
    /// <summary>
    /// The application files are replaced in place and the app restarts.
    /// </summary>
    ReplaceAndRestart,

    /// <summary>
    /// The operating system installs the package and asks the user to confirm.
    /// </summary>
    SystemInstaller
}

public enum ApplicationUpdateOutcome
{
    /// <summary>
    /// The new version is staged and verified. The application must exit now so
    /// its files can be replaced; the update helper restarts it afterwards.
    /// </summary>
    RestartRequired,

    /// <summary>
    /// The package was handed to the operating system installer, which is asking
    /// the user to confirm.
    /// </summary>
    InstallerRequested,

    /// <summary>
    /// The user has to grant an operating system permission before the package
    /// can be installed. The relevant settings screen was opened.
    /// </summary>
    PermissionRequired
}

public sealed record ApplicationUpdateApplyResult(
    string PackagePath,
    ApplicationUpdateOutcome Outcome);

/// <summary>
/// Updates are always opt-in: nothing is downloaded, replaced, or installed
/// until the user asks for it from the update prompt.
/// </summary>
public interface IApplicationUpdateService
{
    bool IsAvailable { get; }

    ApplicationUpdateInstallKind InstallKind { get; }

    /// <summary>
    /// Remembers the user-configured server URL for this process. This keeps
    /// update discovery independent of a second database read after Settings
    /// has already loaded or the user has entered a URL.
    /// </summary>
    void RememberServiceUrl(string? serviceUrl);

    ValueTask<ApplicationUpdateInfo?> CheckForUpdateAsync(
        CancellationToken cancellationToken = default);

    ValueTask<ApplicationUpdateApplyResult> DownloadAndApplyAsync(
        ApplicationUpdateInfo update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableApplicationUpdateService :
    IApplicationUpdateService
{
    public static UnavailableApplicationUpdateService Instance { get; } =
        new();

    private UnavailableApplicationUpdateService()
    {
    }

    public bool IsAvailable => false;

    public ApplicationUpdateInstallKind InstallKind =>
        ApplicationUpdateInstallKind.ReplaceAndRestart;

    public void RememberServiceUrl(string? serviceUrl)
    {
        // There is no updater to configure on this platform.
    }

    public ValueTask<ApplicationUpdateInfo?> CheckForUpdateAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ApplicationUpdateInfo?>(null);
    }

    public ValueTask<ApplicationUpdateApplyResult> DownloadAndApplyAsync(
        ApplicationUpdateInfo update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Application updates are unavailable on this platform.");
}
