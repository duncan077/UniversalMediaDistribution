using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UniversalMediaDistribution.Catalog.ContentSources;

internal sealed record LeerCapituloDecoderDefinition(
    string DecodedAlphabet,
    string EncodedAlphabet);

/// <summary>
/// Extracts the reader's two substitution alphabets without executing the
/// downloaded JavaScript. The obfuscator rotates a string table at startup, so
/// every possible rotation is tested until both reader expressions resolve to
/// 62-character ASCII alphanumeric permutations.
/// </summary>
internal static class LeerCapituloReaderScriptParser
{
    private static readonly TimeSpan RegexTimeout =
        TimeSpan.FromSeconds(2);
    private static readonly Regex DecoderBootstrapRegex = new(
        """
        function\s+(?<decoder>[$A-Za-z_][$A-Za-z0-9_]*)\s*\([^)]*\)\s*\{\s*
        var\s+(?<tableVariable>[$A-Za-z_][$A-Za-z0-9_]*)\s*=\s*
        (?<tableFunction>[$A-Za-z_][$A-Za-z0-9_]*)\s*\(\s*\)\s*;\s*
        return\s+\k<decoder>\s*=\s*function\s*\(\s*
        (?<index>[$A-Za-z_][$A-Za-z0-9_]*)\s*,[^)]*\)\s*\{\s*
        \k<index>\s*=\s*\k<index>\s*-\s*\((?<offset>[^;]+)\)\s*;
        """,
        RegexOptions.IgnorePatternWhitespace |
        RegexOptions.Singleline |
        RegexOptions.CultureInvariant,
        RegexTimeout);
    public static LeerCapituloDecoderDefinition Parse(string script)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        var bootstrap = DecoderBootstrapRegex.Match(script);
        if (!bootstrap.Success)
        {
            throw new InvalidDataException(
                "The LeerCapitulo reader decoder bootstrap was not found.");
        }

        var tableFunction =
            bootstrap.Groups["tableFunction"].Value;
        var table = ParseStringTable(script, tableFunction);
        if (table.Count == 0)
        {
            throw new InvalidDataException(
                "The LeerCapitulo reader string table is empty.");
        }

        int offset;
        try
        {
            offset = checked((int)new IntegerExpressionParser(
                bootstrap.Groups["offset"].Value).Parse());
        }
        catch (Exception exception)
            when (exception is FormatException or OverflowException or
                  DivideByZeroException)
        {
            throw new InvalidDataException(
                "The LeerCapitulo reader decoder offset is invalid.",
                exception);
        }

        const string replacementMarker = "/[A-Z0-9]/gi";
        var searchStart = 0;
        while (searchStart < script.Length)
        {
            var markerIndex = script.IndexOf(
                replacementMarker,
                searchStart,
                StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                break;
            }

            searchStart = markerIndex + replacementMarker.Length;
            if (!TryExtractAlphabetExpressions(
                    script,
                    searchStart,
                    out var decodedExpression,
                    out var encodedExpression))
            {
                continue;
            }

            for (var rotation = 0;
                 rotation < table.Count;
                 rotation++)
            {
                if (!TryEvaluateConcatenation(
                        decodedExpression,
                        table,
                        offset,
                        rotation,
                        out var decodedAlphabet) ||
                    !TryEvaluateConcatenation(
                        encodedExpression,
                        table,
                        offset,
                        rotation,
                        out var encodedAlphabet) ||
                    !IsAsciiAlphaNumericPermutation(decodedAlphabet) ||
                    !IsAsciiAlphaNumericPermutation(encodedAlphabet))
                {
                    continue;
                }

                return new LeerCapituloDecoderDefinition(
                    decodedAlphabet,
                    encodedAlphabet);
            }
        }

