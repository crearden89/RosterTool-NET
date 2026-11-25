// Port of the reads of src/app/load/[token]/page.tsx and of actions.ts.
// Public, no-login Load Planner (converted from the standalone HTML tool). The
// crew and their start times come from today's APPROVED roster - everyone with
// Load Running tiles - so the only manual input is the truck's pallet counts,
// entered once per day (editable by anyone, like the entry). The allocator
// decides who unloads what, rotating category ownership away from whoever did
// it most recently; the SM can pin people to categories and the plan redraws
// live.

using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Engine.Core;
using Roster.Engine.Load;
using Roster.Web.Features.Floor;
using Roster.Web.Kiosk;

namespace Roster.Web.Features.LoadPlanner;

public sealed class LoadPlannerService(IKioskLinkCheck links, IRosterDbContextFactory contexts, TimeProvider clock) : ILoadPlannerService
{
    private static double Min(TimeOnly t) => t.Hour * 60 + t.Minute;

    public async Task<LoadPageData> LoadAsync(string token, KioskLink link, bool fromFloor, CancellationToken ct = default)
    {
        var kiosk = link.Store ?? throw new ArgumentException("An open kiosk link carries its store.", nameof(link));
        var storeId = kiosk.StoreId;
        // "Today" is the STORE's local date (state timezone) - the server clock is
        // UTC, which lags an Australian morning by a whole calendar day.
        var local = StoreTime.StoreNow(kiosk.State, clock.GetUtcNow());
        var day = local.Date;
        var historyStart = day.AddDays(-14);

        await using var db = contexts.Create(AccessContext.ForKiosk(storeId));
        var types = await db.LoadTypes.AsNoTracking().OrderBy(t => t.SortOrder).ToListAsync(ct);
        var loads = await db.DailyLoads.AsNoTracking().Where(l => l.StoreId == storeId && l.Date == day)
            .Select(l => new { l.LoadTypeId, l.Qty, l.UpdatedAt }).ToListAsync(ct);
        var allShifts = await db.GeneratedShifts.AsNoTracking().Where(s => s.StoreId == storeId && s.Date == day)
            .Select(s => new { s.Id, s.EmployeeId, s.EmployeeName, s.StartTime }).ToListAsync(ct);
        var history = await db.LoadAssignments.AsNoTracking()
            .Where(a => a.StoreId == storeId && a.Date >= historyStart && a.Date <= day)
            .Select(a => new { a.Date, a.EmployeeId, a.LoadType, a.Manual }).ToListAsync(ct);
        var rosterStatus = await db.DayPlans.AsNoTracking().Where(p => p.StoreId == storeId && p.Date == day)
            .Select(p => p.RosterStatus).FirstOrDefaultAsync(ct);
        var approved = rosterStatus == DayStatuses.Approved;
        // Crew only comes from an APPROVED roster. Manual shift edits are stored in
        // generated_shifts even before approval, so an un-approved day can hold a
        // few pinned rows that were never signed off - they must not become a crew.
        var shifts = allShifts.Where(_ => approved).ToList();

        // Crew = anyone on today's approved roster with Load Running tiles; their
        // unload starts when their first LR block does. Keyed by EMPLOYEE id so
        // ownership history and pins survive across days.
        var shiftIds = shifts.Select(s => s.Id).ToList();
        var lrBlocks = await db.ShiftTaskBlocks.AsNoTracking()
            .Where(b => shiftIds.Contains(b.ShiftId) && b.LabourGroup == TaskGroups.LoadRunning)
            .Select(b => new { b.ShiftId, b.BlockTime }).ToListAsync(ct);
        var firstLrByShift = new Dictionary<Guid, double>();
        foreach (var b in lrBlocks)
        {
            var m = Min(b.BlockTime);
            if (!firstLrByShift.TryGetValue(b.ShiftId, out var cur) || m < cur) firstLrByShift[b.ShiftId] = m;
        }
        // A new starter is slower, so their pallets take longer and the truck is away
        // later. Each load type carries its own minutes per pallet per tenure band
        // (Admin -> Load Types); this resolves which band each person is in TODAY
        // from their start date. Staff with no recorded start date are treated as
        // fully trained - most of the existing team predates the field.
        var crewIds = shifts.Select(s => s.EmployeeId).ToList();
        var startById = new Dictionary<Guid, DateOnly?>();
        foreach (var r in await db.Employees.AsNoTracking().Where(e => crewIds.Contains(e.Id))
                     .Select(e => new { e.Id, e.StartDate }).ToListAsync(ct))
            startById[r.Id] = r.StartDate;

        var crew = shifts
            .Where(s => firstLrByShift.ContainsKey(s.Id))
            .Select(s => new LoadCrewMember(s.EmployeeId.ToString(), s.EmployeeName, firstLrByShift[s.Id],
                Probation.BandForStartDate(startById.GetValueOrDefault(s.EmployeeId), day)))
            .ToList();
        // Days of service for the "New" tag beside a new starter's name.
        int? DaysOf(string key) => Probation.DaysOfService(startById.GetValueOrDefault(Guid.Parse(key)), day);

        var qtyByType = new Dictionary<Guid, double>();
        foreach (var l in loads) qtyByType[l.LoadTypeId] = l.Qty;
        var typeOptions = types.Select(t => new LoadTypeOption(
            t.Id.ToString(),
            t.Name,
            t.MinutesPerPallet,
            new PalletMinutes(t.MinutesPerPallet, t.MinutesPerPalletLt90, t.MinutesPerPalletLt60, t.MinutesPerPalletLt30),
            t.Color,
            qtyByType.GetValueOrDefault(t.Id, 0),
            t.Priority)).ToList();
        var hasEntry = loads.Count > 0;

        // Rotation history: how often each person owned each category over the last
        // fortnight (today's rows excluded - they'd bias against today's own plan).
        // Today's manual rows are the SM's pins.
        var typeCounts = new Dictionary<string, double>();
        var initialPins = new Dictionary<string, string>();
        foreach (var h in history)
        {
            var employeeId = h.EmployeeId.ToString();
            if (h.Date == day)
            {
                if (h.Manual) initialPins[employeeId] = h.LoadType;
                continue;
            }
            var k = $"{employeeId}:{h.LoadType}";
            typeCounts[k] = typeCounts.GetValueOrDefault(k, 0) + 1;
        }

        var plan = hasEntry && crew.Count > 0
            ? Allocate.AllocateLoad(crew, LoadModel.LoadItemsFor(typeOptions), new AllocateOptions
            {
                Pinned = new JsMap<string, string>(initialPins),
                TypeCounts = new JsMap<string, double>(typeCounts),
            })
            : null;

        // Record today's AUTOMATIC ownership (so tomorrow's rotation knows about it)
        // without ever touching the SM's manual pins.
        if (plan is not null)
        {
            var autoRows = plan.Crew
                .Where(c => c.Segments.Count > 0 && !string.IsNullOrEmpty(c.PrimaryType) && !initialPins.ContainsKey(c.Key))
                .ToList();
            if (autoRows.Count > 0) await RecordOwnersAsync(db, storeId, day, autoRows, ct);
        }

        // When the day's load was last saved (every save rewrites every row).
        DateTimeOffset? lastSave = loads.Count > 0 ? loads.Max(l => l.UpdatedAt) : null;

        return new LoadPageData(
            token,
            $"{kiosk.Name} · {kiosk.Code}",
            kiosk.State,
            day,
            local.Minutes,
            approved,
            crew.Select(c => new CrewOption(c.Key, c.Name, c.StartMin, c.TenureBand ?? TenureBands.Gt90, DaysOf(c.Key))).ToList(),
            typeOptions,
            hasEntry,
            lastSave is { } saved ? StoreTime.StoreNow(kiosk.State, saved).Minutes : null,
            initialPins,
            typeCounts,
            fromFloor)
        {
            NoLoginNeeded = link.NoLoginNeeded,
        };
    }

