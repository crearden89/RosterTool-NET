// Port of src/app/(app)/plan/review/ReviewView.tsx (its state and logic; the markup is in
// ReviewView.razor).
// ─── The fortnight's review (proposal 3, approved 15/11/2026) ────────────────
// Every check of the fortnight on the page in one list, by day, errors first,
// with filters; a severity pill and a "Checked" switch on each; progress cards
// and a submit bar that says what still blocks submitting; the day as a side
// sheet of cards. Only the presentation is new: the checks, the notes an
// error needs, the hard stops, who may submit, approve, query and unlock, and
// every server action and notification are the ones the page always used.

using System.Globalization;
using Microsoft.AspNetCore.Components;
using Roster.Engine.Core;
using Roster.Engine.Planning;
using Roster.Web.Components.Layout;
using Roster.Web.Components.Ui;

namespace Roster.Web.Features.Review;

public sealed partial class ReviewView : IDisposable
{
    private enum Perspective { Store, Reviewer, Viewer }
    private enum StoreDayState { Todo, Checked, Clear, Queried }
    private enum LockForm { Request, Unlock }
    private enum BannerTone { Info, Wait, Back, Good }
    private sealed record Banner(BannerTone Tone, string Title, string? Body = null, string? Quote = null);

    [Inject] private IReviewService Service { get; set; } = default!;
    [Inject] private ShellEvents Shell { get; set; } = default!;

    [Parameter, EditorRequired] public ReviewData Data { get; set; } = default!;
    [Parameter] public IReadOnlyList<QueueStore> Queue { get; set; } = [];
    [Parameter] public bool CanStore { get; set; }
    [Parameter] public bool CanReview { get; set; }

    /// <summary>May unlock an approved plan: the area's EMSO, a DSO or a super user (R5).</summary>
    [Parameter] public bool CanUnlock { get; set; }

    [Parameter] public int UnreadNotices { get; set; }

    /// <summary>A link straight to one day (?day=) opens its sheet.</summary>
    [Parameter] public DateOnly? InitialDay { get; set; }

    /// <summary>The page reads the review again (router.refresh()).</summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    private bool _pending;
    private string? _error;
    private string? _flash;
    private CancellationTokenSource? _flashTimer;
    private DateOnly? _openDate;
    private readonly Dictionary<string, string> _drafts = [];
    private DateOnly? _queryOpen;
    private bool _composing;
    // Unlocking an approved plan (owner decision R5): the store asks, the EMSO
    // or above unlocks - each saying why.
    private LockForm? _lockForm;
    private string _lockReason = "";
    // The list: which checks show, the error whose note is being written in the
    // list, warnings whose optional note box is open in the sheet, and the note
    // an error was switched on without.
    private CheckFilter _filter = CheckFilter.All;
    private string? _inlineNote;
    private bool _focusInlineNote;
    private ElementReference _inlineNoteBox;
    private readonly HashSet<string> _warnNotes = [];
    private readonly Dictionary<string, string> _noteErr = [];
    private readonly Dictionary<string, ElementReference> _sheetNoteBoxes = [];
    private (int Unread, Guid StoreId, DateOnly PeriodStart)? _noticesSeen;

    // ── Derived on every render (Compute) ──
    private Guid _sid;
    private DateOnly _ps;
    private ReviewStatus _s;
    private bool _stale;
    private bool _storeEditable;
    private bool _reviewerEditable;
    private Perspective _perspective;
    private string _reviewers = "";
    private bool _storeActs;
    private ReviewProgress _p = default!;
    private List<ReviewCheckView> _checks = [];
    private List<ReviewDayView> _queried = [];
    private bool _storeReady;
    private List<string> _blockers = [];
    private FilterCounts _counts = default!;
    private List<CheckGroup> _groups = [];
    private ReviewDayView? _day;
    private int _dayIdx = -1;
    private Banner _banner = default!;
    private List<Step> _steps = [];

    protected override void OnInitialized() =>
        _openDate = InitialDay is { } d && Data.Days.Any(x => x.Date == d) ? d : null;