        throw new InvalidDataException(
            "The LeerCapitulo reader substitution alphabets could not be derived.");
    }

    private static IReadOnlyList<string> ParseStringTable(
        string script,
        string functionName)
    {
        var functionPattern =
            $@"function\s+{Regex.Escape(functionName)}\s*" +
            @"\(\s*\)\s*\{\s*var\s+" +
            @"[$A-Za-z_][$A-Za-z0-9_]*\s*=\s*\[";
        var tableStartRegex = new Regex(
            functionPattern,
            RegexOptions.CultureInvariant,
            RegexTimeout);
        var match = tableStartRegex.Match(script);
        if (!match.Success)
        {
            throw new InvalidDataException(
                "The LeerCapitulo reader string table was not found.");
        }

        var position = match.Index + match.Length;
        var values = new List<string>();
        while (position < script.Length)
        {
            SkipWhitespace(script, ref position);
            if (position < script.Length && script[position] == ']')
            {
                return values;
            }

            if (position >= script.Length ||
                script[position] is not ('\'' or '"'))
            {
                throw new InvalidDataException(
                    "The LeerCapitulo reader string table is malformed.");
            }

            values.Add(ReadJavaScriptString(script, ref position));
            SkipWhitespace(script, ref position);
            if (position < script.Length && script[position] == ',')
            {
                position++;
                continue;
            }

            if (position < script.Length && script[position] == ']')
            {
                return values;
            }

            throw new InvalidDataException(
                "The LeerCapitulo reader string table is malformed.");
        }

        throw new InvalidDataException(
            "The LeerCapitulo reader string table is incomplete.");
    }

    private static bool TryExtractAlphabetExpressions(
        string script,
        int searchStart,
        out string decodedExpression,
        out string encodedExpression)
    {
        decodedExpression = string.Empty;
        encodedExpression = string.Empty;
        var arrow = script.IndexOf(
            "=>",
            searchStart,
            StringComparison.Ordinal);
        if (arrow < 0 || arrow - searchStart > 256)
        {
            return false;
        }

        var position = arrow + 2;
        SkipWhitespace(script, ref position);
        if (position >= script.Length || script[position] != '(')
        {
            return false;
        }

        var decodedEnd = FindMatchingParenthesis(script, position);
        if (decodedEnd < 0)
        {
            return false;
        }

        decodedExpression = script[(position + 1)..decodedEnd];
        position = decodedEnd + 1;
        SkipWhitespace(script, ref position);
        if (position >= script.Length || script[position] != '[')
        {
            return false;
        }

        position++;
        SkipWhitespace(script, ref position);
        if (position >= script.Length || script[position] != '(')
        {
            return false;
        }

        var encodedEnd = FindMatchingParenthesis(script, position);
        if (encodedEnd < 0)
        {
            return false;
        }

        encodedExpression = script[(position + 1)..encodedEnd];
        return true;
    }

    private static bool TryEvaluateConcatenation(
        string expression,
        IReadOnlyList<string> table,
        int offset,
        int rotation,
        out string result)
    {
        result = string.Empty;
        var output = new StringBuilder(80);
        var position = 0;
        var needsToken = true;
        while (position < expression.Length)
        {
            SkipWhitespace(expression, ref position);
            if (position >= expression.Length)
            {
                break;
            }

            if (!needsToken)
            {
                if (expression[position] != '+')
                {
                    return false;
                }

                position++;
                needsToken = true;
                continue;
            }

            if (expression[position] is '\'' or '"')
            {
                try
                {
                    output.Append(
                        ReadJavaScriptString(expression, ref position));
                }
                catch (InvalidDataException)
                {
                    return false;
                }
            }
            else
            {
                if (!TryReadIdentifier(expression, ref position))
                {
                    return false;
                }

                SkipWhitespace(expression, ref position);
                if (position >= expression.Length ||
                    expression[position] != '(')
                {
                    return false;
                }

                position++;
                SkipWhitespace(expression, ref position);
                if (position >= expression.Length ||
                    expression[position] is not ('\'' or '"'))
                {
                    return false;
                }

                string rawIndex;
                try
                {
                    rawIndex =
                        ReadJavaScriptString(expression, ref position);
                }
                catch (InvalidDataException)
                {
                    return false;
                }

                SkipWhitespace(expression, ref position);
                if (position >= expression.Length ||
                    expression[position] != ')')
                {
                    return false;
                }

                position++;
                if (!TryParseDecoderIndex(rawIndex, out var decoderIndex))
                {
                    return false;
                }

                var tableIndex = decoderIndex - offset;
                if (tableIndex < 0 || tableIndex >= table.Count)
                {
                    return false;
                }

                output.Append(table[(tableIndex + rotation) % table.Count]);
            }

            if (output.Length > 256)
            {
                return false;
            }

            needsToken = false;
        }

        if (needsToken || output.Length == 0)
        {
            return false;
        }

        result = output.ToString();
        return true;
    }

    private static bool TryParseDecoderIndex(
        string value,
        out int index)
    {
        if (value.StartsWith(
                "0x",
                StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(
                value.AsSpan(2),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out index);
        }

        return int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out index);
    }

    private static bool IsAsciiAlphaNumericPermutation(string value)
    {
        if (value.Length != 62)
        {
            return false;
        }

        Span<bool> seen = stackalloc bool[128];
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) ||
                seen[character])
            {
                return false;
            }

            seen[character] = true;
        }

        return true;
    }

    private static bool TryReadIdentifier(
        string value,
        ref int position)
    {
        if (position >= value.Length ||
            !IsIdentifierStart(value[position]))
        {
            return false;
        }

        position++;
        while (position < value.Length &&
               IsIdentifierPart(value[position]))
        {
            position++;
        }

        return true;
    }

    private static bool IsIdentifierStart(char value) =>
        char.IsAsciiLetter(value) || value is '_' or '$';

    private static bool IsIdentifierPart(char value) =>
        IsIdentifierStart(value) || char.IsAsciiDigit(value);

    private static int FindMatchingParenthesis(
        string value,
        int openPosition)
    {
        var depth = 0;
        for (var position = openPosition;
             position < value.Length;
             position++)
        {
            var character = value[position];
            if (character is '\'' or '"' or '`')
            {
                if (!TrySkipJavaScriptString(
                        value,
                        ref position,
                        character))
                {
                    return -1;
                }

                continue;
            }

            if (character == '/' &&
                position + 1 < value.Length)
            {
                if (value[position + 1] == '/')
                {
                    position += 2;
                    while (position < value.Length &&
                           value[position] is not ('\r' or '\n'))
                    {
                        position++;
                    }

                    continue;
                }

                if (value[position + 1] == '*')
                {
                    var commentEnd = value.IndexOf(
                        "*/",
                        position + 2,
                        StringComparison.Ordinal);
                    if (commentEnd < 0)
                    {
                        return -1;
                    }

                    position = commentEnd + 1;
                    continue;
                }
            }

            if (character == '(')
            {
                depth++;
            }
            else if (character == ')' && --depth == 0)
            {
                return position;
            }
        }

        return -1;
    }

    private static bool TrySkipJavaScriptString(
        string value,
        ref int position,
        char quote)
    {
        for (position++;
             position < value.Length;
             position++)
        {
            if (value[position] == '\\')
            {
                position++;
                continue;
            }

            if (value[position] == quote)
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadJavaScriptString(
        string value,
        ref int position)
    {
        if (position >= value.Length ||
            value[position] is not ('\'' or '"'))
        {
            throw new InvalidDataException(
                "Expected a JavaScript string literal.");
        }

        var quote = value[position++];
        var result = new StringBuilder();
        while (position < value.Length)
        {
            var character = value[position++];
            if (character == quote)
            {
                return result.ToString();
            }

            if (character != '\\')
            {
                result.Append(character);
                continue;
            }

            if (position >= value.Length)
            {
                break;
            }

            var escaped = value[position++];
            switch (escaped)
            {
                case '\r':
                    if (position < value.Length &&
                        value[position] == '\n')
                    {
                        position++;
                    }

                    break;
                case '\n':
                    break;
                case 'b':
                    result.Append('\b');
                    break;
                case 'f':
                    result.Append('\f');
                    break;
                case 'n':
                    result.Append('\n');
                    break;
                case 'r':
                    result.Append('\r');
                    break;
                case 't':
                    result.Append('\t');
                    break;
                case 'v':
                    result.Append('\v');
                    break;
                case '0':
                    result.Append('\0');
                    break;
                case 'x':
                    result.Append(
                        (char)ReadHexDigits(value, ref position, 2));
                    break;
                case 'u':
                    if (position < value.Length &&
                        value[position] == '{')
                    {
                        position++;
                        var closingBrace = value.IndexOf(
                            '}',
                            position);
                        if (closingBrace < 0 ||
                            !int.TryParse(
                                value.AsSpan(
                                    position,
                                    closingBrace - position),
                                NumberStyles.AllowHexSpecifier,
                                CultureInfo.InvariantCulture,
                                out var codePoint) ||
                            !Rune.TryCreate(codePoint, out var rune))
                        {
                            throw new InvalidDataException(
                                "Invalid JavaScript Unicode escape.");
                        }

                        result.Append(rune.ToString());
                        position = closingBrace + 1;
                    }
                    else
                    {
                        result.Append(
                            (char)ReadHexDigits(
                                value,
                                ref position,
                                4));
                    }

                    break;
                default:
                    result.Append(escaped);
                    break;
            }
        }

        throw new InvalidDataException(
            "Unterminated JavaScript string literal.");
    }

    private static int ReadHexDigits(
        string value,
        ref int position,
        int count)
    {
        if (position + count > value.Length ||
            !int.TryParse(
                value.AsSpan(position, count),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var result))
        {
            throw new InvalidDataException(
                "Invalid JavaScript hexadecimal escape.");
        }

        position += count;
        return result;
    }

    private static void SkipWhitespace(
        string value,
        ref int position)
    {
        while (position < value.Length &&
               char.IsWhiteSpace(value[position]))
        {
            position++;
        }
    }

    private sealed class IntegerExpressionParser(string expression)
    {
        private readonly string _expression = expression;
        private int _position;

        public long Parse()
        {
            var result = ParseAddition();
            Skip();
            if (_position != _expression.Length)
            {
                throw new FormatException(
                    "Unexpected JavaScript integer expression token.");
            }

            return result;
        }

        private long ParseAddition()
        {
            var result = ParseMultiplication();
            while (true)
            {
                Skip();
                if (TryConsume('+'))
                {
                    result = checked(result + ParseMultiplication());
                }
                else if (TryConsume('-'))
                {
                    result = checked(result - ParseMultiplication());
                }
                else
                {
                    return result;
                }
            }
        }

        private long ParseMultiplication()
        {
            var result = ParseUnary();
            while (true)
            {
                Skip();
                if (TryConsume('*'))
                {
                    result = checked(result * ParseUnary());
                }
                else if (TryConsume('/'))
                {
                    result /= ParseUnary();
                }
                else if (TryConsume('%'))
                {
                    result %= ParseUnary();
                }
                else
                {
                    return result;
                }
            }
        }

        private long ParseUnary()
        {
            Skip();
            if (TryConsume('+'))
            {
                return ParseUnary();
            }

            if (TryConsume('-'))
            {
                return checked(-ParseUnary());
            }

            if (TryConsume('('))
            {
                var result = ParseAddition();
                Skip();
                if (!TryConsume(')'))
                {
                    throw new FormatException(
                        "Unclosed JavaScript integer expression.");
                }

                return result;
            }

            return ParseNumber();
        }

        private long ParseNumber()
        {
            Skip();
            var start = _position;
            if (_position + 2 <= _expression.Length &&
                _expression.AsSpan(_position).StartsWith(
                    "0x",
                    StringComparison.OrdinalIgnoreCase))
            {
                _position += 2;
                var digitsStart = _position;
                while (_position < _expression.Length &&
                       Uri.IsHexDigit(_expression[_position]))
                {
                    _position++;
                }

                if (_position == digitsStart ||
                    !long.TryParse(
                        _expression.AsSpan(
                            digitsStart,
                            _position - digitsStart),
                        NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture,
                        out var hexadecimal))
                {
                    throw new FormatException(
                        "Invalid hexadecimal JavaScript integer.");
                }

                return hexadecimal;
            }

            while (_position < _expression.Length &&
                   char.IsAsciiDigit(_expression[_position]))
            {
                _position++;
            }

            if (_position == start ||
                !long.TryParse(
                    _expression.AsSpan(start, _position - start),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var number))
            {
                throw new FormatException(
                    "Invalid JavaScript integer.");
            }

            return number;
        }

        private bool TryConsume(char value)
        {
            if (_position >= _expression.Length ||
                _expression[_position] != value)
            {
                return false;
            }

            _position++;
            return true;
        }

        private void Skip()
        {
            while (_position < _expression.Length &&
                   char.IsWhiteSpace(_expression[_position]))
            {
                _position++;
            }
        }
    }
}
