using System.Security.Cryptography;
using System.Text;

namespace UniversalMediaDistribution.Torrenting.Ingestion.Tests;

public sealed class TorrentMetadataParserTests
{
    [Fact]
    public void Parse_ReadsV1IdentityNamePrivacyAndTrackers()
    {
        var torrent = TorrentMetadataTestData.CreateV1(
            "Open Release",
            "https://tracker.example.test/announce",
            isPrivate: true);

        var metadata = TorrentMetadataParser.Parse(torrent.Metadata);

        Assert.Equal(
            Convert.ToHexString(SHA1.HashData(torrent.Info)),
            metadata.InfoHashV1);
        Assert.Null(metadata.InfoHashV2);
        Assert.Equal("Open Release", metadata.DisplayName);
        Assert.True(metadata.IsPrivate);
        Assert.Equal(
            "https://tracker.example.test/announce",
            Assert.Single(metadata.TrackerUris).AbsoluteUri);
        Assert.StartsWith(
            "magnet:?xt=urn:btih:",
            metadata.CreateIdentityMagnet().CatalogIdentityUri,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ReadsPureV2Identity()
    {
        var torrent = TorrentMetadataTestData.CreateV2("V2 Release");

        var metadata = TorrentMetadataParser.Parse(torrent.Metadata);

        Assert.Null(metadata.InfoHashV1);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(torrent.Info)),
            metadata.InfoHashV2);
        Assert.Equal("V2 Release", metadata.DisplayName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-bencoded")]
    [InlineData("d4:infodegarbage")]
    public void Parse_RejectsMalformedMetadata(string value)
    {
        Assert.Throws<TorrentMetadataFormatException>(
            () => TorrentMetadataParser.Parse(
                Encoding.UTF8.GetBytes(value)));
    }
}

internal static class TorrentMetadataTestData
{
    public static TestTorrent CreateV1(
        string name,
        string? tracker = null,
        bool isPrivate = false)
    {
        var infoEntries = new List<(string Key, byte[] Value)>
        {
            ("length", Integer(1)),
            ("name", ByteString(name)),
            ("piece length", Integer(16_384)),
            ("pieces", ByteString(
                Encoding.ASCII.GetBytes("01234567890123456789")))
        };
        if (isPrivate)
        {
            infoEntries.Add(("private", Integer(1)));
        }

        var info = Dictionary(infoEntries);
        var rootEntries = new List<(string Key, byte[] Value)>
        {
            ("info", info)
        };
        if (tracker is not null)
        {
            rootEntries.Add(("announce", ByteString(tracker)));
        }

        return new TestTorrent(Dictionary(rootEntries), info);
    }

    public static TestTorrent CreateV2(string name)
    {
        var info = Dictionary(
        [
            ("file tree", Dictionary([])),
            ("meta version", Integer(2)),
            ("name", ByteString(name)),
            ("piece length", Integer(16_384))
        ]);
        return new TestTorrent(
            Dictionary([("info", info)]),
            info);
    }

    private static byte[] Dictionary(
        IReadOnlyList<(string Key, byte[] Value)> entries)
    {
        using var stream = new MemoryStream();
        stream.WriteByte((byte)'d');
        foreach (var entry in entries.OrderBy(
                     static entry => entry.Key,
                     StringComparer.Ordinal))
        {
            stream.Write(ByteString(entry.Key));
            stream.Write(entry.Value);
        }

        stream.WriteByte((byte)'e');
        return stream.ToArray();
    }

    private static byte[] Integer(long value) =>
        Encoding.ASCII.GetBytes($"i{value}e");

    private static byte[] ByteString(string value) =>
        ByteString(Encoding.UTF8.GetBytes(value));

    private static byte[] ByteString(byte[] value)
    {
        var prefix = Encoding.ASCII.GetBytes($"{value.Length}:");
        var result = new byte[prefix.Length + value.Length];
        prefix.CopyTo(result, 0);
        value.CopyTo(result, prefix.Length);
        return result;
    }
}

internal sealed record TestTorrent(byte[] Metadata, byte[] Info);
