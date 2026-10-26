using Avalonia;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// The reader reserves each page's box from these numbers before anything is
/// decoded, so a wrong answer moves the page someone is reading and a missing
/// one costs the whole point of measuring.
/// </summary>
public sealed class ImageDimensionReaderTests
{
    [Theory]
    [InlineData(40, 600)]
    [InlineData(40, -600)]
    [InlineData(52, 600)]
    [InlineData(56, 600)]
    [InlineData(108, -600)]
    [InlineData(124, 600)]
    public void BmpInfoHeadersSupportBottomUpAndTopDownImages(int dibSize, int height)
    {
        var header = CreateBmpHeader(dibSize, 800, height);
        Assert.Equal(new PixelSize(800, 600), ImageDimensionReader.TryRead(header));
    }

    [Fact]
    public void BmpCoreHeaderUsesUnsignedShortDimensions()
    {
        var header = CreateBmpHeader(12, 0, 0);
        BitConverter.GetBytes((ushort)800).CopyTo(header, 18);
        BitConverter.GetBytes((ushort)600).CopyTo(header, 20);
        Assert.Equal(new PixelSize(800, 600), ImageDimensionReader.TryRead(header.AsSpan(0, 26)));
    }

    [Theory]
    [InlineData(-1, 600)]
    [InlineData(0, 600)]
    [InlineData(800, 0)]
    [InlineData(800, int.MinValue)]
    public void InvalidBmpDimensionsAreRejected(int width, int height) =>
        Assert.Null(ImageDimensionReader.TryRead(CreateBmpHeader(40, width, height)));

    [Fact]
    public void TruncatedOrUnknownBmpHeaderIsRejected()
    {
        Assert.Null(ImageDimensionReader.TryRead(CreateBmpHeader(40, 800, 600).AsSpan(0, 53)));
        Assert.Null(ImageDimensionReader.TryRead(CreateBmpHeader(13, 800, 600)));
    }

    private static byte[] CreateBmpHeader(int dibSize, int width, int height)
    {
        var header = new byte[54];
        "BM"u8.CopyTo(header);
        BitConverter.GetBytes(dibSize).CopyTo(header, 14);
        BitConverter.GetBytes(width).CopyTo(header, 18);
        BitConverter.GetBytes(height).CopyTo(header, 22);
        return header;
    }

    [Fact]
    public void APngStatesItsSizeInTheHeader()
    {
        var header = new List<byte>
        {
            0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A,
            0, 0, 0, 13,
            (byte)'I', (byte)'H', (byte)'D', (byte)'R'
        };
        header.AddRange([0, 0, 0x03, 0x20]);  // 800
        header.AddRange([0, 0, 0x04, 0xB0]);  // 1200

        Assert.Equal(
            new PixelSize(800, 1200),
            ImageDimensionReader.TryRead(header.ToArray()));
    }

