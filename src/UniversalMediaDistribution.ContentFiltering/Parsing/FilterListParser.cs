using System.Globalization;

namespace UniversalMediaDistribution.ContentFiltering.Parsing;

/// <summary>
/// Reads Adblock Plus / uBlock Origin filter syntax.
///
/// Two rules govern the whole file. First, a bad line is never fatal: filter
/// lists are third-party text that changes without notice, and losing one rule
/// must never cost the other hundred thousand. Second, a rule this engine
/// cannot fully enforce is dropped rather than approximated — a filter that
/// half-applies breaks pages in ways nobody can diagnose, and a page that
/// breaks here is a video that does not play.
/// </summary>
public static class FilterListParser
{
    private const int MaxOptionCount = 32;

    public static FilterListParseResult Parse(
        string text,
        FilterListEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Parse(text.AsSpan(), environment);
    }

    public static FilterListParseResult Parse(
        ReadOnlySpan<char> text,
        FilterListEnvironment? environment = null)
    {
        var result = new FilterListParseResult();
        var host = environment ?? FilterListEnvironment.Desktop;
        var conditions = new Stack<ConditionFrame>();
        var active = true;

        foreach (var rawLine in text.EnumerateLines())
        {
            var line = rawLine.Trim();
            result.TotalLines++;
            if (line.IsEmpty)
            {
                continue;
            }

            if (line[0] == '[')
            {
                // "[Adblock Plus 2.0]" and friends: a format banner.
                continue;
            }

            if (line[0] == '!')
            {
                HandleComment(line, host, result, conditions, ref active);
                continue;
            }

            if (!active)
            {
                continue;
            }

            switch (ClassifyCosmetic(line, out var separator, out var length))
            {
                case CosmeticForm.None:
                    ReadNetworkRule(line, result);
                    continue;

                case CosmeticForm.Unsupported:
                    result.UnsupportedLines++;
                    continue;

                default:
                    ReadCosmeticRule(line, separator, length, result);
                    continue;
            }
        }

        return result;
    }

    private static void HandleComment(
        ReadOnlySpan<char> line,
        FilterListEnvironment host,
        FilterListParseResult result,
        Stack<ConditionFrame> conditions,
        ref bool active)
    {
        if (line.StartsWith("!#if ", StringComparison.OrdinalIgnoreCase))
        {
            var condition =
                EvaluateCondition(line["!#if ".Length..].Trim(), host);
            conditions.Push(new ConditionFrame(active, condition));
            active = active && condition;
            return;
        }

        if (line.StartsWith("!#else", StringComparison.OrdinalIgnoreCase))
        {
            if (conditions.Count > 0)
            {
                var frame = conditions.Peek();
                active = frame.ParentActive && !frame.Condition;
            }

            return;
        }

        if (line.StartsWith("!#endif", StringComparison.OrdinalIgnoreCase))
        {
            active = conditions.Count > 0
                ? conditions.Pop().ParentActive
                : true;
            return;
        }

        if (line.StartsWith("!#include ", StringComparison.OrdinalIgnoreCase))
        {
            if (active)
            {
                result.Includes.Add(
                    line["!#include ".Length..].Trim().ToString());
            }

            return;
        }

        if (!active)
        {
            return;
        }

        var body = line.TrimStart('!').TrimStart();
        if (body.StartsWith("Title:", StringComparison.OrdinalIgnoreCase))
        {
            result.Title ??= body["Title:".Length..].Trim().ToString();
            return;
        }

        if (body.StartsWith("Expires:", StringComparison.OrdinalIgnoreCase) &&
            TryReadExpires(body["Expires:".Length..].Trim(), out var expires))
        {
            result.Expires ??= expires;
        }
    }

