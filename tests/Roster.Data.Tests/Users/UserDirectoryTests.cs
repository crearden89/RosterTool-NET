// New: single sign-on look-up (replacing auth.ts's password sign-in) and the Users/EMSO admin
// writes (admin/users/actions.ts, admin/emso/actions.ts), without passwords.
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Tests.Scope;
using Roster.Data.Tests.Support;
using Roster.Data.Users;

namespace Roster.Data.Tests.Users;

public sealed class UserDirectoryTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly TestOrg _org;

    public UserDirectoryTests() => _org = TestOrg.Create(_db);

    public void Dispose() => _db.Dispose();

    private UserDirectory Directory(FirstSuperUserOptions? first = null) =>
        new(new TestContexts(_db, AccessContext.Anonymous), Options.Create(new UserDirectoryOptions { FirstSuperUser = first }),
            new FixedClock(new DateTimeOffset(2026, 8, 3, 22, 0, 0, TimeSpan.Zero)));

    private static SignInIdentity Entra(string upn, string? oid = null) =>
        new() { Method = "entra", Username = upn, ObjectId = oid, DisplayName = "Jordan Example" };

    [Fact]
    public async Task Finds_a_user_by_their_user_principal_name_and_remembers_their_object_id()
    {
        var found = await Directory().FindAsync(Entra("Store.S1@Example.com", "oid-123"));
        Assert.NotNull(found);
        Assert.Equal(_org.StoreS1.Id, found.Id);
        Assert.Equal(Tier.Store, found.Tier);
        Assert.Equal(_org.S1, found.StoreId);

        var row = _db.System().AppUsers.Single(u => u.Id == found.Id);
        Assert.Equal("oid-123", row.EntraObjectId);
        Assert.Equal("store.s1@example.com", row.UserPrincipalName);
        Assert.Equal("Jordan Example", row.DisplayName);
        Assert.NotNull(row.LastSignInAt);

        // From now on the object id alone finds them, even after a change of UPN.
        var again = await Directory().FindAsync(Entra("renamed@example.com", "oid-123"));
        Assert.Equal(found.Id, again?.Id);
    }

    [Fact]
    public async Task Finds_a_windows_sign_in_by_its_domain_account_then_by_its_username()
    {
        using (var sys = _db.System())
        {
            sys.AppUsers.Single(u => u.Id == _org.PaR1.Id).WindowsAccount = @"corp\pa1";
            sys.SaveChanges();
        }
        var byAccount = await Directory().FindAsync(new SignInIdentity { Method = "windows", Username = "someone-else", AccountName = @"CORP\pa1" });
        Assert.Equal(_org.PaR1.Id, byAccount?.Id);

        var byName = await Directory().FindAsync(new SignInIdentity { Method = "windows", Username = "dso.d1@example.com" });
        Assert.Equal(_org.DirectorD1.Id, byName?.Id);
    }

    [Fact]
    public async Task Someone_the_directory_does_not_know_has_no_access()
    {
        Assert.Null(await Directory().FindAsync(Entra("stranger@example.com", "oid-999")));
        Assert.Equal(5, _db.System().AppUsers.Count());
    }

    [Fact]
    public async Task The_configured_first_super_user_is_created_on_their_first_sign_in()
    {
        var first = new FirstSuperUserOptions { UserPrincipalName = "Owner@Example.com", DisplayName = "The Owner" };
        var found = await Directory(first).FindAsync(Entra("owner@example.com", "oid-owner"));
        Assert.NotNull(found);
        Assert.Equal(Tier.SuperUser, found.Tier);
        Assert.Equal("owner@example.com", found.Username);
        Assert.Equal("The Owner", found.DisplayName);
        // And only once.
        var again = await Directory(first).FindAsync(Entra("owner@example.com", "oid-owner"));
        Assert.Equal(found.Id, again?.Id);
        Assert.Equal(6, _db.System().AppUsers.Count());
    }

    [Fact]
    public async Task The_configured_first_super_user_is_promoted_if_their_row_says_otherwise()
    {
        var first = new FirstSuperUserOptions { UserPrincipalName = "pa.r1@example.com" };
        var found = await Directory(first).FindAsync(Entra("pa.r1@example.com"));
        Assert.Equal(Tier.SuperUser, found?.Tier);
        Assert.Null(found?.RegionId); // tier_scope: a super user carries no scope column
    }

    // ─── Admin ──────────────────────────────────────────────────────────────

    private UserAdmin Admin(ScopedUser who)
    {
        var contexts = new TestContexts(_db, AccessContext.ForUser(who));
        return new UserAdmin(contexts, new StoreScope(contexts, contexts));
    }

    [Fact]
    public async Task Creating_a_login_follows_the_tier_scope_rules()
    {
        var admin = Admin(_org.Super);
        Assert.Equal("Pick the person's store.", (await admin.CreateAsync(new NewAppUser { Username = "a@example.com", Tier = Tier.Store })).Error);
        Assert.Equal("Pick the EMSO's area.", (await admin.CreateAsync(new NewAppUser { Username = "b@example.com", Tier = Tier.Emso })).Error);
        Assert.Equal("A username is required.", (await admin.CreateAsync(new NewAppUser { Username = "  ", Tier = Tier.Pa })).Error);

        var ok = await admin.CreateAsync(new NewAppUser { Username = "  New.Manager@Example.com ", Tier = Tier.Store, StoreId = _org.S2, AreaId = _org.A1 });
        Assert.True(ok.Ok);
        var row = _db.System().AppUsers.Single(u => u.Id == ok.UserId);
        Assert.Equal("new.manager@example.com", row.Username);
        Assert.Equal("new.manager@example.com", row.UserPrincipalName);
        Assert.Equal(_org.S2, row.StoreId);
        Assert.Null(row.AreaId); // only the scope column the tier uses is kept

        Assert.False((await admin.CreateAsync(new NewAppUser { Username = "new.manager@example.com", Tier = Tier.Pa })).Ok);
    }

    [Fact]
    public async Task Only_a_super_user_manages_logins()
    {
        var result = await Admin(_org.EmsoA1).CreateAsync(new NewAppUser { Username = "x@example.com", Tier = Tier.Pa });
        Assert.False(result.Ok);
        Assert.False((await Admin(_org.StoreS1).DeleteAsync(_org.PaR1.Id)).Ok);
    }

    [Fact]
    public async Task Moving_an_emso_changes_their_area_only_for_an_emso()
    {
        var admin = Admin(_org.Super);
        Assert.True((await admin.SetEmsoAreaAsync(_org.EmsoA1.Id, _org.A2)).Ok);
        Assert.Equal(_org.A2, _db.System().AppUsers.Single(u => u.Id == _org.EmsoA1.Id).AreaId);
        await admin.SetEmsoAreaAsync(_org.StoreS1.Id, _org.A2); // not an EMSO: matches nothing
        Assert.Null(_db.System().AppUsers.Single(u => u.Id == _org.StoreS1.Id).AreaId);
    }

    [Fact]
    public async Task Deleting_a_login_clears_what_postgres_set_to_null_and_refuses_your_own()
    {
        var admin = Admin(_org.Super);
        Assert.Equal("You can't delete your own login.", (await admin.DeleteAsync(_org.Super.Id)).Error);

        using (var sys = _db.System())
        {
            sys.UploadBatches.Single().UploadedBy = _org.EmsoA1.Id;
            sys.GeneratedShifts.First().LockedBy = _org.EmsoA1.Id;
            sys.RosterAudit.Add(new RosterAudit { StoreId = _org.S1, UserId = _org.EmsoA1.Id, Action = "approve_plan" });
            sys.RosterReviews.Add(new RosterReview { StoreId = _org.S1, PeriodStart = new DateOnly(2026, 8, 3), SubmittedBy = _org.EmsoA1.Id, DecidedBy = _org.EmsoA1.Id });
            sys.SaveChanges();
        }
        Assert.True((await admin.DeleteAsync(_org.EmsoA1.Id)).Ok);

        using var check = _db.System();
        Assert.False(check.AppUsers.Any(u => u.Id == _org.EmsoA1.Id));
        Assert.Null(check.UploadBatches.Single().UploadedBy);
        Assert.False(check.GeneratedShifts.Any(s => s.LockedBy != null));
        Assert.Null(check.RosterAudit.Single().UserId);
        Assert.Null(check.RosterReviews.Single().SubmittedBy);
        Assert.False(check.Notifications.Any(n => n.UserId == _org.EmsoA1.Id)); // cascade
    }

    [Fact]
    public async Task A_login_that_generated_a_held_plan_cannot_be_deleted()
    {
        using (var sys = _db.System())
        {
            sys.PlanSnapshots.Add(new PlanSnapshot { StoreId = _org.S1, PeriodStart = new DateOnly(2026, 8, 3), Kind = SnapshotKinds.Plan, Payload = "{}", GeneratedBy = _org.StoreS1.Id });
            sys.SaveChanges();
        }
        Assert.False((await Admin(_org.Super).DeleteAsync(_org.StoreS1.Id)).Ok);
    }
}
