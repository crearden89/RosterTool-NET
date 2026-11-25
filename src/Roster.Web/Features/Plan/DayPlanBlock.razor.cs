// Port of src/app/(app)/plan/generate/DayPlanBlock.tsx (its props and the values it works
// out before drawing).

using Microsoft.AspNetCore.Components;
using Roster.Engine.Core;
using Roster.Engine.Planning;
using Roster.Engine.Planning.Plan;
using Roster.Engine.Planning.Stage1;
using Roster.Engine.Rostering.Shaper;
using Roster.Engine.SubTasks;
using Roster.Web.Components.Ui;
using Roster.Web.Features.RosterPage;
using Roster.Web.Features.RosterPage.Grid;
using Curve = Roster.Engine.Planning.Plan.TradeCurve;

namespace Roster.Web.Features.Plan;

public partial class DayPlanBlock
{
    private sealed record RowSpec(string Key, string Label, string Color);

    // Display order = the fixed overlap priority (spec Section 7):
    // Checkout → Load Running → Reset → Admin → Specials → Cleaning.
    private static readonly IReadOnlyList<RowSpec> RowOrder =
    [
        new("checkout", "Checkout", LabourGroups.CheckoutColorLeft),
        new("load_running", LabourGroups.ByKey["load_running"].Label, LabourGroups.ByKey["load_running"].Color),
        new("reset", LabourGroups.ByKey["reset"].Label, LabourGroups.ByKey["reset"].Color),
        new("admin", LabourGroups.ByKey["admin"].Label, LabourGroups.ByKey["admin"].Color),
        new("specials", LabourGroups.ByKey["specials"].Label, LabourGroups.ByKey["specials"].Color),
        new("cleaning", LabourGroups.ByKey["cleaning"].Label, LabourGroups.ByKey["cleaning"].Color),
        // Non-Op is additive demand (extra hours the plan must cover), so it's a real
        // row counted in the Total below - not an informational aside.
        new("non_op", LabourGroups.ByKey["non_op"].Label, LabourGroups.ByKey["non_op"].Color),
    ];

    private static readonly IReadOnlyList<string> RowKeys = RowOrder.Select(r => r.Key).ToList();

    private readonly DayPlanEditor _edit = new();
    private bool _saving;

    // Groups opened into their sub-tasks in the Benchmark Hours table: their sub-task
    // rows show underneath, their tiles show the sub-task's code, and in edit mode their
    // sub-tasks join the paint chips.
    private readonly HashSet<string> _openGroups = [];

    // An edit ends with its day: moving to another day keeps this component and its
    // state, so an edit still open would show the last day's tiles under the new day's
    // heading, and Save would write them over that day.
    private DateOnly? _editDate;

    // Worked out before each render.
    private IReadOnlyList<PlanShift> _shifts = [];
    private PlanShiftComputation _shownPlan = PlanShifts.Empty();
    private int _managerShifts, _saShifts, _jsaShifts;
    private double _totalBreakHours;
    private Dictionary<string, double> _tileHours = new();
    private JsMap<string, double> _subHours = new();
    private double _rosteredTotal;
    private HoursCheck? _hours;
    private double _benchTotal;
    private JsMap<string, double> _benchRows = new();
    private double _dayVariance;
    private IReadOnlyList<(double Time, double Headcount)> _checkoutBlocks = [];
    private List<CheckRow> _checkRows = [];
    private bool _hasErrors;
    private HeatRow? _heatRow;
    private IReadOnlyList<LoadRunning.Period> _lrPeriods = [];

    [Parameter, EditorRequired] public DateOnly Date { get; set; }

    [Parameter, EditorRequired] public Stage1Computation Result { get; set; } = new();

    [Parameter, EditorRequired] public PlanShiftComputation Plan { get; set; } = PlanShifts.Empty();

    [Parameter, EditorRequired] public Guid StoreId { get; set; }

    [Parameter, EditorRequired] public DateOnly PeriodStart { get; set; }

    [Parameter] public bool CanEdit { get; set; }

    /// <summary>
    /// Peer view (another store's plan): shift grid and benchmark hours only - the Day Summary
    /// and Coverage Flags panels are not rendered. The page also strips the flags before they
    /// reach this component.
    /// </summary>
    [Parameter] public bool Peer { get; set; }

    /// <summary>
    /// The store's standing weekly trade pattern (all seven weekdays, bucketed to hours).
    /// Absent when the store has no quarter-hour data loaded.
    /// </summary>
    [Parameter] public TradeCurveWeek? TradeCurve { get; set; }

    /// <summary>The same pattern at 15 minutes, index 0 = Monday - heats the plan grid's own quarter-hour columns.</summary>
    [Parameter] public IReadOnlyList<IReadOnlyList<CurveShare>>? TradeQuarterHours { get; set; }

