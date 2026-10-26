using Avalonia.Media.Imaging;
using Avalonia.Skia;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

[Collection("Image memory")]
public sealed class BmpImageTests
{
    [Fact]
    public void SupportedBmpPageMustStillDecode()
    {
        SkiaPlatform.Initialize();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0x4d42);
            writer.Write(58); writer.Write(0); writer.Write(54);
            writer.Write(40); writer.Write(1); writer.Write(1);
            writer.Write((ushort)1); writer.Write((ushort)24);
            writer.Write(0); writer.Write(4); writer.Write(0); writer.Write(0);
            writer.Write(0); writer.Write(0); writer.Write(0x00ffffff);
        }
        stream.Position = 0;
        using var native = Bitmap.DecodeToWidth(stream, 1);
        Assert.Equal(1, native.PixelSize.Width);
        stream.Position = 0;
        using var bounded = BoundedImageDecoder.Decode(stream, 1, 4096);
        Assert.Equal(1, bounded.PixelSize.Width);
    }
}
