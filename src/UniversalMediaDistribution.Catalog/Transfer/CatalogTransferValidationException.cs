namespace UniversalMediaDistribution.Catalog.Transfer;

public sealed class CatalogTransferValidationException : Exception
{
    /// <summary>
    /// How many rules the message names before summarising the rest. Enough to
    /// show a pattern — one bad field repeated across items reads the same in
    /// three lines as in three hundred — without turning a status bar into a
    /// wall of text.
    /// </summary>
    private const int DescribedErrorLimit = 3;

    public CatalogTransferValidationException(IEnumerable<string> errors)
        : this(errors as string[] ?? errors.ToArray())
    {
    }

    private CatalogTransferValidationException(string[] errors)
        : base(Describe(errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }

    /// <summary>
    /// Names what actually failed.
    /// <para>
    /// The rules carry the offending path — which item, which source, which
    /// field — and a message that omitted them left a rejected sync or import
    /// saying only that something, somewhere, was wrong. That is the one
    /// question the caller cannot answer on their own.
    /// </para>
    /// </summary>
    private static string Describe(string[] errors)
    {
        if (errors.Length == 0)
        {
            return "The catalog document is invalid.";
        }

        var described = string.Join(
            " ",
            errors.Take(DescribedErrorLimit));
        var remaining = errors.Length - DescribedErrorLimit;
        return remaining > 0
            ? $"The catalog document is invalid: {described} " +
              $"({remaining} more problem(s) not shown.)"
            : $"The catalog document is invalid: {described}";
    }
}
