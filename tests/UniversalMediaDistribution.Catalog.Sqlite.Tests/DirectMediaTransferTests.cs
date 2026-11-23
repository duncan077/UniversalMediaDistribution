using System.Text.Json;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

public sealed class DirectMediaTransferTests
{
    [Theory]
    [InlineData("https://example.test/chapter.cbr", true)]
    [InlineData("http://example.test/movie.mp4", true)]
    [InlineData("relative/chapter.cbr", false)]
    [InlineData("file:///local/movie.mp4", false)]
    public void OnlyDirectWebAddressesCanOmitTheTorrent(string address, bool accepted)
    {
        var source = JsonSerializer.Deserialize<CatalogTransferMediaSource>(
            JsonSerializer.Serialize(new { filePath = address }),
            JsonSerializerOptions.Web)!;
        var document = new CatalogTransferDocument
        {
            Items = [new CatalogTransferItem
            {
                MediaType = "movie", Subtype = "movie", Name = "Film", Sources = [source]
            }]
        };
        if (accepted)
        {
            CatalogTransferValidator.Validate(document);
            Assert.Equal(address, source.FilePath);
        }
        else
        {
            Assert.Throws<CatalogTransferValidationException>(() => CatalogTransferValidator.Validate(document));
        }
    }
}