    // The upsert of today's automatic rows (on store, date and employee).
    private static async Task RecordOwnersAsync(RosterDbContext db, Guid storeId, DateOnly day, List<CrewSchedule> owners, CancellationToken ct)
    {
        var ids = owners.Select(c => Guid.Parse(c.Key)).ToList();
        var existing = await db.LoadAssignments.Where(a => a.StoreId == storeId && a.Date == day && ids.Contains(a.EmployeeId))
            .ToDictionaryAsync(a => a.EmployeeId, ct);
        foreach (var c in owners)
        {
            var id = Guid.Parse(c.Key);
            if (!existing.TryGetValue(id, out var row))
            {
                row = new LoadAssignment { StoreId = storeId, Date = day, EmployeeId = id };
                db.LoadAssignments.Add(row);
                existing[id] = row;
            }
            row.EmployeeName = c.Name;
            row.LoadType = c.PrimaryType!;
            row.Manual = false;
        }
        // The TypeScript ignores this write's result: the page shows either way.
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { }
    }

    private async Task<KioskStore?> StoreForAsync(string token, CancellationToken ct)
    {
        var link = await links.CheckAsync(token, ct);
        return link.Status == KioskLinkStatus.Open ? link.Store : null;
    }

    private static readonly KioskResult UnknownStore = KioskResult.Fail("Unknown store");