    [Fact]
    public void AGifStatesItsSizeLittleEndian()
    {
        byte[] header =
        [
            (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a',
            0x20, 0x03,
            0xB0, 0x04,
            0, 0
        ];

        Assert.Equal(
            new PixelSize(800, 1200),
            ImageDimensionReader.TryRead(header));
    }

    /// <summary>
    /// The common one. A lossy WebP states its size as two 14-bit values inside
    /// the VP8 bitstream, with the top two bits of each holding a scale that is
    /// not part of the number.
    /// </summary>
    [Fact]
    public void ALossyWebpIgnoresTheScaleBits()
    {
        var header = new byte[30];
        "RIFF"u8.CopyTo(header);
        "WEBP"u8.CopyTo(header.AsSpan(8));
        "VP8 "u8.CopyTo(header.AsSpan(12));

        // Frame tag, then the sync code that starts the key frame.
        header[23] = 0x9D;
        header[24] = 0x01;
        header[25] = 0x2A;

        // Scale bits set on both, to prove they are masked off rather than
        // read as part of the size.
        BitConverter.GetBytes((ushort)(800 | (0b11 << 14)))
            .CopyTo(header.AsSpan(26));
        BitConverter.GetBytes((ushort)(1200 | (0b10 << 14)))
            .CopyTo(header.AsSpan(28));

        Assert.Equal(
            new PixelSize(800, 1200),
            ImageDimensionReader.TryRead(header));
    }

    /// <summary>
    /// The extended form, which is what a WebP with animation, transparency or
    /// metadata carries. Its canvas size is 24-bit and also stored one less
    /// than the real value.
    /// </summary>
    [Fact]
    public void AnExtendedWebpReadsItsCanvasSize()
    {
        var header = new byte[30];
        "RIFF"u8.CopyTo(header);
        "WEBP"u8.CopyTo(header.AsSpan(8));
        "VP8X"u8.CopyTo(header.AsSpan(12));

        BitConverter.GetBytes(800 - 1).AsSpan(0, 3).CopyTo(header.AsSpan(24));
        BitConverter.GetBytes(1200 - 1).AsSpan(0, 3).CopyTo(header.AsSpan(27));

        Assert.Equal(
            new PixelSize(800, 1200),
            ImageDimensionReader.TryRead(header));
    }

    [Fact]
    public void ALosslessWebpStoresOneLessThanItsSize()
    {
        var header = new byte[30];
        "RIFF"u8.CopyTo(header);
        "WEBP"u8.CopyTo(header.AsSpan(8));
        "VP8L"u8.CopyTo(header.AsSpan(12));
        header[20] = 0x2F;

        // 14 bits of width then 14 of height, each one less than the real size.
        var packed = (uint)((800 - 1) | ((1200 - 1) << 14));
        BitConverter.GetBytes(packed).CopyTo(header.AsSpan(21));

        Assert.Equal(
            new PixelSize(800, 1200),
            ImageDimensionReader.TryRead(header));
    }

    /// <summary>
    /// The frame header is what states the size, and a real photograph carries
    /// metadata segments in front of it. Walking the chain rather than reading
    /// a fixed offset is the whole difficulty of this format.
    /// </summary>
    [Fact]
    public void AJpegIsFoundPastItsMetadataSegments()
    {
        var header = new List<byte> { 0xFF, 0xD8 };

        // An APP1 segment 200 bytes long, standing in for EXIF.
        header.AddRange([0xFF, 0xE1, 0x00, 0xC8]);
        header.AddRange(new byte[198]);

        // Then the baseline frame header: precision, height, width.
        header.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08]);
        header.AddRange([0x04, 0xB0]);  // 1200 high
        header.AddRange([0x03, 0x20]);  // 800 wide

        Assert.Equal(
            new PixelSize(800, 1200),
            ImageDimensionReader.TryRead(header.ToArray()));
    }

    [Fact]
    public void AProgressiveJpegIsAlsoAFrameHeader()
    {
        byte[] header =
        [
            0xFF, 0xD8,
            0xFF, 0xC2, 0x00, 0x11, 0x08,
            0x04, 0xB0,
            0x03, 0x20
        ];

        Assert.Equal(
            new PixelSize(800, 1200),
            ImageDimensionReader.TryRead(header));
    }

    /// <summary>
    /// Everything unreadable has to answer the same way, because the caller
    /// treats "no answer" as "keep the estimate" rather than as a failure.
    /// </summary>
    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0xFF, 0xD8 })]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })]
    public void AnythingUnreadableAnswersNothing(byte[] header) =>
        Assert.Null(ImageDimensionReader.TryRead(header));

    /// <summary>
    /// A JPEG whose frame header sits past the bytes that were read must not be
    /// guessed at from a partial segment.
    /// </summary>
    [Fact]
    public void AJpegTruncatedBeforeItsFrameHeaderAnswersNothing()
    {
        byte[] header =
        [
            0xFF, 0xD8,
            0xFF, 0xE1, 0x40, 0x00,
            0, 0, 0, 0
        ];

        Assert.Null(ImageDimensionReader.TryRead(header));
    }

    [Fact]
    public void AScanMarkerEndsTheSearch()
    {
        byte[] header =
        [
            0xFF, 0xD8,
            0xFF, 0xDA, 0x00, 0x08, 0, 0, 0, 0, 0, 0
        ];

        Assert.Null(ImageDimensionReader.TryRead(header));
    }
}
