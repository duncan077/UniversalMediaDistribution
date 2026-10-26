using System.Text;
using UniversalMediaDistribution.Torrenting;

namespace UniversalMediaDistribution.Reading.Pdfium.Tests;

public sealed class PdfiumPdfRendererTests
{
    [Fact]
    public async Task OpenAndRender_ProducesOrderedBoundedPngPages()
    {
        var content = CreatePdf(pageCount: 2);
        var renderer = new PdfiumPdfRenderer();
        using var document = await renderer.OpenAsync(CreateSource(content));

        Assert.Equal([0, 1], document.Pages.Select(static page => page.Position));
        Assert.All(document.Pages, static page =>
        {
            Assert.InRange(page.PixelWidth, 1, 1600);
            Assert.InRange(page.PixelHeight, 1, 4096);
        });

        await using var rendered = await document.OpenPageReadAsync(document.Pages[1]);
        var signature = new byte[8];
        Assert.Equal(signature.Length, await rendered.Stream!.ReadAsync(signature));
        Assert.Equal(
            new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A },
            signature);
    }

    [Fact]
    public async Task Open_RejectsContentWithoutPdfHeader()
    {
        var renderer = new PdfiumPdfRenderer();
        var content = Encoding.ASCII.GetBytes("not-a-pdf");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await renderer.OpenAsync(CreateSource(content)));

        Assert.Contains("PDF header", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Open_RejectsStreamShorterThanDeclaredTorrentLength()
    {
        var renderer = new PdfiumPdfRenderer();
        var content = CreatePdf(pageCount: 1);
        var source = new PdfDocumentSource(
            playbackUri: null,
            content.Length + 1L,
            _ => ValueTask.FromResult<IMediaReadHandle>(new MemoryReadHandle(content)));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await renderer.OpenAsync(source));

        Assert.Contains("declared torrent length", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static PdfDocumentSource CreateSource(byte[] content) =>
        new(
            playbackUri: null,
            content.Length,
            _ => ValueTask.FromResult<IMediaReadHandle>(new MemoryReadHandle(content)));

    private static byte[] CreatePdf(int pageCount)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>"
        };
        var pageReferences = string.Join(
            ' ',
            Enumerable.Range(0, pageCount).Select(index => $"{3 + index * 2} 0 R"));
        objects.Add($"<< /Type /Pages /Kids [{pageReferences}] /Count {pageCount} >>");
        for (var index = 0; index < pageCount; index++)
        {
            var pageObject = 3 + index * 2;
            var contentObject = pageObject + 1;
            objects.Add(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {contentObject} 0 R >>");
            objects.Add("<< /Length 0 >>\nstream\n\nendstream");
        }

        var document = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>(objects.Count);
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(document.Length);
            document.Append(index + 1)
                .Append(" 0 obj\n")
                .Append(objects[index])
                .Append("\nendobj\n");
        }

        var xrefOffset = document.Length;
        document.Append("xref\n0 ")
            .Append(objects.Count + 1)
            .Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            document.Append(offset.ToString("D10"))
                .Append(" 00000 n \n");
        }

        document.Append("trailer\n<< /Size ")
            .Append(objects.Count + 1)
            .Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xrefOffset)
            .Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(document.ToString());
    }

    private sealed class MemoryReadHandle(byte[] content) : IMediaReadHandle
    {
        public Stream? Stream { get; } = new MemoryStream(content, writable: false);

        public Uri? PlaybackUri => null;

        public async ValueTask DisposeAsync()
        {
            if (Stream is not null)
            {
                await Stream.DisposeAsync();
            }
        }
    }
}