    public async Task<KioskResult> SaveDailyLoadAsync(string token, IReadOnlyList<(string LoadTypeId, double Qty)> entries, CancellationToken ct = default)
    {
        if (await StoreForAsync(token, ct) is not { } store) return UnknownStore;
        await using var db = contexts.Create(AccessContext.ForKiosk(store.StoreId));
        var validIds = (await db.LoadTypes.AsNoTracking().Select(t => t.Id).ToListAsync(ct)).ToHashSet();
        // The store's OWN date - a UTC-derived date is yesterday until mid-morning.
        var date = StoreTime.StoreNow(store.State, clock.GetUtcNow()).Date;
        var now = clock.GetUtcNow();

        var rows = entries
            .Where(e => Guid.TryParse(e.LoadTypeId, out var id) && validIds.Contains(id))
            .Select(e =>
            {
                var rounded = Js.Round(e.Qty);
                return (Id: Guid.Parse(e.LoadTypeId), Qty: (int)Math.Max(0, Math.Min(99, Js.Truthy(rounded) ? rounded : 0)));
            })
            .ToList();
        if (rows.Count == 0) return KioskResult.Fail("Nothing to save");

        var ids = rows.Select(r => r.Id).ToList();
        var existing = await db.DailyLoads.Where(l => l.StoreId == store.StoreId && l.Date == date && ids.Contains(l.LoadTypeId))
            .ToDictionaryAsync(l => l.LoadTypeId, ct);
        foreach (var (id, qty) in rows)
        {
            if (!existing.TryGetValue(id, out var row))
            {
                row = new DailyLoad { StoreId = store.StoreId, Date = date, LoadTypeId = id };
                db.DailyLoads.Add(row);
                existing[id] = row;
            }
            row.Qty = qty;
            row.UpdatedAt = now;
        }
        return await SaveAsync(db, ct);
    }

    public async Task<KioskResult> SetLoadAssignmentAsync(string token, string employeeId, string employeeName, string? loadType, CancellationToken ct = default)
    {
        if (await StoreForAsync(token, ct) is not { } store) return UnknownStore;
        await using var db = contexts.Create(AccessContext.ForKiosk(store.StoreId));
        var date = StoreTime.StoreNow(store.State, clock.GetUtcNow()).Date;
        if (!Guid.TryParse(employeeId, out var id)) return KioskResult.Fail($"invalid input syntax for type uuid: \"{employeeId}\"");

        var row = await db.LoadAssignments.FirstOrDefaultAsync(a => a.StoreId == store.StoreId && a.Date == date && a.EmployeeId == id, ct);
        if (loadType is null)
        {
            if (row is null) return KioskResult.Success;
            db.LoadAssignments.Remove(row);
            return await SaveAsync(db, ct);
        }

        if (!await db.LoadTypes.AsNoTracking().AnyAsync(t => t.Name == loadType, ct)) return KioskResult.Fail("Unknown load type");
        // The kiosk is a no-login surface, so the employee must be verified
        // against THIS store's roster for today and the name derived server-side
        // - the same rule AddWalkNote follows. A crafted id from another store
        // (or a made-up name) never reaches the load board or the rotation
        // history that feeds auto-assignment.
        var shift = await db.GeneratedShifts.AsNoTracking()
            .Where(s => s.StoreId == store.StoreId && s.Date == date && s.EmployeeId == id)
            .Select(s => new { s.EmployeeId, s.EmployeeName }).FirstOrDefaultAsync(ct);
        if (shift is null) return KioskResult.Fail("That person is not on today's roster");
        _ = employeeName; // kept in the signature for callers; never trusted

        if (row is null)
        {
            row = new LoadAssignment { StoreId = store.StoreId, Date = date, EmployeeId = shift.EmployeeId };
            db.LoadAssignments.Add(row);
        }
        row.EmployeeName = shift.EmployeeName;
        row.LoadType = loadType;
        row.Manual = true;
        return await SaveAsync(db, ct);
    }

    public async Task<LoadCompleteResult> MarkLoadCompleteAsync(string token, CancellationToken ct = default)
    {
        if (await StoreForAsync(token, ct) is null) return new LoadCompleteResult(false, "Unknown store");
        // Deliberately not persisted yet: the TypeScript app's TODO(reporting) -
        // a load_completions table and a download of it.
        return new LoadCompleteResult(true, null, clock.GetUtcNow());
    }

    private static async Task<KioskResult> SaveAsync(RosterDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return KioskResult.Success;
        }
        catch (DbUpdateException e)
        {
            return KioskResult.Fail((e.InnerException ?? e).Message);
        }
        catch (ScopeViolationException e)
        {
            return KioskResult.Fail(e.Message);
        }
    }
}
