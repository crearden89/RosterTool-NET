// New: the model builds on SQLite, and every context filters by its own identity.
using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Tests.Support;

namespace Roster.Data.Tests.Model;

public class ModelSmokeTests
{
    [Fact]
    public void The_model_creates_on_sqlite()
    {
        using var db = new TestDatabase();
        using var ctx = db.System();
        Assert.Equal(0, ctx.Stores.Count());
    }

    [Fact]
    public void Each_context_filters_by_its_own_user_not_the_first_one_built()
    {
        using var db = new TestDatabase();
        Guid a, b;
        using (var sys = db.System())
        {
            var dg = new DirectorGroup { Name = "Example Group" };
            var region = new Region { Name = "901 - Example Region", DirectorGroupId = dg.Id };
            sys.DirectorGroups.Add(dg);
            sys.SaveChanges();
            region.DirectorGroupId = dg.Id;
            sys.Regions.Add(region);
            sys.SaveChanges();
            var s1 = new Store { Code = "G801", Name = "Example Store North", Slug = "g801", RegionId = region.Id, KioskToken = "k1" };
            var s2 = new Store { Code = "G802", Name = "Example Store South", Slug = "g802", RegionId = region.Id, KioskToken = "k2" };
            sys.Stores.AddRange(s1, s2);
            sys.SaveChanges();
            (a, b) = (s1.Id, s2.Id);
        }

        var userA = new ScopedUser(Guid.NewGuid(), "a", Tier.Store, a, null, null, null);
        var userB = new ScopedUser(Guid.NewGuid(), "b", Tier.Store, b, null, null, null);
        using (var ca = db.As(userA)) Assert.Equal(["G801"], ca.Stores.Select(s => s.Code).ToList());
        using (var cb = db.As(userB)) Assert.Equal(["G802"], cb.Stores.Select(s => s.Code).ToList());
        using (var ca = db.As(userA)) Assert.Equal(["G801"], ca.Stores.Select(s => s.Code).ToList());
    }
}
