namespace UniversalMediaDistribution.ContentFiltering;

/// <summary>
/// Finds the registrable domain of a host, which is what decides whether a
/// request is third-party.
///
/// The authoritative answer is the Public Suffix List, and that list is
/// fetched alongside the filter lists rather than baked into the binary. Until
/// one arrives, <see cref="Default"/> covers the multi-label suffixes that
/// actually occur in practice. Getting this wrong is not catastrophic: it only
/// mis-classifies first versus third party for rules that constrain on it.
/// </summary>
public sealed class PublicSuffixIndex
{
    private readonly HashSet<string> _suffixes;
    private readonly HashSet<string> _exceptions;

    private PublicSuffixIndex(
        HashSet<string> suffixes,
        HashSet<string> exceptions)
    {
        _suffixes = suffixes;
        _exceptions = exceptions;
        _lookup = _suffixes.GetAlternateLookup<ReadOnlySpan<char>>();
        _exceptionLookup = _exceptions.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>>
        _exceptionLookup;

    /// <summary>
    /// The built-in fallback: multi-label suffixes common enough that treating
    /// them as registrable domains would put unrelated sites on the same one.
    /// </summary>
    public static PublicSuffixIndex Default { get; } = new(
        new HashSet<string>(
            [
                "ac.uk", "co.uk", "gov.uk", "ltd.uk", "me.uk", "net.uk",
                "org.uk", "plc.uk", "sch.uk",
                "com.ar", "com.au", "com.bo", "com.br", "com.cn", "com.co",
                "com.cu", "com.do", "com.ec", "com.eg", "com.es", "com.gt",
                "com.hk", "com.hn", "com.mx", "com.my", "com.ng", "com.ni",
                "com.pa", "com.pe", "com.ph", "com.pk", "com.pl", "com.pr",
                "com.py", "com.sg", "com.sv", "com.tr", "com.tw", "com.ua",
                "com.uy", "com.ve", "com.vn",
                "net.au", "net.br", "net.cn", "net.mx", "net.nz", "net.tr",
                "org.au", "org.br", "org.cn", "org.es", "org.mx", "org.nz",
                "org.tr",
                "co.at", "co.id", "co.il", "co.in", "co.jp", "co.kr", "co.nz",
                "co.th", "co.za",
                "or.jp", "ne.jp", "gr.jp", "ac.jp",
                "edu.au", "edu.br", "edu.cn", "edu.mx", "gov.br", "gov.cn",
                "gob.mx", "gob.ar", "gob.pe"
            ],
            StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));

    /// <summary>
    /// Reads a Public Suffix List. Wildcard rules are stored by their literal
    /// tail, which is enough for the two-level lookup below.
    /// </summary>
    public static PublicSuffixIndex Load(string publicSuffixList)
    {
        ArgumentNullException.ThrowIfNull(publicSuffixList);
        var suffixes = new HashSet<string>(StringComparer.Ordinal);
        var exceptions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in publicSuffixList.AsSpan().EnumerateLines())
        {
            var line = rawLine.Trim();
            if (line.IsEmpty ||
                line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (line[0] == '!')
            {
                exceptions.Add(line[1..].ToString().ToLowerInvariant());
                continue;
            }

            if (line[0] == '*' && line.Length > 2 && line[1] == '.')
            {
                line = line[2..];
            }

            suffixes.Add(line.ToString().ToLowerInvariant());
        }

        return suffixes.Count == 0
            ? Default
            : new PublicSuffixIndex(suffixes, exceptions);
    }

    /// <summary>
    /// Returns the registrable part of <paramref name="host"/> — the public
    /// suffix plus one more label. Returns the whole host when it has no more
    /// labels to give.
    /// </summary>
    public ReadOnlySpan<char> RegistrableDomain(ReadOnlySpan<char> host)
    {
        host = host.TrimEnd('.');
        if (host.IsEmpty)
        {
            return host;
        }

        var lastDot = host.LastIndexOf('.');
        if (lastDot < 0)
        {
            return host;
        }

        var secondLastDot = host[..lastDot].LastIndexOf('.');
        if (secondLastDot < 0)
        {
            return host;
        }

        var twoLabels = host[(secondLastDot + 1)..];
        if (!_lookup.Contains(twoLabels) || _exceptionLookup.Contains(twoLabels))
        {
            return twoLabels;
        }

        var thirdLastDot = host[..secondLastDot].LastIndexOf('.');
        return thirdLastDot < 0 ? host : host[(thirdLastDot + 1)..];
    }

    public bool SameSite(ReadOnlySpan<char> first, ReadOnlySpan<char> second) =>
        !first.IsEmpty &&
        !second.IsEmpty &&
        RegistrableDomain(first).Equals(
            RegistrableDomain(second),
            StringComparison.OrdinalIgnoreCase);
}