    private static bool TryReadExpires(
        ReadOnlySpan<char> value,
        out TimeSpan expires)
    {
        expires = default;

        // "4 days", "12 hours", and "4 days (update frequency)" all occur.
        var digits = 0;
        while (digits < value.Length && char.IsAsciiDigit(value[digits]))
        {
            digits++;
        }

        if (digits == 0 ||
            !int.TryParse(
                value[..digits],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var amount) ||
            amount <= 0)
        {
            return false;
        }

        var unit = value[digits..].TrimStart();
        expires = unit.StartsWith("h", StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromHours(amount)
            : TimeSpan.FromDays(amount);
        return true;
    }

    private static bool EvaluateCondition(
        ReadOnlySpan<char> condition,
        FilterListEnvironment host)
    {
        var negated = false;
        while (!condition.IsEmpty && condition[0] == '!')
        {
            negated = !negated;
            condition = condition[1..].TrimStart();
        }

        // Compound expressions are rare and always guard capabilities rather
        // than whole lists, so treating them as satisfied is the safe default.
        var value = condition switch
        {
            _ when Is(condition, "ext_ublock") => true,
            _ when Is(condition, "env_chromium") => true,
            _ when Is(condition, "env_mobile") => host.IsMobile,
            _ when Is(condition, "env_firefox") => false,
            _ when Is(condition, "env_safari") => false,
            _ when Is(condition, "env_legacy") => false,
            _ when Is(condition, "cap_html_filtering") => false,
            _ when condition.StartsWith(
                "adguard",
                StringComparison.OrdinalIgnoreCase) => false,
            _ => true
        };

        return negated ? !value : value;
    }

    private static bool Is(ReadOnlySpan<char> value, string name) =>
        value.Equals(name, StringComparison.OrdinalIgnoreCase);

    private enum CosmeticForm
    {
        None,
        Selector,
        SelectorException,
        Procedural,
        ProceduralException,
        Unsupported
    }

    /// <summary>
    /// Decides whether a line is a cosmetic filter, and which kind. The prefix
    /// before the separator has to look like a domain list, which is what keeps
    /// a network pattern carrying a URL fragment from being read as one.
    /// </summary>
    private static CosmeticForm ClassifyCosmetic(
        ReadOnlySpan<char> line,
        out int separator,
        out int separatorLength)
    {
        separator = -1;
        separatorLength = 0;

        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];
            if (current != '#')
            {
                if (!IsDomainListChar(current))
                {
                    return CosmeticForm.None;
                }

                continue;
            }

            var rest = line[index..];
            separator = index;
            if (rest.StartsWith("##", StringComparison.Ordinal))
            {
                separatorLength = 2;
                return CosmeticForm.Selector;
            }

            if (rest.StartsWith("#@#", StringComparison.Ordinal))
            {
                separatorLength = 3;
                return CosmeticForm.SelectorException;
            }

            if (rest.StartsWith("#?#", StringComparison.Ordinal))
            {
                separatorLength = 3;
                return CosmeticForm.Procedural;
            }

            if (rest.StartsWith("#@?#", StringComparison.Ordinal))
            {
                separatorLength = 4;
                return CosmeticForm.ProceduralException;
            }

            // "#$#", "#%#", "#@$#" and the rest: CSS declaration injection and
            // AdGuard-only forms this engine does not implement.
            return CosmeticForm.Unsupported;
        }

