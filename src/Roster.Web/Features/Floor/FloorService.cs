// Port of the reads of src/app/floor/[token]/page.tsx and of walk-actions.ts.
// Public, no-login Floor Display (spec 11.10): the wall screen in the store.
// Reads the approved roster for the day (generated_shifts + shift_task_blocks)
// as the store's kiosk and shows who is on, what each person is doing now and
// next, the whole staffed day as task bars, the day's admin tasks, the walk
// notes and the truck. "Today" and "now" are the STORE's local clock (state
// timezone), not the server's.

using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Data.Services.Approval;
using Roster.Data.Services.RefData;
using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Walk;
using Roster.Web.Kiosk;

namespace Roster.Web.Features.Floor;

public sealed class FloorService(
    IKioskLinkCheck links,
    IRosterDbContextFactory contexts,
    IRosterDrift drift,
    IRefData refData,
    TimeProvider clock) : IFloorService
{
    private static double Min(TimeOnly t) => t.Hour * 60 + t.Minute;

    public async Task<FloorPageData> LoadAsync(string token, KioskLink link, string? date, string? view, CancellationToken ct = default)
    {
        var kiosk = link.Store ?? throw new ArgumentException("An open kiosk link carries its store.", nameof(link));
        var storeId = kiosk.StoreId;
        var local = StoreTime.StoreNow(kiosk.State, clock.GetUtcNow());
        // An unreadable ?date= shows today (see DIFFS: the TypeScript passes it to
        // the queries as it is).
        DateOnly? given = DateOnly.TryParseExact(date, "yyyy-MM-dd", out var d) ? d : null;
        var day = given ?? local.Date;
        var dow = Period.DowMon0(day);
        // The Load page's rotation history: the fortnight before the day.
        var historyStart = day.AddDays(-14);

        await using var db = contexts.Create(AccessContext.ForKiosk(storeId));
        var store = await db.Stores.AsNoTracking().Where(s => s.Id == storeId)
            .Select(s => new { s.Name, s.Code, s.State, s.RegionId, s.KioskToken }).FirstAsync(ct);

        var shifts = await db.GeneratedShifts.AsNoTracking()
            .Where(s => s.StoreId == storeId && s.Date == day)
            .OrderBy(s => s.StartTime)
            .Select(s => new { s.Id, s.EmployeeId, s.EmployeeName, s.Role, s.StartTime, s.EndTime, s.BreakMinutes })
            .ToListAsync(ct);
        var rules = await refData.GetAdminTasksAsync(ct);
        var attrs = await db.StoreAttributes.AsNoTracking().Where(a => a.StoreId == storeId)
            .Select(a => new { a.McoRegisters, a.ScoLanes }).FirstOrDefaultAsync(ct);
        var employeeCount = await db.BenchmarkDays.AsNoTracking().Where(b => b.StoreId == storeId && b.Date == day)
            .Select(b => (int?)b.EmployeeCount).FirstOrDefaultAsync(ct);
        var hours = await db.StoreTradingHours.AsNoTracking().Where(h => h.StoreId == storeId && h.DayOfWeek == dow)
            .Select(h => new { h.OpenTime, h.CloseTime, h.Closed }).FirstOrDefaultAsync(ct);
        var regionName = await db.Regions.AsNoTracking().Where(r => r.Id == store.RegionId).Select(r => r.Name).FirstOrDefaultAsync(ct);
        // The day's place in the 3-tier holiday calendar, read here as the kiosk:
        // a kiosk has no signed-in user.
        var holiday = await db.PublicHolidaysNational.AsNoTracking().AnyAsync(h => h.Date == day, ct)
            || (!string.IsNullOrEmpty(kiosk.State)
                && await db.PublicHolidaysState.AsNoTracking().AnyAsync(h => h.State == kiosk.State && h.Date == day, ct))
            || await db.PublicHolidaysLocal.AsNoTracking().AnyAsync(h => h.StoreId == storeId && h.Date == day, ct);
        // The truck, read as the Load page reads it.
        var loadTypes = await db.LoadTypes.AsNoTracking().OrderBy(t => t.SortOrder).ToListAsync(ct);
        var loads = await db.DailyLoads.AsNoTracking().Where(l => l.StoreId == storeId && l.Date == day)
            .Select(l => new { l.LoadTypeId, l.Qty }).ToListAsync(ct);
        var loadHistory = await db.LoadAssignments.AsNoTracking()
            .Where(a => a.StoreId == storeId && a.Date >= historyStart && a.Date <= day)
            .Select(a => new { a.Date, a.EmployeeId, a.LoadType, a.Manual }).ToListAsync(ct);

        var shiftIds = shifts.Select(s => s.Id).ToList();
        var employeeIds = shifts.Select(s => s.EmployeeId).Distinct().ToList();
        var blocks = await db.ShiftTaskBlocks.AsNoTracking().Where(b => shiftIds.Contains(b.ShiftId))
            .Select(b => new { b.ShiftId, b.BlockTime, b.LabourGroup }).ToListAsync(ct);
        // Which admin tasks are already ticked off for this day.
        var checkedNames = await db.AdminTaskChecks.AsNoTracking().Where(c => c.StoreId == storeId && c.Date == day)
            .Select(c => c.TaskName).ToListAsync(ct);
        // Open notes carry over from previous walks, so this is not filtered by
        // day. Completed ones are fetched only for TODAY, to back Undo.
        var today = local.Date;
        var noteRows = await db.StoreWalkNotes.AsNoTracking()
            .Where(n => n.StoreId == storeId && (n.CompletedAt == null || n.RaisedDate == today))
            .OrderBy(n => n.RaisedDate)
            .Select(n => new { n.Id, n.Note, n.AssignedEmployeeId, n.AssignedName, n.RaisedDate, n.CompletedAt })
            .ToListAsync(ct);
        // Positions (pay rates) and start dates (pallet minutes) of who is on.
        var people = await db.Employees.AsNoTracking().Where(e => employeeIds.Contains(e.Id))
            .Select(e => new { e.Id, e.PositionCode, e.SmYearBand, e.StartDate }).ToListAsync(ct);
        // Has the plan been re-solved since this roster was published to the floor?
        var driftState = await drift.DetectRosterDriftAsync(storeId, day, ct);

        var reads = new FloorReads
        {
            Token = token,
            StoreName = store.Name,
            StoreCode = store.Code,
            KioskToken = store.KioskToken,
            NoLoginNeeded = link.NoLoginNeeded,
            Day = day,
            LocalDate = local.Date,
            LocalMinutes = local.Minutes,
            DateGiven = given is not null,
            ShowNotes = view == "notes",
            Shifts = shifts.Select(s => new FloorShiftRow(s.Id.ToString(), s.EmployeeId.ToString(), s.EmployeeName, s.Role,
                Min(s.StartTime), Min(s.EndTime), s.BreakMinutes)).ToList(),
            Blocks = blocks.Select(b => new FloorBlockRow(b.ShiftId.ToString(), Min(b.BlockTime), b.LabourGroup)).ToList(),
            AdminTaskRules = rules,
            McoRegisters = attrs?.McoRegisters ?? 0,
            ScoLanes = attrs?.ScoLanes ?? 0,
            EmployeeCount = employeeCount ?? 0,
            Hours = hours is null
                ? null
                : new FloorTradingHours(hours.OpenTime is { } o ? Min(o) : null, hours.CloseTime is { } c ? Min(c) : null, hours.Closed),
            RegionName = regionName,
            PublicHoliday = holiday,
            LoadTypes = loadTypes.Select(t => new LoadTypeRow(t.Id.ToString(), t.Name, t.MinutesPerPallet, t.MinutesPerPalletLt90,
                t.MinutesPerPalletLt60, t.MinutesPerPalletLt30, t.Color, t.Priority)).ToList(),
            Loads = loads.Select(l => new DailyLoadRow(l.LoadTypeId.ToString(), l.Qty)).ToList(),
            LoadHistory = loadHistory.Select(h => new LoadHistoryRow(h.Date, h.EmployeeId.ToString(), h.LoadType, h.Manual)).ToList(),
            CheckedNames = checkedNames,
            Notes = noteRows.Select(n => new WalkNote(n.Id.ToString(), n.Note, n.AssignedEmployeeId?.ToString(), n.AssignedName,
                n.RaisedDate, n.CompletedAt)).ToList(),
            People = people.Select(p => new FloorPersonRow(p.Id.ToString(), p.PositionCode, p.SmYearBand, p.StartDate)).ToList(),
            Approved = driftState.Approved,
            Drifted = driftState.Drifted,
            ApprovedAt = driftState.ApprovedAt,
            RegeneratedAt = driftState.RegeneratedAt,
        };

        // No auto-refresh on the notes view: a refresh mid-typing would wipe an
        // unsaved note, and there is nothing clock-driven on that screen.
        if (reads.ShowNotes) return new FloorPageData(token, null, FloorAssembly.NotesView(reads));

        // Persisted blocks already carry the Registers/SCO split, so no Till data is
        // needed here - BuildRosterRows just re-applies the break-tile overlay.
        var rawShifts = FloorAssembly.RawShifts(reads);
        var rosterRows = Display.BuildRosterRows(rawShifts);

        // Premium time from Pay Rates: the day's pay day type, judged on that day
        // type's own rates for the hourly-paid people on the roster (store
        // assistants and juniors), one entry per person. No rates, no shading.
        var premium = new List<PremiumBand>();
        if (FloorAssembly.HasRoster(reads, rosterRows))
        {
            var positions = FloorAssembly.PremiumPositions(reads);
            if (positions.Count > 0)
            {
                var dayType = PremiumTime.PayDayTypeFor(day, holiday);
                var keys = positions.SelectMany(PremiumTime.RateKeysFor).Distinct().ToList();
                var rates = await db.PayRates.AsNoTracking().Where(r => r.DayType == dayType && keys.Contains(r.Role))
                    .Select(r => new PayRateRow(r.DayType, r.Hour, r.Role, r.Rate)).ToListAsync(ct);
                premium = FloorAssembly.PremiumBands(rates, dayType, positions);
            }
        }

        return new FloorPageData(token, FloorAssembly.ScreenView(reads, rawShifts, rosterRows, premium), null);
    }

    // ─── Walk notes and admin ticks (walk-actions.ts) ────────────────────────
    // Store walk notes are captured on the KIOSK, which is a no-login surface like
    // the Load Planner page - so these actions authenticate on the store's secret
    // kiosk token rather than a session, exactly as SaveDailyLoad does.

    private async Task<KioskStore?> StoreForAsync(string token, CancellationToken ct)
    {
        var link = await links.CheckAsync(token, ct);
        return link.Status == KioskLinkStatus.Open ? link.Store : null;
    }

    private static readonly KioskResult UnknownStore = KioskResult.Fail("Unknown store");

    public async Task<KioskResult> AddWalkNoteAsync(string token, string rawNote, string? assignedEmployeeId, CancellationToken ct = default)
    {
        if (await StoreForAsync(token, ct) is not { } store) return UnknownStore;

        var cleaned = Notes.CleanNote(rawNote);
        if (!cleaned.Ok) return KioskResult.Fail(cleaned.Error!);

        var today = StoreTime.StoreNow(store.State, clock.GetUtcNow()).Date;
        await using var db = contexts.Create(AccessContext.ForKiosk(store.StoreId));

        string? assignedName = null;
        Guid? employeeId = null;
        if (!string.IsNullOrEmpty(assignedEmployeeId) && Guid.TryParse(assignedEmployeeId, out var wanted))
        {
            var shift = await db.GeneratedShifts.AsNoTracking()
                .Where(s => s.StoreId == store.StoreId && s.Date == today && s.EmployeeId == wanted)
                .Select(s => new { s.EmployeeId, s.EmployeeName })
                .FirstOrDefaultAsync(ct);
            // Silently unassigned rather than rejected: the roster can change between
            // the page rendering and the tap, and losing the typed note over a stale
            // dropdown would be a worse outcome than an unassigned note.
            if (shift is not null)
            {
                employeeId = shift.EmployeeId;
                assignedName = shift.EmployeeName;
            }
        }

        db.StoreWalkNotes.Add(new StoreWalkNote
        {
            StoreId = store.StoreId,
            Note = cleaned.Text!,
            AssignedEmployeeId = employeeId,
            AssignedName = assignedName,
            RaisedDate = today,
        });
        return await SaveAsync(db, ct);
    }

    public async Task<KioskResult> CompleteWalkNoteAsync(string token, string noteId, CancellationToken ct = default)
    {
        if (await StoreForAsync(token, ct) is not { } store) return UnknownStore;
        if (!Guid.TryParse(noteId, out var id)) return BadUuid(noteId);
        await using var db = contexts.Create(AccessContext.ForKiosk(store.StoreId));
        var note = await db.StoreWalkNotes.FirstOrDefaultAsync(n => n.Id == id && n.StoreId == store.StoreId && n.CompletedAt == null, ct);
        if (note is null) return KioskResult.Success;
        note.CompletedAt = clock.GetUtcNow();
        return await SaveAsync(db, ct);
    }

    public async Task<KioskResult> ReopenWalkNoteAsync(string token, string noteId, CancellationToken ct = default)
    {
        if (await StoreForAsync(token, ct) is not { } store) return UnknownStore;
        if (!Guid.TryParse(noteId, out var id)) return BadUuid(noteId);
        await using var db = contexts.Create(AccessContext.ForKiosk(store.StoreId));
        var note = await db.StoreWalkNotes.FirstOrDefaultAsync(n => n.Id == id && n.StoreId == store.StoreId, ct);
        if (note is null) return KioskResult.Success;
        note.CompletedAt = null;
        return await SaveAsync(db, ct);
    }

    public async Task<KioskResult> SetAdminTaskCheckAsync(string token, string taskName, bool isChecked, CancellationToken ct = default)
    {
        if (await StoreForAsync(token, ct) is not { } store) return UnknownStore;
        var name = Js.Trim(taskName);
        if (name.Length == 0 || name.Length > 200) return KioskResult.Fail("Bad task name");

        var today = StoreTime.StoreNow(store.State, clock.GetUtcNow()).Date;
        await using var db = contexts.Create(AccessContext.ForKiosk(store.StoreId));
        var existing = await db.AdminTaskChecks
            .FirstOrDefaultAsync(c => c.StoreId == store.StoreId && c.Date == today && c.TaskName == name, ct);
        if (isChecked)
        {
            // An upsert: a second tick of a ticked task changes nothing.
            if (existing is not null) return KioskResult.Success;
            db.AdminTaskChecks.Add(new AdminTaskCheck { StoreId = store.StoreId, Date = today, TaskName = name, CheckedAt = clock.GetUtcNow() });
            var saved = await SaveAsync(db, ct);
            // Two taps racing to tick the same task: the other one's row is the tick.
            if (!saved.Ok && await TickExistsAsync(store.StoreId, today, name, ct)) return KioskResult.Success;
            return saved;
        }
        if (existing is null) return KioskResult.Success;
        db.AdminTaskChecks.Remove(existing);
        return await SaveAsync(db, ct);
    }

    private async Task<bool> TickExistsAsync(Guid storeId, DateOnly day, string name, CancellationToken ct)
    {
        await using var db = contexts.Create(AccessContext.ForKiosk(storeId));
        return await db.AdminTaskChecks.AnyAsync(c => c.StoreId == storeId && c.Date == day && c.TaskName == name, ct);
    }

    // A failed write answers with its message, as the TypeScript's
    // { ok: false, error: error.message } does; it never takes the screen down.
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

    // What Postgres answered for an id that is not a uuid.
    private static KioskResult BadUuid(string value) => KioskResult.Fail($"invalid input syntax for type uuid: \"{value}\"");
}