    // Opening the review clears its notices from your bell.
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusInlineNote)
        {
            _focusInlineNote = false;
            try { await _inlineNoteBox.FocusAsync(); } catch (InvalidOperationException) { }
        }
        var seen = (UnreadNotices, Data.StoreId, Data.PeriodStart);
        if (_noticesSeen == seen) return;
        _noticesSeen = seen;
        if (UnreadNotices <= 0) return;
        try
        {
            await Service.MarkReviewNoticesReadAsync(Data.StoreId, Data.PeriodStart);
            Shell.RaiseUnreadChanged();
            await OnChanged.InvokeAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Left unread; the next visit tries again.
        }
    }

    private void Compute()
    {
        var data = Data;
        (_sid, _ps, _s, _stale) = (data.StoreId, data.PeriodStart, data.Status, data.Stale);
        _storeEditable = CanStore && (_s is ReviewStatus.InProgress or ReviewStatus.Returned || (_s == ReviewStatus.Submitted && _stale));
        _reviewerEditable = CanReview && _s == ReviewStatus.Submitted && !_stale;
        // Whose words the page speaks in. A super user is whichever side is up.
        _perspective = CanStore && CanReview
            ? (_reviewerEditable ? Perspective.Reviewer : Perspective.Store)
            : CanStore ? Perspective.Store : CanReview ? Perspective.Reviewer : Perspective.Viewer;
        var names = string.Join(", ", data.ReviewerNames);
        _reviewers = data.ReviewerFallback ? "the super users" : names.Length > 0 ? names : "your EMSO";
        _storeActs = _storeEditable && _perspective == Perspective.Store;

        _p = CheckList.ReviewProgress(data.Days);
        _checks = [.. data.Days.SelectMany(d => d.Checks)];
        _queried = [.. data.Days.Where(d => d.Verdict == DayVerdict.Query)];
        _storeReady = data.HasSnapshot && _p.Done == _checks.Count && (_s != ReviewStatus.Returned || _p.Answered == _queried.Count);
        _blockers = CheckList.SubmitBlockers(_p, data.HasSnapshot, _s);
        _counts = CheckList.FilterCounts(data.Days);
        _groups = CheckList.CheckGroups(data.Days, _filter);

        _day = _openDate is { } open ? data.Days.FirstOrDefault(d => d.Date == open) : null;
        _dayIdx = _day is null ? -1 : IndexOf(data.Days, _day);
        _banner = BannerFor(data);
        _steps = StepsFor(data);
    }

    private static int IndexOf(IReadOnlyList<ReviewDayView> days, ReviewDayView day)
    {
        for (var i = 0; i < days.Count; i++)
            if (ReferenceEquals(days[i], day)) return i;
        return -1;
    }

    private StoreDayState StoreDay(ReviewDayView d)
    {
        if (_s == ReviewStatus.Returned && d.Verdict == DayVerdict.Query && string.IsNullOrEmpty(d.Reply)) return StoreDayState.Queried;
        if (d.Checks.Count == 0) return StoreDayState.Clear;
        return d.Checks.All(c => c.Done) ? StoreDayState.Checked : StoreDayState.Todo;
    }

    private bool NeedsMe(ReviewDayView d)
    {
        if (_storeEditable && _perspective == Perspective.Store) return StoreDay(d) is StoreDayState.Todo or StoreDayState.Queried;
        if (_reviewerEditable) return d.Verdict is null;
        return false;
    }

    private void OpenDay(DateOnly date)
    {
        _queryOpen = null;
        _openDate = date;
    }

    private void CloseDay() => _openDate = null;

    // ── Running a step ──

    private async Task Run(Func<Task<ReviewResult>> fn, Action<ReviewResult>? done = null, string? ok = null)
    {
        _error = null;
        _pending = true;
        try
        {
            var r = await fn();
            if (!r.Ok)
            {
                _error = r.Error ?? "That didn't work. Try again.";
                return;
            }
            done?.Invoke(r);
            if (ok is not null) ShowFlash(ok);
            await OnChanged.InvokeAsync();
        }
        finally
        {
            _pending = false;
        }
    }

    private void ShowFlash(string text)
    {
        _flash = text;
        _flashTimer?.Cancel();
        var cts = _flashTimer = new CancellationTokenSource();
        _ = ClearFlashLater(cts.Token);
    }

    private async Task ClearFlashLater(CancellationToken ct)
    {
        try
        {
            await Task.Delay(3500, ct);
            await InvokeAsync(() =>
            {
                _flash = null;
                StateHasChanged();
            });
        }
        catch (OperationCanceledException) { }
    }

    private const string ReturnDraft = "return";

    private string Draft(string k) => _drafts.GetValueOrDefault(k) ?? "";
    private void SetDraft(string k, string v) => _drafts[k] = v;
    private void ClearDraft(string k) => _drafts.Remove(k);
    private void ClearNoteErr(string key) => _noteErr.Remove(key);
    private static bool Blank(string s) => Js.Trim(s).Length == 0;

    // ─── a check: switched on, switched off ───────────────────────────────────
    // Switching a check on is the old "Mark checked": the note typed for it goes
    // with it, and an error can't go without one (the action refuses it too).
    private async Task MarkChecked(ReviewCheckView c, bool inList)
    {
        var k = $"n:{c.Key}";
        if (c.Severity == CheckSeverity.Error && Blank(Draft(k)))
        {
            _noteErr[c.Key] = "An error needs a note saying why it's OK.";
            if (inList)
            {
                if (_inlineNote != c.Key) _focusInlineNote = true;
                _inlineNote = c.Key;
            }
            else if (_sheetNoteBoxes.TryGetValue(k, out var box))
            {
                try { await box.FocusAsync(); } catch (InvalidOperationException) { }
            }
            return;
        }
        ClearNoteErr(c.Key);
        await Run(() => Service.CheckFlagAsync(_sid, _ps, c.Key, Draft(k)), done: _ =>
        {
            ClearDraft(k);
            if (_inlineNote == c.Key) _inlineNote = null;
        });
    }

    // Switching it off is the old "Undo": the note comes back to edit.
    private Task Unmark(ReviewCheckView c) =>
        Run(() => Service.UncheckFlagAsync(_sid, _ps, c.Key), done: r => SetDraft($"n:{c.Key}", r.Note ?? ""));

    // The sheet's "Save and next day": the day's notes are checked with their
    // checks, one by one, then the next day opens. A refusal stops there, with
    // what was typed still in its box.
    private async Task SaveNotesThen(IReadOnlyList<ReviewCheckView> list, Action then)
    {
        var items = list.Select(c => (c.Key, Note: Draft($"n:{c.Key}"))).ToList();
        _error = null;
        _pending = true;
        try
        {
            foreach (var it in items)
            {
                var r = await Service.CheckFlagAsync(_sid, _ps, it.Key, it.Note);
                if (!r.Ok)
                {
                    _error = r.Error ?? "That didn't work. Try again.";
                    await OnChanged.InvokeAsync();
                    return;
                }
                ClearDraft($"n:{it.Key}");
            }
            ShowFlash($"{Plural(items.Count, "check", "checks")} saved.");
            await OnChanged.InvokeAsync();
            then();
        }
        finally
        {
            _pending = false;
        }
    }

    private Task OnCheckSwitch(ReviewCheckView c, bool inList, bool on) => on ? MarkChecked(c, inList) : Unmark(c);

    private void OpenInlineNote(string key)
    {
        _inlineNote = key;
        _focusInlineNote = true;
    }

    // ── The EMSO's decisions ──

    private Task MarkFine(DateOnly date) =>
        Run(() => Service.SetDayVerdictAsync(_sid, _ps, date, DayVerdict.Ok), done: _ => _queryOpen = null);

    private void OpenQuery(ReviewDayView day)
    {
        _queryOpen = day.Date;
        SetDraft($"q:{day.Date.ToIso()}", _drafts.GetValueOrDefault($"q:{day.Date.ToIso()}") ?? day.Query ?? "");
    }

    private string QueryDraft(ReviewDayView day) => _drafts.GetValueOrDefault($"q:{day.Date.ToIso()}") ?? day.Query ?? "";

    private Task SaveQuery(ReviewDayView day) =>
        Run(() => Service.SetDayVerdictAsync(_sid, _ps, day.Date, DayVerdict.Query, QueryDraft(day)), done: _ =>
        {
            ClearDraft($"q:{day.Date.ToIso()}");
            _queryOpen = null;
        });

    private Task SendReply(ReviewDayView day) =>
        Run(() => Service.ReplyToQueryAsync(_sid, _ps, day.Date, Draft($"r:{day.Date.ToIso()}")), done: _ => ClearDraft($"r:{day.Date.ToIso()}"));

    private void StartReturn()
    {
        SetDraft(ReturnDraft, string.Join("\n", _queried.Select(d => $"{d.Dow} {d.DateLabel}: {d.Query}")));
        _composing = true;
    }

    private Task SendReturn() =>
        Run(() => Service.ReturnReviewAsync(_sid, _ps, Draft(ReturnDraft)), ok: "Returned. The store has been notified.", done: _ =>
        {
            _composing = false;
            ClearDraft(ReturnDraft);
        });

    private string HaveBeenNotified() =>
        Data.ReviewerFallback ? "The super users have" : $"{_reviewers} {(Data.ReviewerNames.Count > 1 ? "have" : "has")}";

    private Task Submit() =>
        Run(() => Service.SubmitReviewAsync(_sid, _ps), ok: $"Submitted. {HaveBeenNotified()} been notified.");

    private Task Withdraw() =>
        Run(() => Service.WithdrawReviewAsync(_sid, _ps), ok: "Withdrawn. Make your changes, then submit again.");

    private Task MarkUnflaggedFine() => Run(() => Service.MarkUnflaggedFineAsync(_sid, _ps));

    private Task Approve() =>
        Run(() => Service.ApproveReviewAsync(_sid, _ps), ok: "Approved. The store has been notified and can now generate its roster.");

    private Task Remind() => Run(() => Service.SendReminderAsync(_sid, _ps), ok: "Reminder sent to the store.");

    private void CloseLockForm()
    {
        _lockForm = null;
        _lockReason = "";
    }

    private void StartUnlock()
    {
        _lockReason = Data.UnlockRequest?.Reason ?? "";
        _lockForm = LockForm.Unlock;
    }

    private Task SendLock()
    {
        var unlocking = _lockForm == LockForm.Unlock;
        var reason = _lockReason;
        return Run(
            () => unlocking ? Service.UnlockReviewAsync(_sid, _ps, reason) : Service.RequestUnlockAsync(_sid, _ps, reason),
            ok: unlocking ? "Unlocked. The store has been notified." : $"Request sent. {HaveBeenNotified()} been notified.",
            done: _ => CloseLockForm());
    }

    // ─── banner ─────────────────────────────────────────────────────────────
    private static string By(string? name) => string.IsNullOrEmpty(name) ? "" : $" by {name}";

    private Banner BannerFor(ReviewData data)
    {
        var s = _s;
        if (!data.HasSnapshot)
            return new(BannerTone.Wait, "This fortnight's plan hasn't been generated yet.", "Generate it on the Plan page, then come back to review it.");
        if (_perspective == Perspective.Store)
        {
            if (s == ReviewStatus.Approved)
                return new(BannerTone.Good, $"Approved{By(data.DecidedBy)} · {data.DecidedAt ?? ""}.", "The plan is signed off and locked: generate the roster from it on the Roster page. To change the plan, ask for it to be unlocked below.");
            if (s == ReviewStatus.Returned)
                return new(BannerTone.Back, $"Returned{By(data.DecidedBy)} · {data.DecidedAt ?? ""}.", "Answer the query, then resubmit.", data.ReturnMessage);
            if (s == ReviewStatus.Submitted && _stale)
                return new(BannerTone.Wait, "The plan changed after you submitted it.", "Check any new flags, then submit it again.");
            if (s == ReviewStatus.Submitted)
                return new(BannerTone.Info, $"Submitted to {_reviewers} · {data.SubmittedAt ?? ""}.", "You'll get a notification when it's approved or returned. Withdraw it if you need to change the plan.");
            return new(BannerTone.Info, $"Check the plan's flagged days, then submit it to {_reviewers}.", $"Errors need a note saying why they're OK; warnings take one click. Due {data.DueLabel}. The roster can be built once the plan is approved.");
        }
        if (_perspective == Perspective.Reviewer)
        {
            if (s == ReviewStatus.Approved)
                return new(BannerTone.Good, $"Approved{By(data.DecidedBy)} · {data.DecidedAt ?? ""}.", data.UnlockRequest is not null ? "The store has asked for the plan to be unlocked - see below." : "The plan is locked. The store can now generate its roster from it.");
            if (s == ReviewStatus.Returned)
                return new(BannerTone.Wait, $"Returned to the store · {data.DecidedAt ?? ""}.", "Waiting for the store to answer the query and resubmit.");
            if (s == ReviewStatus.Submitted && _stale)
                return new(BannerTone.Wait, "The store changed its plan after submitting.", "Waiting for it to check the changes and submit again.");
            if (s == ReviewStatus.Submitted)
                return new(BannerTone.Info, $"Submitted{By(data.SubmittedBy)} · {data.SubmittedAt ?? ""}.", "Open each flagged day and read the store's notes. Mark days fine or query them, then approve the plan or return it.");
            return new(BannerTone.Wait, "Waiting for the store.", $"It hasn't submitted this fortnight yet (due {data.DueLabel}).");
        }
        var label = s switch
        {
            ReviewStatus.InProgress => "With the store",
            ReviewStatus.Submitted => "With the EMSO",
            ReviewStatus.Returned => "Returned to the store",
            _ => "Approved",
        };
        return new(
            s == ReviewStatus.Approved ? BannerTone.Good : BannerTone.Info,
            $"{label}.",
            CanUnlock && s == ReviewStatus.Approved ? "The plan is locked. You can unlock it below if it needs to change." : "You can see this review but not change it.");
    }

    private static string BannerClass(BannerTone tone) => tone switch
    {
        BannerTone.Info => "bg-[var(--ui-lavender)]",
        BannerTone.Wait => "bg-[var(--ui-amber-bg)]",
        BannerTone.Back => "bg-[var(--brand-red-tint)]",
        _ => "bg-green-50",
    };

    // ─── steps ──────────────────────────────────────────────────────────────
    private List<Step> StepsFor(ReviewData data)
    {
        var s = _s;
        var withEmso = s == ReviewStatus.Submitted && !_stale;
        return
        [
            new("Plan generated", "Plan generated", data.GeneratedAt ?? "Not yet", data.HasSnapshot ? StepState.Done : StepState.Current),
            new("Store review", "Store review",
                s == ReviewStatus.Returned
                    ? "Returned · answer the query"
                    : withEmso || s == ReviewStatus.Approved
                        ? $"Submitted {data.SubmittedAt ?? ""}"
                        : data.HasSnapshot
                            ? $"{_p.Done} of {_checks.Count} checked"
                            : $"Due {data.DueLabel}",
                !data.HasSnapshot ? StepState.Todo
                    : s == ReviewStatus.Returned ? StepState.Attention
                    : withEmso || s == ReviewStatus.Approved ? StepState.Done
                    : StepState.Current),
            new("EMSO review", "EMSO review",
                s == ReviewStatus.Approved ? $"Reviewed{By(data.DecidedBy)}" : withEmso ? $"With {_reviewers}" : _reviewers,
                s == ReviewStatus.Approved ? StepState.Done : withEmso ? StepState.Current : StepState.Todo),
            new("Approved", "Approved",
                s == ReviewStatus.Approved ? $"{data.DecidedAt ?? ""} · roster unlocked" : "Locks the plan, opens the roster",
                s == ReviewStatus.Approved ? StepState.Done : StepState.Todo),
        ];
    }

    private string EmptyList() =>
        _counts.All == 0
            ? Data.HasSnapshot ? "Nothing flagged this fortnight." : "No checks yet - they come with the plan."
            : _filter switch
            {
                CheckFilter.Errors => "No errors this fortnight.",
                CheckFilter.Warnings => "No warnings this fortnight.",
                _ => "Every check has been checked.",
            };

    // ── The reviewer's list of stores ──

    private static int Order(QueueStatus s) => s switch
    {
        QueueStatus.Submitted => 0,
        QueueStatus.Returned => 1,
        QueueStatus.InProgress => 2,
        QueueStatus.NotStarted => 3,
        _ => 4,
    };

    private List<QueueStore> SortedQueue() =>
        Js.Sorted(Queue, (a, b) => ((double)(Order(a.Status) - Order(b.Status))).Or(Js.LocaleCompare(a.Code, b.Code)));

    // ── Numbers as the TypeScript writes them ──

    private static string F2(double n) => Js.ToFixed(n, 2);

    private static string Sgn(double n)
    {
        var r = Js.Round(n * 100) / 100;
        return (r > 0 ? "+" : r < 0 ? "−" : "") + Js.ToFixed(Math.Abs(r), 2);
    }

    private static string Money(double? n) => n is { } v ? $"${Js.ToLocaleString(Js.Round(v / 1000))}k" : "–";

    private static string Dollars(double? n) => n is { } v ? $"${Js.ToLocaleString(Js.Round(v))}" : "–";

    // reduce((a, b) => a + b, 0): in order, one addition at a time.
    private static double Sum(IEnumerable<double> xs)
    {
        var t = 0.0;
        foreach (var x in xs) t += x;
        return t;
    }

    private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    private static bool OutsideTolerance(double diff) => Math.Abs(diff) > ReviewChecks.HoursTolerance + 1e-9;

    private static string Percent(double value) => Js.NumberToString(Js.Round(value * 100));

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private string PlanDayHref(DateOnly? date) => $"/plan/generate?store={_sid}&date={Iso(date ?? _ps)}";

    public void Dispose()
    {
        _flashTimer?.Cancel();
        _flashTimer?.Dispose();
    }
}