        return CosmeticForm.None;
    }

    private static bool IsDomainListChar(char value) =>
        char.IsAsciiLetterOrDigit(value) ||
        value is '.' or '-' or '_' or ',' or '~' or '*' ||
        value > 127;

    private static void ReadCosmeticRule(
        ReadOnlySpan<char> line,
        int separator,
        int separatorLength,
        FilterListParseResult result)
    {
        var domains = line[..separator].Trim();
        var body = line[(separator + separatorLength)..].Trim();
        if (body.IsEmpty)
        {
            result.MalformedLines++;
            return;
        }

        var isException = separatorLength is 3 or 4 &&
            line[separator + 1] == '@';
        var isProcedural = separatorLength is 3 or 4 &&
            line[separator + separatorLength - 2] == '?';

        if (body.StartsWith("+js(", StringComparison.Ordinal))
        {
            if (body[^1] != ')')
            {
                result.MalformedLines++;
                return;
            }

            result.CosmeticRules.Add(new ParsedCosmeticRule
            {
                Body = body[4..^1].Trim().ToString(),
                Kind = CosmeticRuleKind.Scriptlet,
                Domains = domains.IsEmpty ? null : domains.ToString(),
                IsException = isException
            });
            return;
        }

        result.CosmeticRules.Add(new ParsedCosmeticRule
        {
            Body = body.ToString(),
            Kind = CosmeticRuleKind.Selector,
            Domains = domains.IsEmpty ? null : domains.ToString(),
            IsException = isException,
            IsProcedural = isProcedural || UsesProceduralSyntax(body)
        });
    }

    private static bool UsesProceduralSyntax(ReadOnlySpan<char> selector) =>
        selector.Contains(":has(", StringComparison.Ordinal) ||
        selector.Contains(":has-text(", StringComparison.Ordinal) ||
        selector.Contains(":matches-css", StringComparison.Ordinal) ||
        selector.Contains(":matches-attr(", StringComparison.Ordinal) ||
        selector.Contains(":matches-path(", StringComparison.Ordinal) ||
        selector.Contains(":upward(", StringComparison.Ordinal) ||
        selector.Contains(":xpath(", StringComparison.Ordinal) ||
        selector.Contains(":remove(", StringComparison.Ordinal) ||
        selector.Contains(":style(", StringComparison.Ordinal) ||
        selector.Contains(":others(", StringComparison.Ordinal) ||
        selector.Contains(":watch-attr(", StringComparison.Ordinal) ||
        selector.Contains(":min-text-length(", StringComparison.Ordinal);

    private static void ReadNetworkRule(
        ReadOnlySpan<char> line,
        FilterListParseResult result)
    {
        var flags = NetworkRuleFlags.None;
        var body = line;
        if (body.StartsWith("@@", StringComparison.Ordinal))
        {
            flags |= NetworkRuleFlags.Exception;
            body = body[2..];
        }

        var pattern = body;
        var options = ReadOnlySpan<char>.Empty;
        var optionStart = FindOptionSeparator(body);
        if (optionStart >= 0)
        {
            pattern = body[..optionStart];
            options = body[(optionStart + 1)..];
        }

        pattern = pattern.Trim();
        if (pattern.IsEmpty || pattern.SequenceEqual("*"))
        {
            // A rule with nothing to match on would sit in the bucket that is
            // evaluated for every single request, and would match all of them.
            result.UnsupportedLines++;
            return;
        }

        var isRegex = pattern.Length > 2 &&
            pattern[0] == '/' &&
            pattern[^1] == '/';
        if (isRegex)
        {
            flags |= NetworkRuleFlags.Regex;
            pattern = pattern[1..^1];
            if (pattern.IsEmpty)
            {
                result.MalformedLines++;
                return;
            }
        }

        var rule = new ParsedNetworkRule
        {
            Pattern = pattern.ToString(),
            Flags = flags
        };

        if (!options.IsEmpty)
        {
            var outcome = ApplyOptions(options, ref rule);
            if (outcome != OptionOutcome.Accepted)
            {
                if (outcome == OptionOutcome.Unsupported)
                {
                    result.UnsupportedLines++;
                }
                else
                {
                    result.MalformedLines++;
                }

                return;
            }
        }

        result.NetworkRules.Add(rule);
    }

    /// <summary>
    /// Locates the <c>$</c> that begins the option list. A dollar sign inside a
    /// regular expression or a query string is not one, so the candidate has to
    /// be followed by something that could only be options.
    /// </summary>
    private static int FindOptionSeparator(ReadOnlySpan<char> body)
    {
        for (var index = body.Length - 1; index >= 0; index--)
        {
            if (body[index] != '$' || index > 0 && body[index - 1] == '\\')
            {
                continue;
            }

            if (LooksLikeOptions(body[(index + 1)..]))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool LooksLikeOptions(ReadOnlySpan<char> value)
    {
        // Option names may begin with a digit — "$3p" and "$1p" are the
        // shorthand for third- and first-party, and uBlock Origin's own lists
        // use them. Rejecting a leading digit silently folds the options into
        // the pattern, which produces a rule that can never match anything.
        if (value.IsEmpty ||
            !char.IsAsciiLetterOrDigit(value[0]) && value[0] != '~')
        {
            return false;
        }

        foreach (var current in value)
        {
            if (char.IsWhiteSpace(current))
            {
                return false;
            }
        }

        return true;
    }

    private enum OptionOutcome
    {
        Accepted,
        Malformed,
        Unsupported
    }

    private static OptionOutcome ApplyOptions(
        ReadOnlySpan<char> options,
        ref ParsedNetworkRule rule)
    {
        var positiveTypes = ResourceType.None;
        var negativeTypes = ResourceType.None;
        var flags = rule.Flags;
        var methods = RequestMethod.None;
        string? documentDomains = null;
        string? requestDomains = null;
        string? denyAllowDomains = null;
        string? redirectResource = null;
        var count = 0;

        foreach (var range in SplitOptions(options))
        {
            if (++count > MaxOptionCount)
            {
                return OptionOutcome.Malformed;
            }

            var option = options[range].Trim();
            if (option.IsEmpty)
            {
                return OptionOutcome.Malformed;
            }

            var negated = option[0] == '~';
            if (negated)
            {
                option = option[1..];
            }

            var separator = option.IndexOf('=');
            var name = separator < 0 ? option : option[..separator];
            var value = separator < 0
                ? ReadOnlySpan<char>.Empty
                : option[(separator + 1)..];

            var type = MapResourceType(name);
            if (type != ResourceType.None)
            {
                if (negated)
                {
                    negativeTypes |= type;
                }
                else
                {
                    positiveTypes |= type;
                }

                continue;
            }

            if (Is(name, "third-party") || Is(name, "3p"))
            {
                flags |= negated
                    ? NetworkRuleFlags.FirstPartyOnly
                    : NetworkRuleFlags.ThirdPartyOnly;
            }
            else if (Is(name, "first-party") || Is(name, "1p"))
            {
                flags |= negated
                    ? NetworkRuleFlags.ThirdPartyOnly
                    : NetworkRuleFlags.FirstPartyOnly;
            }
            else if (Is(name, "important"))
            {
                flags |= NetworkRuleFlags.Important;
            }
            else if (Is(name, "badfilter"))
            {
                flags |= NetworkRuleFlags.BadFilter;
            }
            else if (Is(name, "match-case"))
            {
                flags |= NetworkRuleFlags.MatchCase;
            }
            else if (Is(name, "domain") || Is(name, "from"))
            {
                if (value.IsEmpty)
                {
                    return OptionOutcome.Malformed;
                }

                documentDomains = value.ToString();
            }
            else if (Is(name, "to"))
            {
                if (value.IsEmpty)
                {
                    return OptionOutcome.Malformed;
                }

                requestDomains = value.ToString();
            }
            else if (Is(name, "denyallow"))
            {
                if (value.IsEmpty)
                {
                    return OptionOutcome.Malformed;
                }

                denyAllowDomains = value.ToString();
            }
            else if (Is(name, "method"))
            {
                if (!TryReadMethods(value, out methods))
                {
                    return OptionOutcome.Malformed;
                }
            }
            else if (Is(name, "redirect") || Is(name, "redirect-rule"))
            {
                if (value.IsEmpty)
                {
                    return OptionOutcome.Malformed;
                }

                flags |= NetworkRuleFlags.Redirect;
                if (Is(name, "redirect-rule"))
                {
                    flags |= NetworkRuleFlags.RedirectRule;
                }

                redirectResource = value.ToString();
            }
            else
            {
                // $csp, $removeparam, $header, $popup, $elemhide and the rest
                // need enforcement this engine does not have yet.
                return OptionOutcome.Unsupported;
            }
        }

        var types = positiveTypes != ResourceType.None
            ? positiveTypes
            : ResourceType.Default & ~negativeTypes;
        if (types == ResourceType.None)
        {
            return OptionOutcome.Malformed;
        }

        if ((flags & NetworkRuleFlags.ThirdPartyOnly) != 0 &&
            (flags & NetworkRuleFlags.FirstPartyOnly) != 0)
        {
            return OptionOutcome.Malformed;
        }

        rule = rule with
        {
            Types = types,
            Flags = flags,
            Methods = methods,
            DocumentDomains = documentDomains,
            RequestDomains = requestDomains,
            DenyAllowDomains = denyAllowDomains,
            RedirectResource = redirectResource
        };
        return OptionOutcome.Accepted;
    }

    private static bool TryReadMethods(
        ReadOnlySpan<char> value,
        out RequestMethod methods)
    {
        methods = RequestMethod.None;
        if (value.IsEmpty)
        {
            return false;
        }

        var positive = RequestMethod.None;
        var negative = RequestMethod.None;
        foreach (var range in value.Split('|'))
        {
            var entry = value[range].Trim();
            if (entry.IsEmpty)
            {
                return false;
            }

            if (entry[0] == '~')
            {
                negative |= RequestMethods.Parse(entry[1..]);
            }
            else
            {
                positive |= RequestMethods.Parse(entry);
            }
        }

        methods = positive != RequestMethod.None
            ? positive
            : RequestMethod.All & ~negative;
        return methods != RequestMethod.None;
    }

    private static ResourceType MapResourceType(ReadOnlySpan<char> name) =>
        name switch
        {
            _ when Is(name, "script") => ResourceType.Script,
            _ when Is(name, "image") => ResourceType.Image,
            _ when Is(name, "stylesheet") || Is(name, "css") =>
                ResourceType.StyleSheet,
            _ when Is(name, "xmlhttprequest") || Is(name, "xhr") =>
                ResourceType.XmlHttpRequest,
            _ when Is(name, "subdocument") || Is(name, "frame") =>
                ResourceType.SubDocument,
            _ when Is(name, "media") => ResourceType.Media,
            _ when Is(name, "font") => ResourceType.Font,
            _ when Is(name, "websocket") => ResourceType.WebSocket,
            _ when Is(name, "ping") || Is(name, "beacon") => ResourceType.Ping,
            _ when Is(name, "other") => ResourceType.Other,
            _ when Is(name, "object") || Is(name, "object-subrequest") =>
                ResourceType.Other,
            _ when Is(name, "document") || Is(name, "doc") =>
                ResourceType.Document,
            _ when Is(name, "all") => ResourceType.All,
            _ => ResourceType.None
        };

    /// <summary>
    /// Splits an option list on commas, leaving alone the ones inside a
    /// <c>/regular expression/</c> value or escaped with a backslash.
    /// </summary>
    private static List<Range> SplitOptions(ReadOnlySpan<char> options)
    {
        var ranges = new List<Range>();
        var start = 0;
        var inRegex = false;
        for (var index = 0; index < options.Length; index++)
        {
            var current = options[index];
            if (current == '\\')
            {
                index++;
                continue;
            }

            if (current == '/')
            {
                inRegex = !inRegex;
                continue;
            }

            if (current == ',' && !inRegex)
            {
                ranges.Add(new Range(start, index));
                start = index + 1;
            }
        }

        ranges.Add(new Range(start, options.Length));
        return ranges;
    }

    private readonly record struct ConditionFrame(
        bool ParentActive,
        bool Condition);
}
