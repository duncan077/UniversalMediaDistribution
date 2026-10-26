namespace UniversalMediaDistribution.Application.UserState;

/// <summary>
/// One person using this installation.
/// <para>
/// A profile owns what someone accumulates — what they starred, how far they
/// got, their lists, their reader settings and the account they sync with — and
/// owns none of the library. Two people sharing a device share every title and
/// disagree only about what they have done with them.
/// </para>
/// </summary>
public sealed record UserProfile(
    Guid Id,
    string Name,
    string? AccentKey,
    int SortOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt);

/// <summary>
/// Whose state the per-person stores currently read and write. Setting it is
/// how the application switches profiles: every store answers from the new
/// profile on its next call, with nothing to rebuild.
/// </summary>
public interface IActiveProfileAccessor
{
    Guid ActiveProfileId { get; set; }
}

public interface IUserProfileStore
{
    ValueTask<IReadOnlyList<UserProfile>> ListAsync(
        CancellationToken cancellationToken = default);

    ValueTask<UserProfile> CreateAsync(
        string name,
        string? accentKey = null,
        CancellationToken cancellationToken = default);

    ValueTask<UserProfile> RenameAsync(
        Guid profileId,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a profile and everything it owns. The library is untouched.
    /// </summary>
    ValueTask DeleteAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that this profile is the one being used, so the picker can offer
    /// it first next time.
    /// </summary>
    ValueTask MarkUsedAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);
}
