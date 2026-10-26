namespace UniversalMediaDistribution.Torrenting.MonoTorrent;

public enum MonoTorrentEncryptionPolicy
{
    Preferred = 1,
    Required = 2,
    Disabled = 3
}

public enum MonoTorrentFastResumePolicy
{
    BestEffort = 1,
    Accurate = 2
}

public sealed record MonoTorrentEngineOptions
{
    public static MonoTorrentEngineOptions Default { get; } = new();

    public MonoTorrentEncryptionPolicy EncryptionPolicy { get; init; } =
        MonoTorrentEncryptionPolicy.Preferred;

    public MonoTorrentFastResumePolicy FastResumePolicy { get; init; } =
        MonoTorrentFastResumePolicy.BestEffort;

    public static MonoTorrentEngineOptions Parse(
        string? encryptionPolicy,
        string? fastResumePolicy)
    {
        return new MonoTorrentEngineOptions
        {
            EncryptionPolicy = ParseEncryptionPolicy(encryptionPolicy),
            FastResumePolicy = ParseFastResumePolicy(fastResumePolicy)
        };
    }

    internal void Validate()
    {
        if (!Enum.IsDefined(EncryptionPolicy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(EncryptionPolicy),
                EncryptionPolicy,
                "The MonoTorrent encryption policy is invalid.");
        }

        if (!Enum.IsDefined(FastResumePolicy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(FastResumePolicy),
                FastResumePolicy,
                "The MonoTorrent fast-resume policy is invalid.");
        }
    }

    private static MonoTorrentEncryptionPolicy ParseEncryptionPolicy(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "preferred" => MonoTorrentEncryptionPolicy.Preferred,
            "required" => MonoTorrentEncryptionPolicy.Required,
            "disabled" => MonoTorrentEncryptionPolicy.Disabled,
            _ => throw new ArgumentException(
                "UMD_MSE_POLICY must be 'preferred', 'required', or 'disabled'.",
                nameof(value))
        };

    private static MonoTorrentFastResumePolicy ParseFastResumePolicy(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "best-effort" => MonoTorrentFastResumePolicy.BestEffort,
            "accurate" => MonoTorrentFastResumePolicy.Accurate,
            _ => throw new ArgumentException(
                "UMD_FAST_RESUME_POLICY must be 'best-effort' or 'accurate'.",
                nameof(value))
        };
}
