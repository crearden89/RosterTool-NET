// New: the Leave Liability page's reads (page.tsx with compute.ts's loading) and its
// four saves (actions.ts) on the data layer's model.

using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Services.Functions;
using Roster.Data.Services.RefData;
using Roster.Web.Features.LeaveLiability;
using Roster.Web.Tests.Sales;

namespace Roster.Web.Tests.LeaveLiability;

public class LeaveLiabilityServiceTests : IDisposable
{
    private readonly PageDb _db = new();
    private readonly Guid _store, _other, _area;
    private static readonly DateOnly P1 = new(2026, 7, 25), P2 = new(2026, 8, 8);

    public LeaveLiabilityServiceTests()
    {
        var (area, ids) = _db.SeedStores("G101", "G102");
        (_area, _store, _other) = (area, ids[0], ids[1]);
        _db.Add(
            new LeaveSettings { Id = true, TargetHoursPerFte = 76, FteDivisor = 76 },
            new LeaveAccrualBand { ContractHours = 90, RoleClass = "SM", LeaveNotionalHours = 9, AccrualWeeks = 5, AnnualAccrualHours = 225 },
            new LeaveAccrualBand { ContractHours = 76, RoleClass = "SA", LeaveNotionalHours = 7.6, AccrualWeeks = 4, AnnualAccrualHours = 152 },
            Snap(_store, P2, "910001", "1-Reyes, Noah", "SM", 90, 168.4, -12.75),
            Snap(_store, P2, "910002", "4-Cooper, Blake", "SA", 76, 80),
            Snap(_store, P2, "910003", "6C-Hart, Mia", "CASUAL", 0, 0),
            Snap(_store, P1, "910001", "1-Reyes, Noah", "SM", 90, 160, -12.75),
            Snap(_store, P1, "910002", "4-Cooper, Blake", "SA", 76, 70),
            Snap(_other, P2, "920001", "4-Lane, Kim", "SA", 76, 152),
            new LeaveRequestUkg { Id = Guid.NewGuid(), StoreId = _store, UkgEmployeeId = "910002", EmployeeName = "4-Cooper, Blake", LeaveType = "REQ - Annual Leave", StartDate = new(2026, 10, 5), EndDate = new(2026, 10, 9), Status = "Approved" });
    }

    private static LeaveLiabilitySnapshot Snap(Guid store, DateOnly period, string id, string name, string role, double contract, double annual, double lieu = 0) => new()
    {
        Id = Guid.NewGuid(), StoreId = store, PeriodEnd = period, UkgEmployeeId = id, EmployeeName = name, RoleClass = role,
        ContractHours = contract, NotionalHours = 7, AnnualHours = annual, LieuHours = lieu,
    };

