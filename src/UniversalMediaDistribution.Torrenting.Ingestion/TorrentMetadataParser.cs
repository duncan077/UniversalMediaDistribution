using System.Security.Cryptography;
using System.Text;
using UniversalMediaDistribution.Torrenting;

namespace UniversalMediaDistribution.Torrenting.Ingestion;

public sealed class TorrentMetadataFormatException : ArgumentException
{
    public TorrentMetadataFormatException(
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed record TorrentMetadataSummary(
    string? InfoHashV1,
    string? InfoHashV2,
    string? DisplayName,
    bool IsPrivate,
    IReadOnlyList<Uri> TrackerUris)
{
    public MagnetLink CreateIdentityMagnet()
    {
        var parameters = new List<string>(3);
        if (InfoHashV1 is not null)
        {
            parameters.Add("xt=urn:btih:" + InfoHashV1);
        }

        if (InfoHashV2 is not null)
        {
            parameters.Add("xt=urn:btmh:1220" + InfoHashV2);
        }

        if (!string.IsNullOrWhiteSpace(DisplayName))
        {
            parameters.Add("dn=" + Uri.EscapeDataString(DisplayName));
        }

        return MagnetLink.Parse("magnet:?" + string.Join('&', parameters));
    }

    public IReadOnlyList<TrackerEndpoint> CreateTrackerEndpoints(
        TorrentEngineCapabilities capabilities,
        bool privateMode)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var result = new List<TrackerEndpoint>();
        for (var index = 0; index < TrackerUris.Count; index++)
        {
            var uri = TrackerUris[index];
            var transport = uri.Scheme.ToLowerInvariant() switch
            {
                "https" => TrackerTransport.Https,
                "wss" => TrackerTransport.Wss,
                "udp" => TrackerTransport.Udp,
                _ => (TrackerTransport?)null
            };

            if (transport is null ||
                !capabilities.TrackerTransports.Contains(transport.Value) ||
                privateMode && transport is TrackerTransport.Udp)
            {
                continue;
            }

            result.Add(new TrackerEndpoint(
                uri,
                transport.Value,
                tier: 0,
                sortOrder: result.Count));
        }

        return result;
    }
}

public static class TorrentMetadataParser
{
    public const int MaximumMetadataBytes = 16 * 1024 * 1024;

    public static TorrentMetadataSummary Parse(ReadOnlyMemory<byte> metadata)
    {
        if (metadata.IsEmpty)
        {
            throw new TorrentMetadataFormatException(
                "The torrent file is empty.");
        }

        if (metadata.Length > MaximumMetadataBytes)
        {
            throw new TorrentMetadataFormatException(
                $"The torrent file exceeds the {MaximumMetadataBytes / 1024 / 1024} MiB limit.");
        }

        return new Parser(metadata).Parse();
    }

    private sealed class Parser(ReadOnlyMemory<byte> metadata)
    {
        private const int MaximumDepth = 128;
        private const int MaximumValues = 250_000;
        private const int MaximumTrackerCandidates = 256;
        private const int MaximumNameBytes = 4 * 1024;

        private readonly List<Uri> _trackers = [];
        private readonly HashSet<string> _trackerKeys =
            new(StringComparer.OrdinalIgnoreCase);
        private int _offset;
        private int _valueCount;
        private int _trackerCandidateCount;

        private ReadOnlySpan<byte> Bytes => metadata.Span;

        public TorrentMetadataSummary Parse()
        {
            if (Bytes[0] != (byte)'d')
            {
                throw Invalid("Torrent metadata must be a bencoded dictionary.");
            }

            _offset = 1;
            InfoFields? info = null;
            var infoStart = -1;
            var infoEnd = -1;
            while (!AtEndMarker())
            {
                var key = ReadByteString();
                if (KeyEquals(key, "info"u8))
                {
                    if (info is not null)
                    {
                        throw Invalid("Torrent metadata contains more than one info dictionary.");
                    }

                    infoStart = _offset;
                    info = ReadInfoDictionary();
                    infoEnd = _offset;
                }
                else if (KeyEquals(key, "announce"u8))
                {
                    ReadTrackerValue();
                }
                else if (KeyEquals(key, "announce-list"u8))
                {
                    ReadTrackerCollection(depth: 1);
                }
                else
                {
                    SkipValue(depth: 1);
                }
            }

            _offset++;
            if (_offset != Bytes.Length)
            {
                throw Invalid("Torrent metadata has trailing data.");
            }

            if (info is null || infoStart < 0 || infoEnd <= infoStart)
            {
                throw Invalid("Torrent metadata does not contain an info dictionary.");
            }

            var infoBytes = Bytes[infoStart..infoEnd];
            string? infoHashV1;
            string? infoHashV2;
            if (info.MetaVersion is 2)
            {
                infoHashV2 = Convert.ToHexString(SHA256.HashData(infoBytes));
                infoHashV1 = info.HasV1Pieces
                    ? Convert.ToHexString(SHA1.HashData(infoBytes))
                    : null;
            }
            else if (info.MetaVersion is null)
            {
                infoHashV1 = Convert.ToHexString(SHA1.HashData(infoBytes));
                infoHashV2 = null;
            }
            else
            {
                throw Invalid(
                    $"Torrent metadata version {info.MetaVersion} is not supported.");
            }

            return new TorrentMetadataSummary(
                infoHashV1,
                infoHashV2,
                info.DisplayName,
                info.IsPrivate,
                _trackers.ToArray());
        }

