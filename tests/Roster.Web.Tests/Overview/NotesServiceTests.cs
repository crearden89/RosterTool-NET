// New: Notes History's reads (analysis/notes/page.tsx) against a SQLite copy of the schema.

using Roster.Data.Entities;
using Roster.Web.Features.Analysis;
using Roster.Web.Tests.Overview;

namespace Roster.Web.Tests.Analysis;

public class NotesServiceTests
{
    private static OverviewTestDb WithNotes()
    {
        var t = new OverviewTestDb();
        var at = (int d) => new DateTimeOffset(2026, 11, d, 1, 0, 0, TimeSpan.Zero);
        t.Seed(db =>
        {
            db.StoreWalkNotes.Add(new StoreWalkNote { Id = Guid.NewGuid(), StoreId = OverviewTestDb.StoreA, Note = "Chiller 3 needs re-facing", RaisedDate = new(2026, 11, 2), CreatedAt = at(2), CompletedAt = at(4) });
            db.StoreWalkNotes.Add(new StoreWalkNote { Id = Guid.NewGuid(), StoreId = OverviewTestDb.StoreA, Note = "Trolley bay sign is down", RaisedDate = new(2026, 11, 5), CreatedAt = at(5) });
            db.StoreWalkNotes.Add(new StoreWalkNote { Id = Guid.NewGuid(), StoreId = OverviewTestDb.StoreC, Note = "Spill kit missing", RaisedDate = new(2026, 11, 1), CreatedAt = at(1) });
        });
        return t;
    }

    [Fact]
    public async Task Opens_on_the_stores_whole_history_of_completed_notes()
    {
        using var t = WithNotes();
        t.SignInAs(Tier.Store);
        var page = (await t.Analysis.LoadNotesAsync(null, null, null, null))!;
        Assert.Equal(("2026-11-02", "2026-11-25", NoteStatus.Completed), (page.StartDate, page.EndDate, page.Status));
        Assert.Equal(["Chiller 3 needs re-facing"], page.Notes.Select(n => n.Note));
        Assert.False(page.ShowStoreSelect);
        Assert.Empty(page.Stores);
        Assert.Equal((1, 1, 0), (page.Summary.Raised, page.Summary.Completed, page.Summary.Open));
    }

    [Fact]
    public async Task Filters_on_status_newest_first()
    {
        using var t = WithNotes();
        t.SignInAs(Tier.Store);
        var all = (await t.Analysis.LoadNotesAsync(null, null, null, "all"))!;
        Assert.Equal(["Trolley bay sign is down", "Chiller 3 needs re-facing"], all.Notes.Select(n => n.Note));
        var open = (await t.Analysis.LoadNotesAsync(null, null, null, "open"))!;
        Assert.Equal(["Trolley bay sign is down"], open.Notes.Select(n => n.Note));
        Assert.Equal(20, open.Summary.OldestOpenDays);
    }

    [Fact]
    public async Task A_range_that_isnt_a_date_finds_nothing()
    {
        using var t = WithNotes();
        t.SignInAs(Tier.Store);
        Assert.Empty((await t.Analysis.LoadNotesAsync(null, "2026-11-01", "", "all"))!.Notes);
    }

    [Fact]
    public async Task A_store_login_reads_only_its_own_notes()
    {
        using var t = WithNotes();
        t.SignInAs(Tier.Store);
        var page = (await t.Analysis.LoadNotesAsync(OverviewTestDb.StoreC.ToString(), "2026-11-01", "2026-11-30", "all"))!;
        Assert.Equal(OverviewTestDb.StoreA, page.StoreId);
        Assert.DoesNotContain(page.Notes, n => n.Note == "Spill kit missing");
    }
}
