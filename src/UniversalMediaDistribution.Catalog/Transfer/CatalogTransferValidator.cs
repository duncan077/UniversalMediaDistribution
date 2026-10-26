using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Transfer;

public static class CatalogTransferValidator
{
    public const int MaximumItemCount = 100_000;

    private static readonly HashSet<string> AssetRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "primary",
        "subtitle",
        "supplement",
        "preview"
    };

    private static readonly HashSet<string> SourceLayouts = new(StringComparer.OrdinalIgnoreCase)
    {
        "single_file",
        "image_sequence"
    };

    private static readonly HashSet<string> CredentialQueryParameters =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "access_token",
            "api_key",
            "apikey",
            "auth",
            "authorization",
            "key",
            "passkey",
            "secret",
            "sig",
            "signature",
            "token"
        };

    public static void Validate(CatalogTransferDocument? document) =>
        Validate(document, allowMissingTorrentIdentity: false);

    public static void Validate(
        CatalogTransferDocument? document,
        bool allowMissingTorrentIdentity) =>
        Validate(document, allowMissingTorrentIdentity, false);

    /// <summary>
    /// Validates a catalog document.
    /// </summary>
    /// <param name="allowDuplicateItemNames">
    /// Whether two items whose names normalize alike may both appear.
    /// <para>
    /// A hand-written or third-party catalog listing the same title twice is a
    /// mistake worth reporting. A synchronization snapshot is not: it is merged
    /// from several devices that each identify a title by its own id, so two
    /// spellings of one title reaching the same key is ordinary. The importer
    /// already resolves an incoming item by media type and normalized name and
    /// updates the row it finds, so the pair collapses on the way in — the only
    /// thing this rule achieves there is refusing the whole snapshot.
    /// </para>
    /// </param>
    public static void Validate(
        CatalogTransferDocument? document,
        bool allowMissingTorrentIdentity,
        bool allowDuplicateItemNames)
    {
        var errors = new List<string>();

        if (document is null)
        {
            throw new CatalogTransferValidationException(["The document is empty."]);
        }

        if (!string.Equals(document.Format, CatalogTransferDocument.CurrentFormat, StringComparison.Ordinal))
        {
            errors.Add($"Unsupported catalog format '{document.Format}'.");
        }

        if (document.Version != CatalogTransferDocument.CurrentVersion)
        {
            errors.Add($"Unsupported catalog version '{document.Version}'.");
        }

        if (document.Items is null)
        {
            errors.Add("items is required.");
            throw new CatalogTransferValidationException(errors);
        }

        if (document.Items.Count > MaximumItemCount)
        {
            errors.Add($"Catalog contains more than {MaximumItemCount:N0} items.");
        }

        var itemKeys = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < document.Items.Count; index++)
        {
            var item = document.Items[index];
            if (item is null)
            {
                errors.Add($"items[{index}] cannot be null.");
                continue;
            }

            ValidateItem(
                item,
                index,
                itemKeys,
                errors,
                allowMissingTorrentIdentity,
                allowDuplicateItemNames);
        }

        if (errors.Count > 0)
        {
            throw new CatalogTransferValidationException(errors);
        }
    }

    private static void ValidateItem(
        CatalogTransferItem item,
        int itemIndex,
        HashSet<string> itemKeys,
        List<string> errors,
        bool allowMissingTorrentIdentity,
        bool allowDuplicateItemNames)
    {
        var path = $"items[{itemIndex}]";
        MediaType? mediaType = null;
        MediaSubtypeDefinition? subtype = null;

        if (string.IsNullOrWhiteSpace(item.MediaType))
        {
            errors.Add($"{path}.mediaType is required.");
        }
        else
        {
            try
            {
                mediaType = CatalogSubtypeResolver.ParseMediaType(item.MediaType);
            }
            catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
            {
                errors.Add($"{path}.mediaType: {exception.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(item.Subtype))
        {
            errors.Add($"{path}.subtype is required.");
        }
        else if (mediaType is not null)
        {
            try
            {
                subtype = MediaSubtypeDefinition.GetRequired(CatalogSubtypeResolver.ResolveCode(mediaType.Value, item.Subtype));
            }
            catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
            {
                errors.Add($"{path}.subtype: {exception.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(item.Name))
        {
            errors.Add($"{path}.name is required.");
        }
        else if (mediaType is not null)
        {
            var itemKey = ContentItemKey.Create(mediaType.Value, item.Name, subtype?.Code);
            if (item.SyncVersion is not null)
            {
                try
                {
                    Sync.UserSyncDocumentValidator.ValidateSyncVersion(
                        item.SyncVersion);
                }
                catch (IOException exception)
                {
                    errors.Add($"{path}.syncVersion: {exception.Message}");
                }
            }

            // A tombstone may share its name with a live successor — deleting
            // a title and adding it back is one title twice by name, not a
            // document contradiction.
            if (!item.Deleted &&
                !itemKeys.Add(itemKey) &&
                !allowDuplicateItemNames)
            {
                errors.Add($"{path} duplicates catalog item '{item.Name}' in media type '{mediaType}'.");
            }
        }

        ValidateNonNegative(item.RuntimeSeconds, $"{path}.runtimeSeconds", errors);
        ValidateNonNegative(item.PageCount, $"{path}.pageCount", errors);

        if (item.ReleaseYear is { } releaseYear &&
            !CatalogReleaseYear.IsPlausible(releaseYear))
        {
            errors.Add(
                $"{path}.releaseYear must be between " +
                $"{CatalogReleaseYear.MinimumYear} and " +
                $"{CatalogReleaseYear.MaximumYear}.");
        }

        ValidateGenres(item.Genres, path, errors);
        ValidateCategories(item.Categories, path, mediaType, errors);
        ValidateAlternativeNames(
            CatalogTransferMapper.GetAlternativeNames(item),
            path,
            errors);
        ValidateContributors(item.Contributors, path, errors);
        ValidateRights(item.DistributionRights, path, errors);

        var seasonNumbers = new HashSet<int>();
        var seasons = RequireList(item.Seasons, $"{path}.seasons", errors);
        for (var seasonIndex = 0; seasonIndex < seasons.Count; seasonIndex++)
        {
            var season = seasons[seasonIndex];
            var seasonPath = $"{path}.seasons[{seasonIndex}]";
            if (season is null)
            {
                errors.Add($"{seasonPath} cannot be null.");
                continue;
            }

            if (season.Number < 0)
            {
                errors.Add($"{seasonPath}.number cannot be negative.");
            }

            if (season.SortOrder < 0)
            {
                errors.Add($"{seasonPath}.sortOrder cannot be negative.");
            }

            if (!seasonNumbers.Add(season.Number))
            {
                errors.Add($"{path}.seasons contains duplicate season number {season.Number}.");
            }

            ValidateUnits(
                season.Units,
                seasonPath,
                subtype,
                errors,
                allowMissingTorrentIdentity);
        }

        ValidateUnits(
            item.Units,
            path,
            subtype,
            errors,
            allowMissingTorrentIdentity);
        ValidateSources(
            item.Sources,
            $"{path}.sources",
            errors,
            allowMissingTorrentIdentity);
    }

    private static void ValidateGenres(
        IReadOnlyList<string>? genres,
        string itemPath,
        List<string> errors)
    {
        var values = RequireList(genres, $"{itemPath}.genres", errors);
        var keys = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < values.Count; index++)
        {
            var genre = values[index];
            var path = $"{itemPath}.genres[{index}]";
            if (string.IsNullOrWhiteSpace(genre))
            {
                errors.Add($"{path} is required.");
                continue;
            }

            if (!keys.Add(NameNormalizer.Normalize(genre)))
            {
                errors.Add($"{path} is duplicated.");
            }
        }
    }

    private static void ValidateCategories(
        IReadOnlyList<string>? categories,
        string itemPath,
        MediaType? mediaType,
        List<string> errors)
    {
        var values = RequireList(categories, $"{itemPath}.categories", errors);
        if (mediaType is MediaType.Audio && values.Count > 0)
        {
            errors.Add($"{itemPath}.categories applies only to movie and book items.");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Count; index++)
        {
            var category = values[index];
            var path = $"{itemPath}.categories[{index}]";
            if (string.IsNullOrWhiteSpace(category))
            {
                errors.Add($"{path} is required.");
                continue;
            }

            if (!keys.Add(NameNormalizer.Normalize(category)))
            {
                errors.Add($"{path} is duplicated.");
            }
        }
    }

    private static void ValidateAlternativeNames(
        IReadOnlyList<CatalogTransferAlias>? alternativeNames,
        string itemPath,
        List<string> errors)
    {
        alternativeNames = RequireList(
            alternativeNames,
            $"{itemPath}.alternativeNames",
            errors);
        var keys = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < alternativeNames.Count; index++)
        {
            var alternativeName = alternativeNames[index];
            var path = $"{itemPath}.alternativeNames[{index}]";
            if (alternativeName is null)
            {
                errors.Add($"{path} cannot be null.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(alternativeName.Name))
            {
                errors.Add($"{path}.name is required.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(alternativeName.AliasType))
            {
                errors.Add($"{path}.aliasType is required.");
                continue;
            }

            CatalogAlternativeName normalized;
            try
            {
                normalized = new CatalogAlternativeName(
                    alternativeName.Name,
                    alternativeName.Language,
                    alternativeName.AliasType);
            }
            catch (ArgumentException exception)
            {
                errors.Add($"{path}: {exception.Message}");
                continue;
            }

            var key =
                $"{normalized.NameKey}:{normalized.Language}:{normalized.Kind}";
            if (!keys.Add(key))
            {
                errors.Add($"{path} is duplicated.");
            }
        }
    }

    private static void ValidateContributors(
        IReadOnlyList<CatalogTransferContributor>? contributors,
        string itemPath,
        List<string> errors)
    {
        contributors = RequireList(contributors, $"{itemPath}.contributors", errors);
        var keys = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < contributors.Count; index++)
        {
            var contributor = contributors[index];
            var path = $"{itemPath}.contributors[{index}]";
            if (contributor is null)
            {
                errors.Add($"{path} cannot be null.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(contributor.Name))
            {
                errors.Add($"{path}.name is required.");
                continue;
            }

            if (!string.Equals(contributor.Kind, "person", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(contributor.Kind, "organization", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{path}.kind must be 'person' or 'organization'.");
            }

            if (contributor.Url is not null &&
                !IsSafeHttpUrl(contributor.Url))
            {
                errors.Add(
                    $"{path}.url must be an absolute HTTP or HTTPS URL without embedded credentials.");
            }

            var kind = contributor.Kind?.Trim() ?? string.Empty;
            var key = $"{kind.ToUpperInvariant()}:{NameNormalizer.Normalize(contributor.Name)}";
            if (!keys.Add(key))
            {
                errors.Add($"{path} duplicates contributor '{contributor.Name}'.");
            }

            var credits = RequireList(contributor.Credits, $"{path}.credits", errors);
            for (var creditIndex = 0; creditIndex < credits.Count; creditIndex++)
            {
                var credit = credits[creditIndex];
                if (credit is null)
                {
                    errors.Add($"{path}.credits[{creditIndex}] cannot be null.");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(credit.Role))
                {
                    errors.Add($"{path}.credits[{creditIndex}].role is required.");
                }

                if (credit.SortOrder < 0)
                {
                    errors.Add($"{path}.credits[{creditIndex}].sortOrder cannot be negative.");
                }
            }
        }
    }

    private static void ValidateUnits(
        IReadOnlyList<CatalogTransferUnit>? units,
        string parentPath,
        MediaSubtypeDefinition? subtype,
        List<string> errors,
        bool allowMissingTorrentIdentity)
    {
        units = RequireList(units, $"{parentPath}.units", errors);
        var keys = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < units.Count; index++)
        {
            var unit = units[index];
            var path = $"{parentPath}.units[{index}]";
            if (unit is null)
            {
                errors.Add($"{path} cannot be null.");
                continue;
            }

            ContentUnitKind? kind = null;

            if (string.IsNullOrWhiteSpace(unit.Kind))
            {
                errors.Add($"{path}.kind is required.");
            }
            else
            {
                try
                {
                    kind = CatalogTransferMapper.ParseUnitKind(unit.Kind);
                }
                catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
                {
                    errors.Add($"{path}.kind: {exception.Message}");
                }
            }

            if (string.IsNullOrWhiteSpace(unit.DisplayNumber))
            {
                errors.Add($"{path}.displayNumber is required.");
            }

            if (unit.SortOrder < 0)
            {
                errors.Add($"{path}.sortOrder cannot be negative.");
            }

            ValidateNonNegative(unit.DurationSeconds, $"{path}.durationSeconds", errors);
            ValidateNonNegative(unit.PageCount, $"{path}.pageCount", errors);

            if (kind is not null && !string.IsNullOrWhiteSpace(unit.DisplayNumber))
            {
                var key = $"{kind}:{unit.DisplayNumber.Trim().ToUpperInvariant()}";
                if (!keys.Add(key))
                {
                    errors.Add($"{path} duplicates unit '{unit.Kind} {unit.DisplayNumber}'.");
                }

                if (subtype?.DefaultUnitKind is not null &&
                    subtype.DefaultUnitKind != kind &&
                    !unit.IsSpecial)
                {
                    errors.Add($"{path}.kind '{unit.Kind}' is incompatible with subtype '{subtype.DisplayName}'.");
                }
            }

            ValidateSources(
                unit.Sources,
                $"{path}.sources",
                errors,
                allowMissingTorrentIdentity);
        }
    }

    private static void ValidateRights(
        IReadOnlyList<CatalogTransferRight>? rights,
        string itemPath,
        List<string> errors)
    {
        rights = RequireList(rights, $"{itemPath}.distributionRights", errors);
        for (var index = 0; index < rights.Count; index++)
        {
            var right = rights[index];
            var path = $"{itemPath}.distributionRights[{index}]";
            if (right is null)
            {
                errors.Add($"{path} cannot be null.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(right.SourceName))
            {
                errors.Add($"{path}.sourceName is required.");
            }

            if (!IsAbsoluteUri(right.SourceUri))
            {
                errors.Add($"{path}.sourceUri must be an absolute URI.");
            }

            if (string.IsNullOrWhiteSpace(right.LicenseName))
            {
                errors.Add($"{path}.licenseName is required.");
            }

            if (right.LicenseUri is not null && !IsAbsoluteUri(right.LicenseUri))
            {
                errors.Add($"{path}.licenseUri must be an absolute URI when supplied.");
            }
        }
    }

    private static void ValidateSources(
        IReadOnlyList<CatalogTransferMediaSource>? sources,
        string sourcesPath,
        List<string> errors,
        bool allowMissingTorrentIdentity)
    {
        sources = RequireList(sources, sourcesPath, errors);
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            var path = $"{sourcesPath}[{index}]";
            if (source is null)
            {
                errors.Add($"{path} cannot be null.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(source.AssetRole) || !AssetRoles.Contains(source.AssetRole))
            {
                errors.Add($"{path}.assetRole is unsupported.");
            }

            if (string.IsNullOrWhiteSpace(source.SourceLayout) || !SourceLayouts.Contains(source.SourceLayout))
            {
                errors.Add($"{path}.sourceLayout must be 'single_file' or 'image_sequence'.");
            }

            ValidateNonNegative(source.FileIndex, $"{path}.fileIndex", errors);
            ValidateNonNegative(source.SizeBytes, $"{path}.sizeBytes", errors);

            if (source.SortOrder < 0)
            {
                errors.Add($"{path}.sortOrder cannot be negative.");
            }

            if (string.Equals(source.SourceLayout, "image_sequence", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(source.FilePath))
            {
                errors.Add($"{path}.filePath is required for an image sequence.");
            }

            ValidateTorrent(
                source.Torrent,
                $"{path}.torrent",
                errors,
                allowMissingTorrentIdentity);
        }
    }

    private static void ValidateTorrent(
        CatalogTransferTorrent? torrent,
        string path,
        List<string> errors,
        bool allowMissingTorrentIdentity)
    {
        if (torrent is null)
        {
            errors.Add($"{path} is required.");
            return;
        }

        var hasIdentity = !string.IsNullOrWhiteSpace(torrent.InfoHashV2) ||
                          !string.IsNullOrWhiteSpace(torrent.InfoHashV1) ||
                          !string.IsNullOrWhiteSpace(torrent.MagnetUri);
        var hasMetadata = !string.IsNullOrWhiteSpace(torrent.TorrentMetadataBase64);

        if (!hasIdentity && !hasMetadata && !allowMissingTorrentIdentity)
        {
            errors.Add($"{path} requires an info hash, magnet URI, or torrent metadata.");
        }

        if (torrent.MagnetUri is not null &&
            (!Uri.TryCreate(torrent.MagnetUri, UriKind.Absolute, out var magnet) ||
             !string.Equals(magnet.Scheme, "magnet", StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"{path}.magnetUri is invalid.");
        }

        if (torrent.TorrentMetadataBase64 is not null)
        {
            try
            {
                Convert.FromBase64String(torrent.TorrentMetadataBase64);
            }
            catch (FormatException)
            {
                errors.Add($"{path}.torrentMetadataBase64 is invalid.");
            }
        }

        ValidateNonNegative(torrent.TotalSize, $"{path}.totalSize", errors);
        try
        {
            _ = LanguageTag.Normalize(torrent.Language);
        }
        catch (ArgumentException exception)
        {
            errors.Add($"{path}.language: {exception.Message}");
        }

        var trackers = RequireList(torrent.Trackers, $"{path}.trackers", errors);
        for (var index = 0; index < trackers.Count; index++)
        {
            var tracker = trackers[index];
            if (tracker is null)
            {
                errors.Add($"{path}.trackers[{index}] cannot be null.");
                continue;
            }

            ValidateTracker(
                tracker,
                torrent.IsPrivate,
                $"{path}.trackers[{index}]",
                errors);
        }
    }

    private static void ValidateTracker(
        CatalogTransferTracker tracker,
        bool isPrivateTorrent,
        string path,
        List<string> errors)
    {
        if (!Uri.TryCreate(tracker.Endpoint, UriKind.Absolute, out var endpoint))
        {
            errors.Add($"{path}.endpoint must be an absolute URI.");
            return;
        }

        var expectedScheme = tracker.Transport?.Trim().ToLowerInvariant() switch
        {
            "https" => "https",
            "wss" => "wss",
            "udp" => "udp",
            _ => null
        };

        if (expectedScheme is null)
        {
            errors.Add($"{path}.transport must be 'https', 'wss', or 'udp'.");
        }
        else if (!string.Equals(endpoint.Scheme, expectedScheme, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{path}.endpoint scheme does not match transport '{tracker.Transport}'.");
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo) ||
            HasEmbeddedCredentialQuery(endpoint))
        {
            errors.Add(
                $"{path}.endpoint cannot contain tracker credentials; " +
                "use a local credential provider.");
        }

        if (!string.IsNullOrEmpty(endpoint.Fragment))
        {
            errors.Add($"{path}.endpoint cannot contain a fragment.");
        }

        if (!string.Equals(tracker.ClientFamily, "native", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(tracker.ClientFamily, "browser", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(tracker.ClientFamily, "both", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{path}.clientFamily must be 'native', 'browser', or 'both'.");
        }

        if (isPrivateTorrent && expectedScheme is "udp")
        {
            errors.Add($"{path} cannot use UDP for a private torrent.");
        }

        if (isPrivateTorrent &&
            expectedScheme is "https" &&
            string.Equals(tracker.ClientFamily, "browser", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{path} must use WSS for a private browser tracker.");
        }

        if (isPrivateTorrent &&
            expectedScheme is "wss" &&
            string.Equals(tracker.ClientFamily, "native", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{path} must use HTTPS for a private native tracker.");
        }

        if (!string.IsNullOrWhiteSpace(tracker.CredentialKey) &&
            (!tracker.RequiresAuthentication ||
             !IsValidCredentialKey(tracker.CredentialKey)))
        {
            errors.Add(
                $"{path}.credentialKey must be a valid local lookup key and " +
                "requiresAuthentication must be true.");
        }

        if (tracker.Tier < 0 || tracker.SortOrder < 0)
        {
            errors.Add($"{path}.tier and sortOrder cannot be negative.");
        }
    }

    private static bool IsAbsoluteUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out _);

    private static bool IsSafeHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
        string.IsNullOrEmpty(uri.UserInfo);

    private static bool HasEmbeddedCredentialQuery(Uri endpoint)
    {
        foreach (var segment in endpoint.Query.TrimStart('?')
                     .Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            var encodedKey = separator < 0 ? segment : segment[..separator];
            var encodedValue = separator < 0 ? string.Empty : segment[(separator + 1)..];
            try
            {
                var key = Uri.UnescapeDataString(encodedKey.Replace('+', ' '));
                var value = Uri.UnescapeDataString(encodedValue.Replace('+', ' '));
                if (value.Length > 0 && CredentialQueryParameters.Contains(key))
                {
                    return true;
                }
            }
            catch (UriFormatException)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsValidCredentialKey(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length is > 0 and <= 128 &&
            trimmed.All(static character =>
                character is >= 'a' and <= 'z' or
                    >= 'A' and <= 'Z' or
                    >= '0' and <= '9' or
                    '.' or '_' or '-');
    }

    private static IReadOnlyList<T> RequireList<T>(
        IReadOnlyList<T>? value,
        string path,
        List<string> errors)
    {
        if (value is not null)
        {
            return value;
        }

        errors.Add($"{path} is required.");
        return [];
    }

    private static void ValidateNonNegative(int? value, string path, List<string> errors)
    {
        if (value is < 0)
        {
            errors.Add($"{path} cannot be negative.");
        }
    }

    private static void ValidateNonNegative(long? value, string path, List<string> errors)
    {
        if (value is < 0)
        {
            errors.Add($"{path} cannot be negative.");
        }
    }
}