    /// <summary>The store may run its load at night (Store Attributes -> Night Fill).</summary>
    [Parameter] public bool NightFill { get; set; }

    /// <summary>The store's floor-cover rules, for the live hours check while editing.</summary>
    [Parameter] public CoverRules? Cover { get; set; }

    /// <summary>Called after an edit is saved, so the page reads the plan afresh (router.refresh()).</summary>
    [Parameter] public EventCallback OnSaved { get; set; }

    /// <summary>"Show sub-tasks" (the switch in the generate bar, as on the Roster page).</summary>
    [CascadingParameter] public SubtaskViewProvider? SubtaskView { get; set; }

    // "Show sub-tasks" opens all four groups at once: every tile shows its code, every
    // group's sub-task rows show, and each sub-task is a paint chip. Off, each group opens
    // on its own as above, and comes back as it was left.
    private bool ShowAll => SubtaskView?.On == true;

    private IReadOnlySet<string> ShownGroups => ShowAll ? ShowSubtasks.AllSubtaskGroups : _openGroups;

    protected override void OnParametersSet()
    {
        if (_editDate != Date)
        {
            _editDate = Date;
            _edit.Stop();
        }
    }

    // Everything the markup reads, from the plan or the draft as it stands (worked out at
    // the top of every render, as the TypeScript component does on each render).
    private void Compute()
    {
        _shifts = _edit.ShiftsOf(Plan);
        _shownPlan = _edit.Editing && _edit.Draft is not null ? Plan with { Shifts = _edit.Draft } : Plan;

        _managerShifts = _shifts.Count(s => s.PlanRole == RoleBadges.Mgr);
        _saShifts = _shifts.Count(s => s.PlanRole == RoleBadges.Sa);
        _jsaShifts = _shifts.Count(s => s.PlanRole == RoleBadges.Jsa);
        _totalBreakHours = _shifts.Aggregate(0.0, (s, x) => s + x.BreakMinutes) / 60;

        // WYSIWYG: the Allocated column counts the ACTUAL tiles on the shifts above
        // (0.25h each), so the table always reconciles with what's drawn - live while
        // editing. Checkout combines the split Registers/SCO tiles.
        _tileHours = new Dictionary<string, double>();
        foreach (var s in _shifts)
        {
            foreach (var b in s.Blocks)
            {
                var key = b.Group is "registers" or "scos" ? "checkout" : b.Group;
                if (key is TaskGroups.Break or TaskGroups.Flex) continue;
                _tileHours[key] = _tileHours.GetValueOrDefault(key) + 0.25;
            }
        }
        _subHours = Label.SubtaskTiles(_shifts.Select(s => (IEnumerable<DisplayBlock>)s.Blocks));
        _rosteredTotal = _tileHours.Values.Aggregate(0.0, (a, b) => a + b);

        // The day's hours check - one calculation for the status, this page and the
        // Review (owner decision R2): worked hours against the benchmark file. The held
        // day carries it; while editing it is worked out live from the draft.
        if (!(_edit.Editing && _edit.Draft is not null))
        {
            _hours = Plan.Hours;
        }
        else
        {
            var draft = _edit.Draft.Select(s => s.ToChecked()).ToList();
            var held = DayHours.HeldTimeOf(draft, Cover ?? DayHours.DefaultCover);
            _hours = DayHours.HoursCheckOf(Result, draft, new DayHours.HeldHours(
                Cover: held.Cover.Count / 4.0,
                Minimum: held.Minimum.Count / 4.0,
                Managers: held.Managers.Count / 4.0,
                Fixed: held.Fixed.Count / 4.0,
                Breaks: BreakTime.BreakTimeAdded(Plan.Notes)));
        }

        // The table's benchmark is the day's, the one the hours check holds it to, split
        // by group so the rows add up to it as shown (owner, 15/11/2026; BenchmarkSplit):
        // each group has Stage 1's share and Checkout the rest. In hundredths of an hour,
        // so each row's variance is the one its figures show.
        var benchTotal = _hours?.Benchmark ?? BenchmarkSplit.DayBenchmark(BenchmarkParts.Of(Result));
        (_benchTotal, _benchRows) = BenchmarkSplit.BenchmarkRows(BenchmarkParts.Of(Result), benchTotal, RowKeys);
        _dayVariance = Js.Round(_rosteredTotal * 100) - _benchTotal;
        _checkoutBlocks = Result.Blocks.Where(b => b.Group == "checkout").Select(b => (b.Time, b.Headcount)).ToList();

        // The day's checks as one list, graded as the Review grades them (owner decision
        // 8): every planning step's flags, errors first. The day hours check says so when
        // it is met; when it is not, its message is a flag.
        var checks = ReviewChecks.SortChecks(ReviewChecks.FlagsToChecks(Date, [.. Result.Flags, .. Plan.Flags]));
        _checkRows = [];
        if (_hours?.Status == "ok")
        {
            _checkRows.Add(CheckRow.Of(
                "hours",
                CheckTone.Ok,
                $"Day hours {Js.ToFixed(_hours.Worked, 2)} h worked against {Js.ToFixed(_hours.Benchmark, 2)} h benchmark ({PageFigures.Signed(_hours.Gap)} h) - within the 1-hour tolerance"));
        }
        foreach (var c in checks)
            _checkRows.Add(CheckRow.Of(c.Key, c.Severity == Severity.Error ? CheckTone.Error : CheckTone.Warning, c.Message, c.Detail));
        if (checks.Count == 0)
            _checkRows.Add(CheckRow.Of("none", CheckTone.Ok, "No coverage flags - demand met with a manager on the floor every block"));
        _hasErrors = checks.Any(c => c.Severity == Severity.Error);

        // The sales curve, heated quarter-hour by quarter-hour in the grid's own columns,
        // above the Front End row. One colour scale for the whole week - the same colour
        // means the same share of a day's trade whichever day the chips are on.
        _heatRow = null;
        var dow = Period.DowMon0(Date);
        var day = TradeQuarterHours is { } week && dow < week.Count ? week[dow] : [];
        if (day.Count > 0)
        {
            var thresholds = Curve.HeatThresholds(TradeQuarterHours!.SelectMany(d => d.Select(p => p.Share)));
            var cells = new Dictionary<double, HeatCell>();
            foreach (var p in day)
            {
                var step = Curve.HeatStep(p.Share, thresholds);
                if (step < 0) continue;
                cells[p.Time] = new HeatCell(Curve.HeatRamp[step], $"{Grid.MinToTime(p.Time)} - {Js.ToFixed(p.Share * 100, 2)}% of the day's trade");
            }
            _heatRow = new HeatRow(
                "Sales curve",
                cells,
                "How busy each quarter-hour is: the store's typical share of the day's trade (customers and items), from the POS quarter-hour statistics");
        }

        // The store's load running periods for the day - dark blue rules in the grid,
        // and Load Running outside them drawn as error tiles.
        _lrPeriods = LoadRunning.LoadRunningPeriods(Result);
    }

