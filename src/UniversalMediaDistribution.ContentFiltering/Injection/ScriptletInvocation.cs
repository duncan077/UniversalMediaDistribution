namespace UniversalMediaDistribution.ContentFiltering.Injection;

/// <summary>
/// A parsed <c>+js(name, arg, ...)</c> body.
/// </summary>
public sealed record ScriptletInvocation(string Name, IReadOnlyList<string> Arguments)
{
    /// <summary>
    /// Splits a scriptlet body on commas, honouring backslash escapes and
    /// quoted arguments. Filter authors write both <c>'a, b'</c> and
    /// <c>a\, b</c> to mean an argument containing a comma.
    /// </summary>
    public static bool TryParse(string body, out ScriptletInvocation invocation)
    {
        invocation = null!;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        var parts = Split(body);
        if (parts.Count == 0 || parts[0].Length == 0)
        {
            return false;
        }

        invocation = new ScriptletInvocation(
            parts[0],
            parts.GetRange(1, parts.Count - 1));
        return true;
    }

    private static List<string> Split(string body)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';
        for (var index = 0; index < body.Length; index++)
        {
            var value = body[index];
            if (value == '\\' && index + 1 < body.Length)
            {
                current.Append(body[++index]);
                continue;
            }

            if (quote != '\0')
            {
                if (value == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(value);
                }

                continue;
            }

            if (value is '\'' or '"')
            {
                quote = value;
                continue;
            }

            if (value == ',')
            {
                parts.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(value);
        }

        parts.Add(current.ToString().Trim());
        return parts;
    }
}
