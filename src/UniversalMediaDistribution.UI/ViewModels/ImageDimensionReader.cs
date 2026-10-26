using Avalonia;

namespace UniversalMediaDistribution.UI.ViewModels;

/// <summary>
/// Reads an image's pixel dimensions out of its header, without decoding it.
/// <para>
/// A reader page has to reserve a box before it has a picture to put in it, and
/// a box that turns out to be the wrong height moves everything below it the
/// moment the picture arrives. Every format here states its dimensions in the
/// first few dozen bytes, so the box can be right from the start for the cost of
/// one small read instead of a full decode.
/// </para>
/// </summary>
internal static class ImageDimensionReader
{
    /// <summary>
    /// Enough for the header of every format handled here. A JPEG can carry
    /// large EXIF or ICC segments before its frame header, which is what makes
    /// this larger than the others would need.
    /// </summary>
    public const int MaximumHeaderBytes = 128 * 1024;

    public static PixelSize? TryRead(ReadOnlySpan<byte> header)
    {
        if (TryReadPng(header) is { } png)
        {
            return png;
        }

        if (TryReadGif(header) is { } gif)
        {
            return gif;
        }

        if (TryReadWebp(header) is { } webp)
        {
            return webp;
        }

        if (TryReadBmp(header) is { } bmp)
        {
            return bmp;
        }

        return TryReadJpeg(header);
    }

    private static PixelSize? TryReadBmp(ReadOnlySpan<byte> header)
    {
        if (header.Length < 26 || !header[..2].SequenceEqual("BM"u8)) return null;
        var dibSize = ReadLittleEndian32(header.Slice(14, 4));
        if (dibSize == 12) // OS/2 BITMAPCOREHEADER stores unsigned 16-bit dimensions.
            return Create(ReadLittleEndian16(header.Slice(18, 2)),
                ReadLittleEndian16(header.Slice(20, 2)));
        if (dibSize is not (40 or 52 or 56 or 108 or 124) || header.Length < 54)
            return null;
        var width = unchecked((int)ReadLittleEndian32(header.Slice(18, 4)));
        var height = unchecked((int)ReadLittleEndian32(header.Slice(22, 4)));
        // Negative height means a top-down bitmap; MinValue cannot be negated.
        if (height == int.MinValue) return null;
        return Create(width, Math.Abs(height));
    }

    private static PixelSize? TryReadPng(ReadOnlySpan<byte> header)
    {
        // 8-byte signature, then a length and "IHDR", then width and height as
        // big-endian 32-bit values.
        ReadOnlySpan<byte> signature =
            [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        if (header.Length < 24 ||
            !header[..8].SequenceEqual(signature) ||
            !header.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return null;
        }

        return Create(
            ReadBigEndian32(header.Slice(16, 4)),
            ReadBigEndian32(header.Slice(20, 4)));
    }

    private static PixelSize? TryReadGif(ReadOnlySpan<byte> header)
    {
        if (header.Length < 10 ||
            (!header[..6].SequenceEqual("GIF87a"u8) &&
             !header[..6].SequenceEqual("GIF89a"u8)))
        {
            return null;
        }

        return Create(
            ReadLittleEndian16(header.Slice(6, 2)),
            ReadLittleEndian16(header.Slice(8, 2)));
    }

    private static PixelSize? TryReadWebp(ReadOnlySpan<byte> header)
    {
        if (header.Length < 30 ||
            !header[..4].SequenceEqual("RIFF"u8) ||
            !header.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return null;
        }

        var chunk = header.Slice(12, 4);

        // Lossy: dimensions are 14 bytes into the VP8 bitstream, as two
        // 14-bit values.
        if (chunk.SequenceEqual("VP8 "u8))
        {
            return Create(
                ReadLittleEndian16(header.Slice(26, 2)) & 0x3FFF,
                ReadLittleEndian16(header.Slice(28, 2)) & 0x3FFF);
        }

        // Lossless: 14 bits each, packed across four bytes after the signature
        // byte, and stored one less than the real size.
        if (chunk.SequenceEqual("VP8L"u8))
        {
            var bits = ReadLittleEndian32(header.Slice(21, 4));
            return Create(
                (int)(bits & 0x3FFF) + 1,
                (int)((bits >> 14) & 0x3FFF) + 1);
        }

        // Extended: 24-bit values, also stored one less than the real size.
        if (chunk.SequenceEqual("VP8X"u8))
        {
            return Create(
                ReadLittleEndian24(header.Slice(24, 3)) + 1,
                ReadLittleEndian24(header.Slice(27, 3)) + 1);
        }

        return null;
    }

    /// <summary>
    /// Walks the JPEG segment chain to the frame header, which is the only
    /// place the size is stated. Everything before it is metadata of some kind
    /// and is skipped by its declared length.
    /// </summary>
    private static PixelSize? TryReadJpeg(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4 || header[0] != 0xFF || header[1] != 0xD8)
        {
            return null;
        }

        var position = 2;
        while (position + 4 <= header.Length)
        {
            if (header[position] != 0xFF)
            {
                // Padding between segments is legal; step over it.
                position++;
                continue;
            }

            var marker = header[position + 1];
            position += 2;

            // Standalone markers carry no length payload.
            if (marker is 0x01 or 0xD8 or >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            // Start of scan: the entropy-coded data begins and no frame header
            // follows, so there is nothing left to find.
            if (marker == 0xDA || marker == 0xD9)
            {
                return null;
            }

            if (position + 2 > header.Length)
            {
                return null;
            }

            var length = ReadBigEndian16(header.Slice(position, 2));
            if (length < 2)
            {
                return null;
            }

            // Start of frame, in any of its forms. The four excluded values in
            // this range are not frame headers: 0xC4 is a Huffman table, 0xC8 is
            // reserved, 0xCC is an arithmetic-coding table, and 0xD* was handled
            // above.
            if (marker is >= 0xC0 and <= 0xCF &&
                marker is not (0xC4 or 0xC8 or 0xCC))
            {
                if (position + 7 > header.Length)
                {
                    return null;
                }

                return Create(
                    ReadBigEndian16(header.Slice(position + 5, 2)),
                    ReadBigEndian16(header.Slice(position + 3, 2)));
            }

            position += length;
        }

        return null;
    }

    private static PixelSize? Create(int width, int height) =>
        width > 0 && height > 0 ? new PixelSize(width, height) : null;

    private static int ReadBigEndian16(ReadOnlySpan<byte> value) =>
        (value[0] << 8) | value[1];

    private static int ReadBigEndian32(ReadOnlySpan<byte> value) =>
        (value[0] << 24) | (value[1] << 16) | (value[2] << 8) | value[3];

    private static int ReadLittleEndian16(ReadOnlySpan<byte> value) =>
        value[0] | (value[1] << 8);

    private static int ReadLittleEndian24(ReadOnlySpan<byte> value) =>
        value[0] | (value[1] << 8) | (value[2] << 16);

    private static uint ReadLittleEndian32(ReadOnlySpan<byte> value) =>
        (uint)(value[0] |
               (value[1] << 8) |
               (value[2] << 16) |
               (value[3] << 24));
}
