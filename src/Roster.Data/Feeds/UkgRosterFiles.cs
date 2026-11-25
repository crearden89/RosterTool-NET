// Port of src/app/api/export/ukg/route.ts and src/app/api/print/ukg-roster/route.ts: what
// the two routes read and the files they build. The HTTP side (who may ask, the query
// string) is in Roster.Web/Features/Feeds.

using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Roster.Data.Services.Planning;
using Roster.Data.Services.RefData;
using Roster.Data.Services.SubTasks;
using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.RosterOps;
using Roster.Engine.Rostering.Fortnight;
using Roster.Engine.SubTasks;

namespace Roster.Data.Feeds;

/// <summary>A file to send: its text and the name to save it under.</summary>
public sealed record UkgExportFile(string Csv, string FileName);

/// <summary>The approved fortnight roster for UKG: the import CSV and the printed roster.</summary>
public interface IUkgRosterFiles
{
    /// <summary>
    /// The approved roster for the fortnight as a UKG-style import CSV (spec 11.8 - manual
    /// import into UKG, no live sync in Phase 1). <paramref name="startText"/> is the query's
    /// yyyy-mm-dd, echoed into the file name; the fortnight runs from <paramref name="start"/>.
    /// Reads as the signed-in user, so a store outside their scope exports no rows.
    /// </summary>
    Task<UkgExportFile> ExportCsvAsync(string storeId, string startText, DateOnly start, CancellationToken ct = default);

    /// <summary>
    /// The print-optimised HTML fortnight roster; with <paramref name="showSubtasks"/> each
    /// shift also shows the sub-task codes the person works, in order, under a legend.
    /// </summary>
    Task<string> PrintHtmlAsync(string storeId, DateOnly start, bool showSubtasks, CancellationToken ct = default);
}

