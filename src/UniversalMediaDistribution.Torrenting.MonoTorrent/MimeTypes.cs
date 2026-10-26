namespace UniversalMediaDistribution.Torrenting.MonoTorrent;

internal static class MimeTypes
{
    public static string? FromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".avif" => "image/avif",
        ".cbz" => "application/vnd.comicbook+zip",
        ".epub" => "application/epub+zip",
        ".flac" => "audio/flac",
        ".jpeg" or ".jpg" => "image/jpeg",
        ".json" => "application/json",
        ".m4a" => "audio/mp4",
        ".m4v" or ".mp4" => "video/mp4",
        ".mkv" => "video/x-matroska",
        ".mp3" => "audio/mpeg",
        ".ogg" or ".oga" => "audio/ogg",
        ".ogv" => "video/ogg",
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".wav" => "audio/wav",
        ".webm" => "video/webm",
        ".webp" => "image/webp",
        _ => null
    };
}
