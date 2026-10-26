namespace UniversalMediaDistribution.ContentFiltering;

/// <summary>
/// HTTP methods a <c>$method=</c> option can name. Anything unrecognized is
/// <see cref="Other"/> so an exotic method still participates in matching
/// instead of silently matching nothing.
/// </summary>
[Flags]
public enum RequestMethod
{
    None = 0,
    Get = 1 << 0,
    Head = 1 << 1,
    Post = 1 << 2,
    Put = 1 << 3,
    Delete = 1 << 4,
    Options = 1 << 5,
    Patch = 1 << 6,
    Connect = 1 << 7,
    Trace = 1 << 8,
    Other = 1 << 9,

    All =
        Get |
        Head |
        Post |
        Put |
        Delete |
        Options |
        Patch |
        Connect |
        Trace |
        Other
}

public static class RequestMethods
{
    /// <summary>
    /// Maps a method name to its flag without allocating. Returns
    /// <see cref="RequestMethod.None"/> for an empty name, which callers treat
    /// as "unknown, do not constrain".
    /// </summary>
    public static RequestMethod Parse(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty)
        {
            return RequestMethod.None;
        }

        return name switch
        {
            _ when Is(name, "GET") => RequestMethod.Get,
            _ when Is(name, "HEAD") => RequestMethod.Head,
            _ when Is(name, "POST") => RequestMethod.Post,
            _ when Is(name, "PUT") => RequestMethod.Put,
            _ when Is(name, "DELETE") => RequestMethod.Delete,
            _ when Is(name, "OPTIONS") => RequestMethod.Options,
            _ when Is(name, "PATCH") => RequestMethod.Patch,
            _ when Is(name, "CONNECT") => RequestMethod.Connect,
            _ when Is(name, "TRACE") => RequestMethod.Trace,
            _ => RequestMethod.Other
        };
    }

    private static bool Is(ReadOnlySpan<char> value, string name) =>
        value.Equals(name, StringComparison.OrdinalIgnoreCase);
}
