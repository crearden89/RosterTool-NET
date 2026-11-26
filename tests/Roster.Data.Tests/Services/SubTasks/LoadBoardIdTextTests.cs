// New: the load board's ids are made into text after the read, as .NET writes them (lower
// case), not inside the query, where SQL Server and SQLite print a uniqueidentifier in
// upper case. Every other id the app carries as text is .NET's (and the TypeScript app's)
// lower-case form, so an upper-case one would never match it.

using Roster.Data.Entities;
using Roster.Data.Services.RefData;
using Roster.Data.Services.SubTasks;
using Roster.Data.Tests.Support;

namespace Roster.Data.Tests.Services.SubTasks;

public sealed class LoadBoardIdTextTests : IDisposable
{
    private readonly TestDatabase _tdb = new();

    public void Dispose() => _tdb.Dispose();

    [Fact]
    public async Task Load_type_ids_read_as_the_same_text_dotnet_writes()
    {
        var contexts = new TestContexts(_tdb);
        var store = Seed.Stores(_tdb)[0];
        var typeId = Guid.Parse("8d6f1a2b-3c4d-4e5f-9a0b-1c2d3e4f5a6b");
        var period = new DateOnly(2026, 11, 23);
        using (var db = contexts.CreateSystem("test setup"))
        {
            db.LoadTypes.Add(new LoadType { Id = typeId, Name = "Chilled", MinutesPerPallet = 20 });
            db.DailyLoads.Add(new DailyLoad { StoreId = store.Id, Date = period.AddDays(-7), LoadTypeId = typeId, Qty = 4 });
            db.SaveChanges();
        }

        var (types, rows) = await new SubtaskLabels(contexts, new RefDataReader(contexts)).ReadLoadBoardAsync(store.Id, period);

        Assert.Equal(typeId.ToString(), Assert.Single(types, t => t.Name == "Chilled").Id);
        Assert.Equal("8d6f1a2b-3c4d-4e5f-9a0b-1c2d3e4f5a6b", Assert.Single(rows).LoadTypeId);
    }
}
