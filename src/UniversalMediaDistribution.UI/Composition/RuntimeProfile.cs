using UniversalMediaDistribution.Torrenting;

namespace UniversalMediaDistribution.UI.Composition;

public sealed record RuntimeProfile(
    string PlatformName,
    TorrentEngineCapabilities TorrentCapabilities,
    bool IsTelevision = false,
    bool IsTouchPrimary = false)
{
    public static RuntimeProfile Desktop { get; } = new(
        "Native desktop",
        TorrentEngineCapabilities.MonoTorrentBaseline);

    public static RuntimeProfile Browser { get; } = new(
        "Browser / WebAssembly",
        TorrentEngineCapabilities.WebTorrentBaseline);

    public static RuntimeProfile Android { get; } = new(
        "Android",
        TorrentEngineCapabilities.MonoTorrentBaseline,
        IsTouchPrimary: true);

    public static RuntimeProfile AndroidTv { get; } = new(
        "Android TV",
        TorrentEngineCapabilities.MonoTorrentBaseline,
        IsTelevision: true);

    public static RuntimeProfile Ios { get; } = new(
        "iOS / iPadOS",
        TorrentEngineCapabilities.MonoTorrentBaseline,
        IsTouchPrimary: true);

    /// <summary>
    /// Previews the television or touch shell on the current platform, so the
    /// ten-foot and handset layouts can be checked without the device.
    /// </summary>
    public RuntimeProfile WithShellOverride(string? shell) =>
        shell?.Trim().ToLowerInvariant() switch
        {
            "tv" or "television" => this with
            {
                IsTelevision = true,
                IsTouchPrimary = false
            },
            "touch" or "mobile" => this with
            {
                IsTelevision = false,
                IsTouchPrimary = true
            },
            "desktop" or "pointer" => this with
            {
                IsTelevision = false,
                IsTouchPrimary = false
            },
            _ => this
        };

    public RuntimeProfile WithEnvironmentShellOverride() =>
        WithShellOverride(Environment.GetEnvironmentVariable("UMD_UI_SHELL"));
}
