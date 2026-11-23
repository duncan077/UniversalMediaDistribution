#:property TargetFramework=net11.0
#:project ../../../../src/UniversalMediaDistribution.Catalog.Sqlite/UniversalMediaDistribution.Catalog.Sqlite.csproj
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Catalog.Sqlite;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Reading;

// dotnet run GenerateDotnetLibrary.cs -- <new scratch database> <new .umdbackup>
var db = new SqliteCatalogDatabase(args[0]);
var catalog = new SqliteCatalogRepository(db);
await catalog.MergeCatalogAsync(new CatalogTransferDocument
{
    Items = new[] { "manga", "novel" }.Select(kind => new CatalogTransferItem
    {
        Name = "Prueba de migración",
        MediaType = "book",
        Subtype = kind,
        Genres = ["Aventura"],
        Units = [new CatalogTransferUnit { Kind = "chapter", DisplayNumber = "2", SortOrder = 1 }]
    }).ToArray()
});

var profiles = new SqliteUserProfileStore(db);
var second = await profiles.CreateAsync("Segundo", null);
var state = new SqliteUserMediaStateStore(db);
var bindings = new SqliteApiTitleBindingStore(db);
var first = db.ActiveProfileId;
foreach (var id in new[] { first, second.Id })
{
    db.ActiveProfileId = id;
    var list = await state.CreateUserListAsync("Lecturas");
    var kind = id == first ? "book.manga" : "book.novel";
    var card = (await catalog.ListAsync()).Single(c => c.SubtypeCode == kind);
    var details = (await catalog.GetDetailsAsync(card.Id))!;
    await bindings.SaveAsync(new ApiTitleBinding(card.Id, "leercapitulo", kind, card.Name));
    await state.SaveProgressAsync(details.Units[0].Identity, new MediaProgressState(
        id == first ? 7 : 19, MediaPositionKind.Page, false, DateTimeOffset.UtcNow));
    await state.SaveReaderDisplayModeAsync(details.Identity, ReaderDisplayMode.Continuous);
    await state.SetFavoriteCatalogItemAsync(card.Id, true);
    await state.SetUserListMembershipAsync(list.Id, card.Id, true);
}
await profiles.MarkUsedAsync(second.Id);
await new SqliteDatabaseBackupService(db).CreateBackupAsync(args[1]);