        private InfoFields ReadInfoDictionary()
        {
            CountValue();
            RequireMarker('d');
            _offset++;

            string? name = null;
            string? utf8Name = null;
            long? metaVersion = null;
            var isPrivate = false;
            var hasPrivate = false;
            var hasMetaVersion = false;
            var hasV1Pieces = false;

            while (!AtEndMarker())
            {
                var key = ReadByteString();
                if (KeyEquals(key, "name.utf-8"u8))
                {
                    utf8Name = ReadDisplayName();
                }
                else if (KeyEquals(key, "name"u8))
                {
                    name = ReadDisplayName();
                }
                else if (KeyEquals(key, "private"u8))
                {
                    if (hasPrivate)
                    {
                        throw Invalid(
                            "Torrent info contains more than one private flag.");
                    }

                    hasPrivate = true;
                    var privateValue = ReadInteger();
                    if (privateValue is not 0 and not 1)
                    {
                        throw Invalid(
                            "The torrent private flag must be zero or one.");
                    }

                    isPrivate = privateValue is 1;
                }
                else if (KeyEquals(key, "meta version"u8))
                {
                    if (hasMetaVersion)
                    {
                        throw Invalid(
                            "Torrent info contains more than one metadata version.");
                    }

                    hasMetaVersion = true;
                    metaVersion = ReadInteger();
                }
                else if (KeyEquals(key, "pieces"u8))
                {
                    hasV1Pieces = true;
                    SkipValue(depth: 2);
                }
                else
                {
                    SkipValue(depth: 2);
                }
            }

            _offset++;
            return new InfoFields(
                utf8Name ?? name,
                isPrivate,
                metaVersion,
                hasV1Pieces);
        }

        private string? ReadDisplayName()
        {
            if (!IsByteStringMarker(CurrentMarker()))
            {
                SkipValue(depth: 2);
                return null;
            }

            var value = ReadByteString();
            if (value.Length > MaximumNameBytes)
            {
                throw Invalid(
                    $"Torrent display name exceeds {MaximumNameBytes:N0} bytes.");
            }

            var text = Encoding.UTF8.GetString(
                Bytes.Slice(value.Start, value.Length)).Trim();
            return text.Length == 0 ? null : text;
        }

        private void ReadTrackerValue()
        {
            if (!IsByteStringMarker(CurrentMarker()))
            {
                SkipValue(depth: 1);
                return;
            }

            AddTracker(ReadByteString());
        }

        private void ReadTrackerCollection(int depth)
        {
            if (depth > MaximumDepth)
            {
                throw Invalid("Torrent metadata nesting is too deep.");
            }

            CountValue();
            if (CurrentMarker() != (byte)'l')
            {
                SkipValueAfterCount(depth);
                return;
            }

            _offset++;
            while (!AtEndMarker())
            {
                if (IsByteStringMarker(CurrentMarker()))
                {
                    AddTracker(ReadByteString());
                }
                else if (CurrentMarker() == (byte)'l')
                {
                    ReadTrackerCollection(depth + 1);
                }
                else
                {
                    SkipValue(depth + 1);
                }
            }

            _offset++;
        }

        private void AddTracker(ByteRange value)
        {
            _trackerCandidateCount++;
            if (_trackerCandidateCount > MaximumTrackerCandidates)
            {
                throw Invalid("Torrent metadata contains too many tracker entries.");
            }

            var trackerText = Encoding.UTF8.GetString(
                Bytes.Slice(value.Start, value.Length)).Trim();
            if (!Uri.TryCreate(trackerText, UriKind.Absolute, out var tracker) ||
                !_trackerKeys.Add(tracker.AbsoluteUri))
            {
                return;
            }

            if (_trackers.Count >= MagnetLink.MaximumTrackers)
            {
                throw Invalid(
                    $"Torrent metadata contains more than {MagnetLink.MaximumTrackers} unique trackers.");
            }

            _trackers.Add(tracker);
        }

