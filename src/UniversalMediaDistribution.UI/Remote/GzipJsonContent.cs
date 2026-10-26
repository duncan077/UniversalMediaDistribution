using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace UniversalMediaDistribution.UI.Remote;

// Stream the compressed JSON instead of buffering both the large snapshot and
// its compressed copy. Source-generated type information also works in AOT.
internal sealed class GzipJsonContent<T> : HttpContent
{
    private readonly T _value;
    private readonly JsonTypeInfo<T> _typeInfo;

    public GzipJsonContent(T value, JsonTypeInfo<T> typeInfo)
    {
        _value = value;
        _typeInfo = typeInfo;
        Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        Headers.ContentEncoding.Add("gzip");
    }

    protected override bool TryComputeLength(out long length) { length = 0; return false; }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        await using var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true);
        await JsonSerializer.SerializeAsync(gzip, _value, _typeInfo, cancellationToken);
    }
}
