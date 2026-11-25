// Port of the assembly half of src/app/floor/[token]/page.tsx: from the rows the
// page reads to what FloorScreen and WalkNotes draw. Pure, so it is tested
// without a database; FloorService does the reads.

using System.Text.RegularExpressions;
using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Planning;
using Roster.Engine.Walk;

namespace Roster.Web.Features.Floor;

/// <summary>A generated_shifts row as the Floor Display reads it (times in minutes).</summary>
public sealed record FloorShiftRow(
    string Id, string? EmployeeId, string EmployeeName, string Role, double Start, double End, double BreakMinutes);

/// <summary>A shift_task_blocks row (time in minutes).</summary>
public sealed record FloorBlockRow(string ShiftId, double Time, string Group);

/// <summary>An employees row: position and pay band (premium time) and start date (pallet minutes).</summary>
public sealed record FloorPersonRow(string Id, string? PositionCode, int? SmYearBand, DateOnly? StartDate);

/// <summary>The day's weekly trading hours row (minutes), when the store has one.</summary>
public sealed record FloorTradingHours(double? Open, double? Close, bool Closed);

/// <summary>Everything page.tsx reads before it draws, in its own shapes.</summary>
public sealed record FloorReads
{
    public required string Token { get; init; }
    public required string StoreName { get; init; }
    public required string StoreCode { get; init; }
    public required string? KioskToken { get; init; } // the store's own kiosk link token
    public required bool NoLoginNeeded { get; init; }
    public required DateOnly Day { get; init; }
    public required DateOnly LocalDate { get; init; } // the store's today
    public required int LocalMinutes { get; init; } // the store's time now
    public required bool DateGiven { get; init; } // the link carried a (valid) ?date=
    public required bool ShowNotes { get; init; }
    public required IReadOnlyList<FloorShiftRow> Shifts { get; init; } // ordered by start time
    public required IReadOnlyList<FloorBlockRow> Blocks { get; init; }
    public required IReadOnlyList<AdminTaskRule> AdminTaskRules { get; init; }
    public required double McoRegisters { get; init; }
    public required double ScoLanes { get; init; }
    public required double EmployeeCount { get; init; }
    public required FloorTradingHours? Hours { get; init; }
    public required string? RegionName { get; init; }
    public required bool PublicHoliday { get; init; }
    public required IReadOnlyList<LoadTypeRow> LoadTypes { get; init; }
    public required IReadOnlyList<DailyLoadRow> Loads { get; init; }
    public required IReadOnlyList<LoadHistoryRow> LoadHistory { get; init; }
    public required IReadOnlyList<string> CheckedNames { get; init; }
    public required IReadOnlyList<WalkNote> Notes { get; init; } // open ones, and today's completed ones
    public required IReadOnlyList<FloorPersonRow> People { get; init; }
    public required bool Approved { get; init; }
    public required bool Drifted { get; init; }
    public required DateTimeOffset? ApprovedAt { get; init; }
    public required DateTimeOffset? RegeneratedAt { get; init; }
}

public static class FloorAssembly
{
    /// <summary>The link the page was opened with, keeping its ?date= (keepDate in the TypeScript).</summary>
    public static string KeepDate(FloorReads r, string? view)
    {
        var qs = new List<string>();
        if (view is not null) qs.Add($"view={view}");
        if (r.DateGiven) qs.Add($"date={r.Day.ToIso()}");
        var path = $"/floor/{Uri.EscapeDataString(r.Token)}";
        return qs.Count > 0 ? $"{path}?{string.Join("&", qs)}" : path;
    }

    /// <summary>Raw shifts for the shared roster display (persisted blocks carry no Till split).</summary>
    public static List<RawShift> RawShifts(FloorReads r)
    {
        var blocksByShift = new JsMap<string, List<RawBlock>>();
        foreach (var b in r.Blocks)
        {
            if (!blocksByShift.Has(b.ShiftId)) blocksByShift.Set(b.ShiftId, []);
            blocksByShift.Get(b.ShiftId)!.Add(new RawBlock(b.Time, b.Group));
        }
        return r.Shifts
            .Select(sh => new RawShift(sh.Id, sh.EmployeeName, sh.Role, sh.Start, sh.End, sh.BreakMinutes,
                blocksByShift.Get(sh.Id) ?? []))
            .ToList();
    }