        private void SkipValue(int depth)
        {
            CountValue();
            SkipValueAfterCount(depth);
        }

        private void SkipValueAfterCount(int depth)
        {
            if (depth > MaximumDepth)
            {
                throw Invalid("Torrent metadata nesting is too deep.");
            }

            var marker = CurrentMarker();
            if (IsByteStringMarker(marker))
            {
                _ = ReadByteString();
                return;
            }

            if (marker == (byte)'i')
            {
                _ = ReadInteger();
                return;
            }

            if (marker is not ((byte)'l') and not ((byte)'d'))
            {
                throw Invalid("Torrent metadata contains invalid bencoding.");
            }

            var isDictionary = marker == (byte)'d';
            _offset++;
            while (!AtEndMarker())
            {
                if (isDictionary)
                {
                    _ = ReadByteString();
                }

                SkipValue(depth + 1);
            }

            _offset++;
        }

        private long ReadInteger()
        {
            CountValue();
            RequireMarker('i');
            _offset++;
            var negative = false;
            if (CurrentMarker() == (byte)'-')
            {
                negative = true;
                _offset++;
            }

            var digitStart = _offset;
            while (_offset < Bytes.Length &&
                   Bytes[_offset] is >= (byte)'0' and <= (byte)'9')
            {
                _offset++;
            }

            if (_offset == digitStart ||
                _offset >= Bytes.Length ||
                Bytes[_offset] != (byte)'e' ||
                Bytes[digitStart] == (byte)'0' && _offset - digitStart > 1 ||
                negative && Bytes[digitStart] == (byte)'0')
            {
                throw Invalid("Torrent metadata contains an invalid integer.");
            }

            long value = 0;
            try
            {
                for (var index = digitStart; index < _offset; index++)
                {
                    value = checked(
                        value * 10 + Bytes[index] - (byte)'0');
                }

                if (negative)
                {
                    value = checked(-value);
                }
            }
            catch (OverflowException exception)
            {
                throw Invalid(
                    "Torrent metadata contains an oversized integer.",
                    exception);
            }

            _offset++;
            return value;
        }

        private ByteRange ReadByteString()
        {
            CountValue();
            var lengthStart = _offset;
            while (_offset < Bytes.Length &&
                   Bytes[_offset] is >= (byte)'0' and <= (byte)'9')
            {
                _offset++;
            }

            if (_offset == lengthStart ||
                _offset >= Bytes.Length ||
                Bytes[_offset] != (byte)':' ||
                Bytes[lengthStart] == (byte)'0' && _offset - lengthStart > 1)
            {
                throw Invalid("Torrent metadata contains an invalid byte string.");
            }

            var length = 0;
            try
            {
                for (var index = lengthStart; index < _offset; index++)
                {
                    length = checked(
                        length * 10 + Bytes[index] - (byte)'0');
                }
            }
            catch (OverflowException exception)
            {
                throw Invalid(
                    "Torrent metadata contains an oversized byte string.",
                    exception);
            }

            var start = ++_offset;
            if (length > Bytes.Length - start)
            {
                throw Invalid("Torrent metadata contains a truncated byte string.");
            }

            _offset += length;
            return new ByteRange(start, length);
        }

        private bool AtEndMarker()
        {
            if (_offset >= Bytes.Length)
            {
                throw Invalid("Torrent metadata ended unexpectedly.");
            }

            return Bytes[_offset] == (byte)'e';
        }

        private byte CurrentMarker()
        {
            if (_offset >= Bytes.Length)
            {
                throw Invalid("Torrent metadata ended unexpectedly.");
            }

            return Bytes[_offset];
        }

        private void RequireMarker(char marker)
        {
            if (CurrentMarker() != (byte)marker)
            {
                throw Invalid("Torrent metadata contains invalid bencoding.");
            }
        }

        private bool KeyEquals(ByteRange key, ReadOnlySpan<byte> expected) =>
            Bytes.Slice(key.Start, key.Length).SequenceEqual(expected);

        private void CountValue()
        {
            _valueCount++;
            if (_valueCount > MaximumValues)
            {
                throw Invalid("Torrent metadata contains too many values.");
            }
        }

        private static bool IsByteStringMarker(byte marker) =>
            marker is >= (byte)'0' and <= (byte)'9';

        private static TorrentMetadataFormatException Invalid(
            string message,
            Exception? innerException = null) =>
            new(message, innerException);

        private readonly record struct ByteRange(int Start, int Length);

        private sealed record InfoFields(
            string? DisplayName,
            bool IsPrivate,
            long? MetaVersion,
            bool HasV1Pieces);
    }
}
