namespace UniversalMediaDistribution.Domain.Catalog;

public enum PublicationStatus
{
    Unknown = 0,
    Announced = 1,
    Ongoing = 2,
    Completed = 3,
    Cancelled = 4,
    Paused = 5
}

public static class PublicationStatusApplicability
{
    public static bool AppliesTo(MediaType mediaType, string subtypeCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subtypeCode);

        return (mediaType is MediaType.Book &&
                string.Equals(
                    subtypeCode,
                    "book.manga",
                    StringComparison.OrdinalIgnoreCase)) ||
            (mediaType is MediaType.Movie &&
             (string.Equals(
                  subtypeCode,
                  "movie.series",
                  StringComparison.OrdinalIgnoreCase) ||
              MediaSubtypeGroups.IsAnime(subtypeCode)));
    }
}
