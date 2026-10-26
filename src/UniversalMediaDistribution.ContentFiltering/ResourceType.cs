namespace UniversalMediaDistribution.ContentFiltering;

/// <summary>
/// The kind of resource a request is fetching, matching the type options a
/// filter list can carry (<c>$script</c>, <c>$image</c>, and the rest).
/// </summary>
[Flags]
public enum ResourceType
{
    None = 0,

    /// <summary>A top-level document navigation.</summary>
    Document = 1 << 0,

    /// <summary>A nested frame document.</summary>
    SubDocument = 1 << 1,

    Script = 1 << 2,
    Image = 1 << 3,
    StyleSheet = 1 << 4,
    XmlHttpRequest = 1 << 5,
    Media = 1 << 6,
    Font = 1 << 7,
    WebSocket = 1 << 8,
    Ping = 1 << 9,
    Other = 1 << 10,

    All =
        Document |
        SubDocument |
        Script |
        Image |
        StyleSheet |
        XmlHttpRequest |
        Media |
        Font |
        WebSocket |
        Ping |
        Other,

    /// <summary>
    /// What a filter carrying no type option applies to. Top-level documents
    /// are excluded because a list that blocks <c>||ads.example^</c> means the
    /// resources it serves, not every navigation a viewer makes to it.
    /// </summary>
    Default = All & ~Document
}
