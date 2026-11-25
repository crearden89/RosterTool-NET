// Port of src/lib/closeRules.ts.
// ─── Times that follow the close ────────────────────────────────────────────
// Pure constants shared by the store-day resolver, Stage 1 and the Admin task
// resolver, so the staffed day and the windows that hang off the close always
// agree.

namespace Roster.Engine.Core;

public static class CloseRules
{
    /// <summary>
    /// The close-down: the staffed day runs this long after the doors close, every
    /// day, with cover - a manager on and nobody alone (owner decision 2,
    /// 12/11/2026). It was 15 minutes, 30 on Tuesday and Friday, which left no room
    /// for the after-close Admin (End of Day, UKG End of Day, the cash-up's tail).
    /// </summary>
    public const double CloseDownMin = 30;

    /// <summary>
    /// A window that opens later than this long before close opens this long
    /// before close instead (owner decisions 2, 9 and 12): Specials, evening
    /// Cleaning and the "after 17:00" date checks. A 17:00 close gets them from
    /// 15:00, an 18:00 close from 16:00; a 19:00 close or later is unchanged.
    /// Without it a 17:00 close left Specials one quarter-hour, and Stage 1 stacked
    /// the whole day's Specials into it (review RC-1).
    /// </summary>
    public const double FollowCloseMin = 120;

    /// <summary><paramref name="start"/>, or FollowCloseMin before <paramref name="close"/> if that is earlier.</summary>
    public static double FollowClose(double start, double close) => Math.Min(start, close - FollowCloseMin);

    /// <summary>
    /// Evening Cleaning runs to the end of the close-down (owner, 14/11/2026). The
    /// national evening window ends at a clock time (21:00), so on a 21:00 close
    /// the second person kept in for the close-down (decision 2) had nothing they
    /// were allowed to do. An evening Cleaning window - one still open in the last
    /// FollowCloseMin of trading - that ends before the staffed day does is now
    /// followed by a Cleaning window from its end to the end of the staffed day.
    /// That window is make-up-only: Stage 1 plans no Cleaning there of its own, so
    /// the day's Cleaning hours and their placement are as before; the painter may
    /// do Cleaning there, the day checker and hand edits accept it, and the shaper
    /// may level Cleaning into it for the people who must be on (CloseDownFrom,
    /// where the window used to end). The quoted morning Cleaning hour (it carries
    /// hours) is never touched, and a window that already reaches the end of the
    /// staffed day (a 20:00 close) gets nothing added.
    /// </summary>
    public static List<WindowRule> EveningCleaningToCloseDown(WindowRule w, double tradingClose, double staffClose)
    {
        if (w.Group != "cleaning" || (w.Hours ?? 0) > 0) return [w];
        if (w.End <= tradingClose - FollowCloseMin || w.End >= staffClose) return [w];
        return [w, new WindowRule { Group = "cleaning", Start = w.End, End = staffClose, MakeUpOnly = true, CloseDownFrom = w.End }];
    }
}