public sealed class UkgRosterFiles(
    IRosterDbContextFactory contexts, IStoreDayLoader storeDays, IRefData refData, IPublishedSubtasks published) : IUkgRosterFiles
{
    /// <summary>
    /// The query's start date as the TypeScript read it: <c>new Date(start + "T00:00:00")</c>
    /// rolls a day past the month's end into the next month (2026-02-30 is 2 March) and
    /// refuses a month or day out of range (null: the route then failed).
    /// </summary>
    public static DateOnly? FortnightStart(string start)
    {
        if (start.Length != 10 || start[4] != '-' || start[7] != '-') return null;
        if (!int.TryParse(start.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) ||
            !int.TryParse(start.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var m) ||
            !int.TryParse(start.AsSpan(8, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var d)) return null;
        if (y < 1 || m < 1 || m > 12 || d < 1 || d > 31) return null;
        return new DateOnly(y, m, 1).AddDays(d - 1);
    }

    private static List<DateOnly> Fortnight(DateOnly start) => Enumerable.Range(0, 14).Select(start.AddDays).ToList();

    private static Guid? IdOf(string storeId) => Guid.TryParse(storeId, out var id) ? id : null;

    private static string Hhmm(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static double Min(TimeOnly t) => Grid.TimeToMin(t.ToString("HH:mm:ss", CultureInfo.InvariantCulture));

    // ─── GET /api/export/ukg ────────────────────────────────────────────────────

    public async Task<UkgExportFile> ExportCsvAsync(string storeId, string startText, DateOnly start, CancellationToken ct = default)
    {
        var dates = Fortnight(start);
        string? code = null;
        var shifts = new List<(DateOnly Date, string EmployeeName, string Role, TimeOnly Start, TimeOnly End)>();
        if (IdOf(storeId) is { } id)
        {
            await using var db = contexts.Create();
            code = await db.Stores.AsNoTracking().Where(s => s.Id == id).Select(s => s.Code).FirstOrDefaultAsync(ct);
            var rows = await db.GeneratedShifts.AsNoTracking().Where(s => s.StoreId == id && dates.Contains(s.Date))
                .OrderBy(s => s.Date).ThenBy(s => s.StartTime).ThenBy(s => s.Id)
                .Select(s => new { s.Date, s.EmployeeName, s.Role, s.StartTime, s.EndTime })
                .ToListAsync(ct);
            shifts.AddRange(rows.Select(s => (s.Date, s.EmployeeName, s.Role, s.StartTime, s.EndTime)));
        }

        const string header = "StoreCode,Date,Employee,Position,StartTime,EndTime,UnpaidBreakMins";
        var lines = new List<string> { header };
        foreach (var s in shifts)
        {
            // UKG's payroll needs the UNPAID break minutes (the deducted portion), not
            // the total time off the floor stored in generated_shifts.break_minutes.
            var span = Min(s.End) - Min(s.Start);
            var unpaidBreak = Breaks.BreakForRole(s.Role, span / 60).UnpaidMinutes;
            lines.Add(string.Join(",",
                CsvCell(code ?? ""), s.Date.ToIso(), CsvCell(s.EmployeeName), CsvCell(s.Role), Hhmm(s.Start), Hhmm(s.End),
                Js.NumberToString(unpaidBreak)));
        }
        return new UkgExportFile(string.Join("\n", lines), $"ukg_{code ?? "store"}_{startText}.csv");
    }

    // Spreadsheets EVALUATE any imported cell starting with = + - @ (or a
    // tab/CR) - quoting does not stop them. This file exists to be opened in
    // Excel by payroll, and employee names are free text, so every text cell is
    // neutralised with a leading apostrophe before it is quoted.
    internal static string CsvCell(string v)
    {
        var neutral = v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + v : v;
        return "\"" + neutral.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    // ─── GET /api/print/ukg-roster ──────────────────────────────────────────────
    // A print-optimised HTML fortnight roster: one row per team member, first
    // columns Name / Contract Hrs / Rostered Hrs, then the 14 days across the top
    // with each person's shift filling the matrix. Fixed one page wide (landscape),
    // as many pages tall as the team needs. A Saturday or Sunday the Weekends page
    // marks someone off prints with black diagonal lines through their cell, so on
    // a sick call the manager can see who can't be called in. With subtasks on
    // (the Roster page's "Show sub-tasks" on when Print was pressed) each shift
    // also shows the sub-task codes the person works, in order, and a small
    // legend of the codes heads the page; without it the print is as it always was.

    private static readonly string[] Dow = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    internal static string Esc(string s) =>
        s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);

    private static string Ddmm(DateOnly d) => $"{d.Day:00}/{d.Month:00}";

    // The weekend-off lines: black 45-degree strokes 6px apart, drawn once and
    // reused by every struck-through cell (each cell's SVG clips it to size).
    // Vector strokes rather than a CSS background: Chrome prints a background
    // gradient as a coarse grey bitmap, and drops it entirely when the print
    // dialog's "Background graphics" is off - strokes print crisp and always.
    private static readonly string OffLines = string.Concat(Enumerable.Range(0, 84).Select(i =>
    {
        var x = -100 + i * 6;
        return $"M{x.ToString(CultureInfo.InvariantCulture)} 100L{(x + 100).ToString(CultureInfo.InvariantCulture)} 0";
    }));

    private const string OffSvg = """<svg class="hatch" aria-hidden="true"><use href="#off-lines"/></svg>""";

    // Position order first (SM → ASM → DSM → RSM → SA → JSA), alphabetical within.
    private static readonly string[] RoleOrder = ["SM", "ASM", "DSM", "RSM", "SA", "JSA"];

    private sealed record Leave(DateOnly Start, DateOnly End, string Type, double? DayHours);

    public async Task<string> PrintHtmlAsync(string storeId, DateOnly start, bool showSubtasks, CancellationToken ct = default)
    {
        var dates = Fortnight(start);
        var id = IdOf(storeId);

        (string Code, string Name)? store = null;
        var emps = new List<(Guid Id, string Name, string Role, double ContractHours, double? NotionalHours)>();
        var shifts = new List<(Guid EmployeeId, string Role, DateOnly Date, TimeOnly Start, TimeOnly End)>();
        var leaveRows = new List<(Guid EmployeeId, Leave Leave)>();
        var markerRows = new List<WeekendMarker>();
        var empIds = new List<Guid>();
        if (id is { } sid)
        {
            await using var db = contexts.Create();
            var s = await db.Stores.AsNoTracking().Where(x => x.Id == sid).Select(x => new { x.Code, x.Name }).FirstOrDefaultAsync(ct);
            if (s is not null) store = (s.Code, s.Name);
            var e = await db.Employees.AsNoTracking().Where(x => x.StoreId == sid && x.Active)
                .OrderBy(x => x.Name).ThenBy(x => x.Id)
                .Select(x => new { x.Id, x.Name, x.Role, x.ContractHours, x.NotionalHours }).ToListAsync(ct);
            emps.AddRange(e.Select(x => (x.Id, x.Name, x.Role, x.ContractHours, x.NotionalHours)));
            var sh = await db.GeneratedShifts.AsNoTracking().Where(x => x.StoreId == sid && dates.Contains(x.Date))
                .OrderBy(x => x.Date).ThenBy(x => x.StartTime).ThenBy(x => x.Id)
                .Select(x => new { x.EmployeeId, x.Role, x.Date, x.StartTime, x.EndTime }).ToListAsync(ct);
            shifts.AddRange(sh.Select(x => (x.EmployeeId, x.Role, x.Date, x.StartTime, x.EndTime)));

            // Approved leave overlapping the fortnight, and the Weekends page's markers for
            // the fortnight's weekends (weekend_markers, keyed by each weekend's Saturday).
            empIds = emps.Select(x => x.Id).ToList();
            if (empIds.Count > 0)
            {
                var saturdays = dates.Select(WeekendOff.WeekendSaturday).Where(d => d is not null).Select(d => d!.Value).Distinct().ToList();
                var (first, last) = (dates[0], dates[13]);
                var l = await db.EmployeeLeave.AsNoTracking()
                    .Where(x => empIds.Contains(x.EmployeeId) && x.StartDate <= last && x.EndDate >= first)
                    .OrderBy(x => x.StartDate).ThenBy(x => x.Id)
                    .Select(x => new { x.EmployeeId, x.StartDate, x.EndDate, x.LeaveType, x.DayHours }).ToListAsync(ct);
                leaveRows.AddRange(l.Select(x => (x.EmployeeId, new Leave(x.StartDate, x.EndDate, x.LeaveType ?? "ANN", x.DayHours))));
                var m = await db.WeekendMarkers.AsNoTracking()
                    .Where(x => saturdays.Contains(x.WeekendStart) && empIds.Contains(x.EmployeeId))
                    .Select(x => new { x.EmployeeId, x.WeekendStart, x.SatOff, x.SunOff }).ToListAsync(ct);
                markerRows.AddRange(m.Select(x => new WeekendMarker(x.EmployeeId.ToString(), x.WeekendStart, x.SatOff, x.SunOff)));
            }
        }

        // Same closed-day rule as Stage 1: trading hours / special hours OR a
        // Sales-page override.
        var closedSet = new HashSet<DateOnly>();
        var holidays = new HashSet<DateOnly>();
        if (id is { } storeGuid)
        {
            var overrides = await refData.GetSalesOverrideMapAsync(storeGuid, ct);
            foreach (var d in dates)
            {
                var day = await storeDays.ResolveStoreDayAsync(storeGuid, d, ct);
                if (day.Closed || (overrides.TryGetValue(d, out var o) && o.Closed)) closedSet.Add(d);
            }
            holidays.UnionWith(await refData.GetHolidaySetAsync(storeGuid, ct));
        }

        // A leave WEEKDAY shows its type code (ANN/LS/BRV/SCK/CAR/UNP) and credits the
        // entry's per-day hours - or the employee's notional hours for a full contract
        // day - so someone away for the whole period lands on contract rather than
        // reading as completely un-rostered. Weekends never show or credit leave, closed
        // days credit nothing (the person stays short - client decision), and public
        // holidays credit the notional instead via the PH rule below. UNP blocks the day
        // but adds nothing to the paid Rostered column.
        var leaveByEmp = leaveRows.GroupBy(r => r.EmployeeId).ToDictionary(g => g.Key, g => g.Select(r => r.Leave).ToList());
        Leave? LeaveOn(Guid empId, DateOnly date) =>
            leaveByEmp.TryGetValue(empId, out var list) ? list.FirstOrDefault(l => l.Start <= date && date <= l.End) : null;

        // date -> the people marked off that day (empty on weekdays).
        var offOn = dates.ToDictionary(d => d, d => WeekendOff.MarkedOffOn(d, markerRows));
        var anyOff = offOn.Values.Any(ids => empIds.Any(e => ids.Has(e.ToString())));

        // Sub-task codes per person-day, only when asked for. A label must never
        // stop the roster printing: an unreadable label prints the times alone.
        JsMap<string, List<string>> runs = new();
        if (showSubtasks && id is { } runStore)
        {
            try
            {
                runs = await published.PublishedSubtaskRunsAsync(runStore, start, dates, ct: ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                runs = new JsMap<string, List<string>>();
            }
        }

        // employee_id → date → shift
        var byEmpDate = new Dictionary<Guid, Dictionary<DateOnly, (TimeOnly Start, TimeOnly End)>>();
        var rosteredMin = new Dictionary<Guid, double>();
        foreach (var s in shifts)
        {
            if (!byEmpDate.TryGetValue(s.EmployeeId, out var byDate)) byEmpDate[s.EmployeeId] = byDate = [];
            byDate[s.Date] = (s.Start, s.End);
            // Rostered (paid) hours deduct only the unpaid break portion, not the full
            // off-floor break, matching the roster grid's Hrs column.
            var span = Min(s.End) - Min(s.Start);
            var unpaid = Breaks.BreakForRole(s.Role, span / 60).UnpaidMinutes;
            rosteredMin[s.EmployeeId] = rosteredMin.GetValueOrDefault(s.EmployeeId) + Math.Max(0, span - unpaid);
        }

        var headCells = string.Concat(dates.Select(d =>
        {
            var weekend = Period.DowMon0(d) >= 5;
            return $"""<th class="day{(weekend ? " weekend" : "")}"><div>{Dow[Period.DowMon0(d)]}</div><div class="date">{Ddmm(d)}</div></th>""";
        }));

        var sortedEmps = Js.Sorted(emps, (a, b) =>
            ((double)(Array.IndexOf(RoleOrder, a.Role) - Array.IndexOf(RoleOrder, b.Role))).Or(Js.LocaleCompare(a.Name, b.Name)));

        var bodyRows = string.Concat(sortedEmps.Select(e =>
        {
            var notional = FortnightEngine.NotionalHoursOf(e.ContractHours, e.NotionalHours);
            // Paid credit outside worked shifts: leave weekdays (excluding closed
            // days and public holidays) and public holidays not worked (notional).
            double creditHours = 0;
            string CellFor(DateOnly d)
            {
                if (byEmpDate.TryGetValue(e.Id, out var byDate) && byDate.TryGetValue(d, out var sh))
                {
                    runs.TryGet($"{e.Id}:{d.ToIso()}", out var codes);
                    return $"{Hhmm(sh.Start)}–{Hhmm(sh.End)}" +
                           (codes is { Count: > 0 } ? $"""<div class="subs">{string.Join(" ", codes)}</div>""" : "");
                }
                if (holidays.Contains(d))
                {
                    if (notional > 0)
                    {
                        creditHours += notional;
                        return """<span class="leave">PH</span>""";
                    }
                    return "";
                }
                var lv = LeaveOn(e.Id, d);
                if (lv is null || Period.DowMon0(d) >= 5 || closedSet.Contains(d)) return "";
                if (lv.Type != "UNP") creditHours += lv.DayHours ?? notional;
                return $"""<span class="leave">{Esc(lv.Type)}</span>""";
            }
            var dayCells = string.Concat(dates.Select(d =>
            {
                var weekend = Period.DowMon0(d) >= 5;
                var content = CellFor(d);
                if (offOn[d].Has(e.Id.ToString()))
                {
                    // Marked off: the lines fill the cell. Anything printed in it (a
                    // shift someone was given anyway, a PH) sits on a white label so
                    // it stays readable - and the clash shows.
                    return $"""<td class="shift weekend off" title="Marked off on the Weekends page">{OffSvg}{(content.Length > 0 ? $"""<span class="over">{content}</span>""" : "")}</td>""";
                }
                return $"""<td class="shift{(weekend ? " weekend" : "")}">{content}</td>""";
            }));
            var rostered = rosteredMin.GetValueOrDefault(e.Id) / 60 + creditHours;
            return $"""
<tr>
        <td class="name">{Esc(e.Name)}</td>
        <td class="role">{Esc(e.Role)}</td>
        <td class="num">{Js.ToFixed(e.ContractHours, 1)}</td>
        <td class="num">{Js.ToFixed(rostered, 1)}</td>
        {dayCells}
      </tr>
""";
        }));

        var code = store?.Code ?? "";
        var name = store?.Name;
        var title = $"UKG Roster - {code} {name ?? ""} - {Ddmm(dates[0])} to {Ddmm(dates[13])}";

        var key = anyOff
            ? $"""
<svg width="0" height="0" style="position:absolute" aria-hidden="true"><defs><path id="off-lines" d="{OffLines}"/></defs></svg>
  <p class="key"><span class="swatch">{OffSvg}</span>Black diagonal lines: marked off for that weekend day on the Weekends page - not available to call in</p>
"""
            : "";
        var legend = showSubtasks
            ? "<p class=\"codes\">Sub-tasks, in the order each person works them: " + string.Join(" · ", Catalogue.SubtaskGroups.Select(g =>
                $"<b>{Esc(LabourGroups.ByKey[g].Label)}</b> " +
                string.Join(", ", Catalogue.SubtasksOf(g).Select(t => $"{t.Code} {Esc(t.Label)}")))) + "</p>"
            : "";
        var cols = string.Concat(dates.Select(_ => "<col class=\"c-day\" />"));
        var body = bodyRows.Length > 0 ? bodyRows : """<tr><td colspan="18" style="padding:16px">No active team members.</td></tr>""";

        return $$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>{{Esc(title)}}</title>
<style>
  :root { --navy:#00005F; --border:#c9ccd6; --tint:#e6e6ef; }
  * { box-sizing: border-box; }
  body { margin: 0; padding: 16px; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Arial, sans-serif; color: var(--navy); }
  h1 { font-size: 15px; margin: 0 0 2px; }
  .sub { font-size: 11px; color: #555; margin: 0 0 12px; }
  .toolbar { margin-bottom: 12px; }
  button { border: 1px solid var(--navy); background: var(--navy); color: #fff; border-radius: 6px; padding: 6px 14px; font-size: 12px; font-weight: 600; cursor: pointer; }
  /* Fixed layout: every day column is exactly the same width regardless of
     content, so the fortnight grid lines up page after page. */
  table { border-collapse: collapse; width: 100%; font-size: 10px; table-layout: fixed; }
  col.c-name { width: 11%; }
  col.c-role { width: 4.5%; }
  col.c-num { width: 5.5%; }
  col.c-day { width: calc(73.5% / 14); }
  th, td { border: 1px solid var(--border); padding: 3px 4px; text-align: center; overflow: hidden; text-overflow: ellipsis; }
  thead th { background: var(--navy); color: #fff; font-weight: 600; }
  th.day .date { font-weight: 400; opacity: .85; }
  td.name { text-align: left; font-weight: 600; white-space: nowrap; }
  td.role { color: #555; }
  td.num { font-variant-numeric: tabular-nums; }
  td.shift { font-variant-numeric: tabular-nums; white-space: nowrap; }
  .leave { font-weight: 700; letter-spacing: .04em; color: #7D3F98; }
  .weekend { background: var(--tint); }
  thead th.weekend { background: #1a1a73; }
  tbody tr:nth-child(even) td:not(.weekend) { background: #f6f7fb; }
  /* Weekend day marked off on the Weekends page: black diagonal lines (an SVG
     filling the cell - see OFF_LINES). Anything printed in the cell sits on a
     white label above them; the label's white is forced to print, as
     browsers leave backgrounds off a printout by default. */
  td.off { position: relative; background: #fff; }
  svg.hatch { position: absolute; inset: 0; width: 100%; height: 100%; stroke: #000; stroke-width: 1; fill: none; }
  td.off .over { position: relative; background: #fff; padding: 0 2px; -webkit-print-color-adjust: exact; print-color-adjust: exact; }
  .key { display: flex; align-items: center; gap: 6px; font-size: 10px; color: #333; margin: -6px 0 12px; }
  .subs { font-size: 8px; color: #333; white-space: normal; line-height: 1.15; letter-spacing: .02em; }
  .codes { font-size: 9px; color: #333; margin: -6px 0 12px; }
  .codes b { color: var(--navy); }
  .key .swatch { position: relative; display: inline-block; width: 22px; height: 11px; border: 1px solid var(--border); overflow: hidden; }
  @media print {
    body { padding: 0; }
    .toolbar { display: none; }
    thead { display: table-header-group; }
    tr { break-inside: avoid; }
    @page { size: A4 landscape; margin: 10mm; }
  }
</style>
</head>
<body>
  <h1>{{Esc(name ?? "Roster")}} <span style="font-weight:400">({{Esc(code)}})</span></h1>
  <p class="sub">Fortnight {{Ddmm(dates[0])}} - {{Ddmm(dates[13])}} · UKG roster export</p>
  {{key}}
  {{legend}}
  <div class="toolbar"><button onclick="window.print()">Print</button></div>
  <table>
    <colgroup>
      <col class="c-name" /><col class="c-role" /><col class="c-num" /><col class="c-num" />
      {{cols}}
    </colgroup>
    <thead>
      <tr>
        <th class="name" style="text-align:left">Name</th>
        <th>Role</th>
        <th>Contract Hrs (F/N)</th>
        <th>Rostered Hrs</th>
        {{headCells}}
      </tr>
    </thead>
    <tbody>
      {{body}}
    </tbody>
  </table>
  <script>window.addEventListener("load", function () { setTimeout(function () { window.print(); }, 250); });</script>
</body>
</html>
""";
    }
}