    /// <summary>10 August 2026, midday in Sydney: the October leave is still ahead.</summary>
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 8, 10, 2, 0, 0, TimeSpan.Zero);
    }

    private LeaveLiabilityService Service() =>
        new(_db, _db.Scope, _db, new DatabaseFunctionsService(_db, _db.Scope, TimeProvider.System), new RefDataReader(_db), new FixedClock());

    [Fact]
    public async Task A_super_user_sees_the_store_its_previous_period_the_network_and_the_trend()
    {
        _db.User(Tier.SuperUser);
        var data = await Service().LoadAsync(new LeavePageQuery(_store, null, "network", null));

        var view = data.View!;
        Assert.Equal(P2, view.PeriodEnd);
        Assert.Equal(P1, view.PreviousPeriodEnd);
        Assert.Equal(["910001", "910002"], view.Members.Select(m => m.UkgEmployeeId).ToArray()); // the casual is left out
        Assert.Equal(1, view.Liability.CasualCount);
        Assert.Equal(5, view.Members[1].PlannedDays); // 5-9 Oct, approved
        Assert.Equal(["G102", "G101"], data.Network.Rows.Select(r => r.Code).ToArray()); // 152 per FTE first
        Assert.Equal([P2, P1], data.Network.Periods.ToArray());
        Assert.Equal([P1, P2], data.Trend.Select(t => t.PeriodEnd).ToArray());
        Assert.Equal("network", data.InitialTab);
        Assert.True(data.IsSuperUser && data.CanEditStore && data.ShowNetwork);
        Assert.Equal(3, data.MaxStaffOnLeave);
        Assert.Equal(2, data.Bands.Count);
        Assert.Equal("SA", data.Bands[0].RoleClass); // by role, then contract largest first
    }

    [Fact]
    public async Task A_store_login_sees_only_its_own_store_and_no_network_or_settings_tab()
    {
        _db.User(Tier.Store, storeId: _other);
        var data = await Service().LoadAsync(new LeavePageQuery(_store, P1, "settings", "national"));

        Assert.Equal(_other, data.StoreId); // ?store= outside the remit falls back
        Assert.Single(data.Stores);
        Assert.False(data.ShowNetwork);
        Assert.True(data.CanEditStore); // its own cover cap
        Assert.Equal("settings", data.InitialTab);
        Assert.Equal(P2, data.View!.PeriodEnd); // P1 is not held for this store: its newest
        // National is not honoured; the network's period is the one asked for (held by
        // another store), and this store has no rows in it.
        Assert.Equal(P1, data.Network.PeriodEnd);
        Assert.Empty(data.Network.Rows);
    }

    [Fact]
    public async Task A_read_only_tier_lands_on_the_overview_when_it_asks_for_settings()
    {
        _db.User(Tier.Pa);
        var data = await Service().LoadAsync(new LeavePageQuery(null, null, "settings", null));
        Assert.Equal("overview", data.InitialTab);
        Assert.False(data.CanEditStore);
        Assert.True(data.CanSeeNational);
    }

    [Fact]
    public async Task The_national_settings_and_bands_are_super_user_saves()
    {
        _db.User(Tier.Store, storeId: _store);
        Assert.Equal("/overview", (await Service().SaveLeaveSettingsAsync(80, 76)).RedirectTo);

        _db.User(Tier.SuperUser);
        var svc = Service();
        Assert.Equal("Target and FTE divisor must both be greater than zero.", (await svc.SaveLeaveSettingsAsync(double.NaN, 76)).Error);
        Assert.True((await svc.SaveLeaveSettingsAsync(80, 75)).Ok);
        Assert.Equal("Unknown role class", (await svc.SaveAccrualBandAsync(70, "CASUAL", 7, 4, 140)).Error);
        Assert.Equal("Contract hours must be greater than zero.", (await svc.SaveAccrualBandAsync(0, "SA", 7, 4, 140)).Error);
        Assert.True((await svc.SaveAccrualBandAsync(70, "DSM", 7.5, 5, 187.5)).Ok);
        Assert.True((await svc.SaveAccrualBandAsync(90, "SM", 9.5, 5, 237.5)).Ok);
        Assert.True((await svc.DeleteAccrualBandAsync(76, "SA")).Ok);

        await using var db = _db.System();
        var settings = await db.LeaveSettings.SingleAsync();
        Assert.Equal((80.0, 75.0), (settings.TargetHoursPerFte, settings.FteDivisor));
        var bands = await db.LeaveAccrualBands.OrderBy(b => b.RoleClass).ToListAsync();
        Assert.Equal(["DSM", "SM"], bands.Select(b => b.RoleClass).ToArray());
        Assert.Equal(9.5, bands[1].LeaveNotionalHours);
    }

    [Fact]
    public async Task The_cover_cap_is_the_stores_own_and_rounds_to_a_whole_number()
    {
        _db.User(Tier.Emso, areaId: _area);
        var svc = Service();
        Assert.Equal("Maximum on leave must be between 0 and 99.", (await svc.SaveMaxOnLeaveAsync(_store, 100)).Error);
        Assert.True((await svc.SaveMaxOnLeaveAsync(_store, 2.5)).Ok);

        _db.User(Tier.Pa);
        Assert.Equal(ScopeMessages.DeniedReadOnly, (await Service().SaveMaxOnLeaveAsync(_store, 4)).Error);

        await using var db = _db.System();
        Assert.Equal(3, (await db.StoreAttributes.SingleAsync(a => a.StoreId == _store)).MaxStaffOnLeave); // Math.round(2.5) = 3
    }

    public void Dispose() => _db.Dispose();
}