    private void StartEditing() => _edit.Start(Plan);

    private void AddShift() => _edit.AddShift(Result, DayPlanEditor.ManualSlotId(Clock.GetUtcNow()));

    private void CellClick(GridCell cell) => _edit.CellClick(Result, cell.RowKey, cell.Time);

    private void ToggleGroup(string g)
    {
        if (!_openGroups.Remove(g)) _openGroups.Add(g);
    }

    // A benchmark row: in edit mode it picks its group's tool; otherwise a group with
    // sub-tasks opens or closes.
    private void GroupRowClick(string rowTool, bool opens, string key)
    {
        if (_edit.Editing) _edit.SetTool(rowTool);
        else if (opens) ToggleGroup(key);
    }

    private void SubRowClick(string tool)
    {
        if (_edit.Editing) _edit.SetTool(tool);
    }

    private async Task SaveAsync()
    {
        if (_edit.Draft is null) return;
        _edit.Error = null;
        if (_edit.SaveProblem(Result) is { } problem)
        {
            _edit.Error = problem;
            return;
        }
        _saving = true;
        PlanResult res;
        try
        {
            res = await PlanService.EditPlanDayAsync(StoreId, PeriodStart, Date, _edit.Edits());
        }
        catch
        {
            // A failed call is a message, never the error page.
            res = new PlanResult(false, "The connection to the server dropped. Reload the page to see whether the changes were saved.");
        }
        _saving = false;
        if (!res.Ok)
        {
            _edit.Error = res.Error ?? "Could not save the edits";
            return;
        }
        _edit.Stop();
        await OnSaved.InvokeAsync();
    }

    private static string GroupColor(string g) => LabourGroups.ByKey[g].Color;

    // Dark text on the light group colours, as on the tiles.
    private static string TileText(string g) => g is "reset" or "specials" ? "var(--brand-navy)" : "#fff";

    // An opened group's own chip is spelt out ("Reset"), so it can't be taken for one of
    // its sub-tasks: it paints the group and leaves the label to the normal rules on save.
    private string ChipText(string g, string code) => ShownGroups.Contains(g) ? LabourGroups.ByKey[g].Label : code;

    private static RenderFragment Text(string s) => b => b.AddContent(0, s);
}