    /// <summary>
    /// Allocation targets: whoever is on TODAY, one entry per person even if they
    /// hold more than one shift.
    /// </summary>
    public static List<CrewOption> Crew(FloorReads r)
    {
        var byId = new JsMap<string, CrewOption>();
        foreach (var sh in r.Shifts.Where(sh => !string.IsNullOrEmpty(sh.EmployeeId)))
            byId.Set(sh.EmployeeId!, new CrewOption(sh.EmployeeId!, sh.EmployeeName));
        return Js.Sorted(byId.Values(), (a, b) => Js.LocaleCompare(a.Name, b.Name));
    }

    /// <summary>The region's 3-digit prefix ("501 - Somewhere" -> "501"), which keys region-varying schedules.</summary>
    public static string? RegionPrefix(string? regionName)
    {
        if (regionName is null) return null;
        var m = Regex.Match(regionName, "^([0-9]{3})");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Today's named admin tasks, resolved with the same engine the Plan uses.
    /// Window anchors: trading hours from the weekly template; staffed bounds
    /// from the day's actual shifts (fallback: an hour either side of trading).
    /// </summary>
    public static List<ResolvedAdminTask> AdminTasksFor(FloorReads r, IReadOnlyList<RawShift> rawShifts)
    {
        if (r.Hours is not { Closed: false, Open: { } tradingOpen, Close: { } tradingClose }) return [];
        return AdminTasks.ResolveAdminTasks(r.AdminTaskRules, new AdminTaskContext
        {
            Date = r.Day,
            RegionPrefix = RegionPrefix(r.RegionName),
            EmployeeCount = r.EmployeeCount,
            RegisterCount = r.McoRegisters,
            ScoCount = r.ScoLanes,
            TradingOpen = tradingOpen,
            TradingClose = tradingClose,
            StaffOpen = rawShifts.Count > 0 ? Js.Min(rawShifts.Select(sh => sh.Start)) : tradingOpen - 60,
            StaffClose = rawShifts.Count > 0 ? Js.Max(rawShifts.Select(sh => sh.End)) : tradingClose + 60,
        });
    }

    public static FloorHeaderInfo Header(FloorReads r)
    {
        var isToday = r.Day == r.LocalDate;
        return new FloorHeaderInfo(
            isToday ? $"{FloorModel.ShortDate(r.Day)} · {FloorModel.Clock12(r.LocalMinutes)}" : FloorModel.ShortDate(r.Day),
            $"{r.StoreName} · {r.StoreCode}",
            !isToday)
        { NoLoginNeeded = r.NoLoginNeeded };
    }

    public static DriftNote? Drift(FloorReads r) => r.Drifted
        ? new DriftNote(
            r.ApprovedAt is { } a ? Dates.AuDateTime(a) : "—",
            r.RegeneratedAt is { } g ? Dates.AuDateTime(g) : "—")
        : null;

    /// <summary>The walk notes view: no clock-driven content, so no auto-refresh either.</summary>
    public static FloorNotesView NotesView(FloorReads r) => new(
        Header(r),
        Drift(r),
        KeepDate(r, null),
        r.Notes.Where(n => n.CompletedAt is null).ToList(),
        Crew(r),
        r.LocalDate,
        r.Notes.Where(n => n.CompletedAt is not null && n.RaisedDate == r.LocalDate).ToList());

    /// <summary>
    /// The hourly-paid people on the roster (store assistants and juniors), one
    /// position per person, for the premium-time shading.
    /// </summary>
    public static List<string> PremiumPositions(FloorReads r)
    {
        var peopleById = new JsMap<string, FloorPersonRow>();
        foreach (var p in r.People) peopleById.Set(p.Id, p);
        var positionByPerson = new JsMap<string, string>();
        foreach (var sh in r.Shifts)
        {
            if (!PremiumTime.HourlyRoles.Contains(sh.Role)) continue;
            var p = sh.EmployeeId is { Length: > 0 } id ? peopleById.Get(id) : null;
            positionByPerson.Set(string.IsNullOrEmpty(sh.EmployeeId) ? sh.Id : sh.EmployeeId, Positions.PayKeyFor(p?.PositionCode, sh.Role, p?.SmYearBand));
        }
        return positionByPerson.Values().ToList();
    }

    /// <summary>Whether the day shows a published roster: only an APPROVED day does.</summary>
    public static bool HasRoster(FloorReads r, IReadOnlyList<RosterRow> rosterRows) => rosterRows.Count > 0 && r.Approved;

    /// <summary>The wall screen, given the day's premium bands (none when there is no roster or no rates).</summary>
    public static FloorScreenView ScreenView(FloorReads r, IReadOnlyList<RawShift> rawShifts, IReadOnlyList<RosterRow> rosterRows,
        IReadOnlyList<PremiumBand> premium)
    {
        var isToday = r.Day == r.LocalDate;
        var hasRoster = HasRoster(r, rosterRows);
        var drift = Drift(r);

        // The truck, planned as the Load page plans it (crew from the approved
        // roster only).
        var firstLoadRunning = new Dictionary<string, double>();
        foreach (var sh in rawShifts)
        {
            var lr = sh.RawBlocks.Where(b => b.Group == TaskGroups.LoadRunning).Select(b => b.Time).ToList();
            if (lr.Count > 0) firstLoadRunning[sh.Key] = Js.Min(lr);
        }
        var startDates = new Dictionary<string, DateOnly?>();
        foreach (var p in r.People) startDates[p.Id] = p.StartDate;
        var load = FloorLoad.SummariseLoad(new LoadInputs(
            r.Day,
            hasRoster ? r.Shifts.Select(s => new LoadShiftRow(s.Id, s.EmployeeId, s.EmployeeName)).ToList() : [],
            firstLoadRunning,
            startDates,
            r.LoadTypes,
            r.Loads,
            r.LoadHistory));

        double? now = isToday ? r.LocalMinutes : null;
        var rows = hasRoster
            ? rosterRows.Select(row => new ScreenRow(row.Key, row.Name, row.Badge, row.Start, row.End,
                FloorModel.TaskRuns(row.Blocks), now is { } n ? FloorModel.NowNext(row, n) : null)).ToList()
            : [];

        var emptyMessage = hasRoster
            ? null
            : rosterRows.Count > 0
                ? new EmptyMessage("This day's roster has not been approved yet.", "It shows here once the Store Manager approves it.")
                : new EmptyMessage($"No approved roster for {(isToday ? "today" : "this day")}.", "");

        var adminTasks = AdminTasksFor(r, rawShifts)
            .Select(t => new ChecklistTask(t.Name, Js.Round(t.Minutes), $"{Grid.MinToTime(t.WindowStart)}–{Grid.MinToTime(t.WindowEnd)}"))
            .ToList();

        return new FloorScreenView(
            Header(r),
            drift,
            rows,
            emptyMessage,
            FloorModel.DaySpan(rows.Select(x => (x.Start, x.End)).ToList()),
            now,
            FloorModel.RowScaleFor(rows.Count, drift is not null),
            premium,
            adminTasks,
            r.CheckedNames,
            isToday,
            r.Notes.Where(n => n.CompletedAt is null).ToList(),
            r.LocalDate,
            KeepDate(r, "notes"),
            load,
            // The store's own kiosk link (Admin -> Kiosk Links): one token per
            // store opens both its Floor Display and its Load Planner, so this is
            // the token the screen was opened with. The Load page always shows
            // today, so another day's strip is not a link. ?from=floor lets its
            // "Floor Display" button close the tab this opens, back to here.
            isToday && !string.IsNullOrEmpty(r.KioskToken) ? $"/load/{Uri.EscapeDataString(r.KioskToken)}?from=floor" : null);
    }

    /// <summary>The premium-time bands for the hourly-paid positions on the day's rates.</summary>
    public static List<PremiumBand> PremiumBands(IEnumerable<PayRateRow> rates, string dayType, IReadOnlyList<string> positions) =>
        PremiumTime.PremiumBands(PremiumTime.PremiumHours(rates, dayType, positions))
            .Select(b => new PremiumBand(b.Start, b.End))
            .ToList();
}
