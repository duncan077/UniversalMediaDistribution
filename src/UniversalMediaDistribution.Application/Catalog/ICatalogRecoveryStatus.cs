namespace UniversalMediaDistribution.Application.Catalog;

public sealed record CatalogRecoveryNotice(
    DateTimeOffset RecoveredAt,
    string RecoveryDirectory,
    string Reason);

public interface ICatalogRecoveryStatus
{
    CatalogRecoveryNotice? LastRecovery { get; }
}
