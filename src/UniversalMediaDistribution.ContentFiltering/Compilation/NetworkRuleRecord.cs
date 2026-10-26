using System.Runtime.InteropServices;

namespace UniversalMediaDistribution.ContentFiltering.Compilation;

[Flags]
internal enum RecordFlags : ushort
{
    None = 0,
    Exception = 1 << 0,
    Important = 1 << 1,
    ThirdPartyOnly = 1 << 2,
    FirstPartyOnly = 1 << 3,
    MatchCase = 1 << 4,
    Regex = 1 << 5,
    Redirect = 1 << 6,
    RedirectRule = 1 << 7,

    /// <summary>The pattern was written with <c>||</c>.</summary>
    AnchorHost = 1 << 8,

    /// <summary>The pattern was written with a leading <c>|</c>.</summary>
    AnchorStart = 1 << 9,

    /// <summary>The pattern was written with a trailing <c>|</c>.</summary>
    AnchorEnd = 1 << 10
}

/// <summary>
/// One compiled network rule, sixteen bytes wide and free of references so a
/// hundred thousand of them are two large arrays rather than a hundred
/// thousand objects. The options only a minority of rules carry live in a
/// side table addressed by <see cref="AuxRef"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct NetworkRuleRecord(
    int patternOffset,
    ushort patternLength,
    ushort typeMask,
    ushort methodMask,
    ushort flags,
    int auxRef)
{
    public readonly int PatternOffset = patternOffset;
    public readonly ushort PatternLength = patternLength;
    public readonly ushort TypeMask = typeMask;
    public readonly ushort MethodMask = methodMask;
    public readonly ushort Flags = flags;

    /// <summary>
    /// Index into the auxiliary table, or -1 when this rule carries no
    /// <c>$domain</c>, <c>$to</c>, <c>$denyallow</c>, or <c>$redirect</c>.
    /// </summary>
    public readonly int AuxRef = auxRef;

    public RecordFlags TypedFlags => (RecordFlags)Flags;

    public bool Has(RecordFlags flag) => (Flags & (ushort)flag) != 0;
}

/// <summary>
/// The four optional string references a rule may carry, kept out of
/// <see cref="NetworkRuleRecord"/> so the common case stays small. Eight
/// integers wide.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct NetworkRuleAux(
    int documentDomainsOffset,
    int documentDomainsLength,
    int requestDomainsOffset,
    int requestDomainsLength,
    int denyAllowOffset,
    int denyAllowLength,
    int redirectOffset,
    int redirectLength)
{
    public readonly int DocumentDomainsOffset = documentDomainsOffset;
    public readonly int DocumentDomainsLength = documentDomainsLength;
    public readonly int RequestDomainsOffset = requestDomainsOffset;
    public readonly int RequestDomainsLength = requestDomainsLength;
    public readonly int DenyAllowOffset = denyAllowOffset;
    public readonly int DenyAllowLength = denyAllowLength;
    public readonly int RedirectOffset = redirectOffset;
    public readonly int RedirectLength = redirectLength;
}

/// <summary>
/// One compiled cosmetic rule. Cosmetics are resolved once per navigation
/// rather than once per request, so this record trades a little width for
/// clarity.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct CosmeticRecord(
    int bodyOffset,
    int bodyLength,
    int domainsOffset,
    int domainsLength,
    int flags)
{
    public readonly int BodyOffset = bodyOffset;
    public readonly int BodyLength = bodyLength;
    public readonly int DomainsOffset = domainsOffset;
    public readonly int DomainsLength = domainsLength;
    public readonly int Flags = flags;

    public bool IsScriptlet => (Flags & (int)CosmeticFlags.Scriptlet) != 0;

    public bool IsException => (Flags & (int)CosmeticFlags.Exception) != 0;

    public bool IsProcedural => (Flags & (int)CosmeticFlags.Procedural) != 0;
}

[Flags]
internal enum CosmeticFlags
{
    None = 0,
    Scriptlet = 1 << 0,
    Exception = 1 << 1,
    Procedural = 1 << 2
}
