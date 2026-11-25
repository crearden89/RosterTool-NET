// Port of src/lib/stage2/g999.fixture.ts.
// ─── G999 audit fixture - REAL inputs, frozen 25/07/2026 (plan v14) ─────────
// A verbatim export of everything the fortnight roster consumes for store
// G999, period 2026-08-03 → 2026-08-16: the team with contracts, the award
// break rules, approved leave, the fixed roster, the weekend-off markers as
// the manager set them, granted day-offs, school days, and the exact slot
// inventory the shaper cut from the benchmark. G999 is the seeded TEST store.
//
// Purpose: this fixture lets the REAL engine run against the REAL store
// offline - the audit harness (G999Audit) replays it, and regression tests can
// assert fortnight-level outcomes (contract attainment) on true data instead
// of toy scenarios. Availability is omitted: every row in the store is
// "available, no window", which is the engine's default for a missing row.

using Roster.Engine.Core;
using Roster.Engine.Rostering.Shaper;

namespace Roster.Engine.Tests.Rostering.Fortnight;

public sealed record G999Employee(string Id, string Name, string Role, double ContractHours);
public sealed record G999Leave(string EmployeeId, string Start, string End);
public sealed record G999Fixed(string EmployeeId, int Dow, double Start, double End);
public sealed record G999Marker(string EmployeeId, string WeekendStart, bool SatOff, bool SunOff);
public sealed record G999DayOff(string EmployeeId, string Date);

public sealed record G999Slot(string Id, string Role, double Start, double End)
{
    public PlanSlot ToPlanSlot() => new(Id, Role, Start, End);
}

public sealed record G999Day(string Date, double StaffOpen, double StaffClose, double TradingOpen, double TradingClose, bool Closed, IReadOnlyList<G999Slot> Slots);

public static class G999Fixture
{
    public const string PeriodStart = "2026-08-03";

    public static readonly IReadOnlyList<G999Employee> Employees =
    [
        new("a76eec7d-261c-4add-8830-56568e14c973", "Noah Reyes", "SM", 90),
        new("34986aaa-2847-4505-aeca-d21f6779ef2f", "Harvey Chen", "ASM", 90),
        new("781a5e57-3c74-4f6e-aa3f-0d4960bff475", "Rory Nolan", "DSM", 70),
        new("b22f891d-fff0-4ad2-9443-c9aae847f188", "Simon Dixon", "DSM", 70),
        new("13f322f6-7343-49f6-b77d-e8452bddadd5", "Marcus Turner", "RSM", 60),
        new("9b2cd304-7cfa-4330-be05-c5282ef284b6", "Adam Mahoney", "SA", 50),
        new("f16821a4-4185-410f-bc57-f26a8b2f3f9d", "Angus Collins", "SA", 50),
        new("92de7e83-9869-4c75-a74b-6c55628279a1", "Ella Bishop", "SA", 50),
        new("8a6b9a4b-8dfa-4055-b82b-f03d17834dfc", "Rhys Brooks", "SA", 50),
        new("9efec109-2b51-48ef-876a-0b9a5b88efdd", "Blake Cooper", "SA", 40),
        new("f78474ca-6f44-4a44-a9b2-f4270524bc3b", "Stuart Ramsay", "SA", 40),
        new("b4450157-57b5-4659-a5e3-1b569f3a5ce7", "Cara Mackay", "SA", 30),
        new("d42a4642-d1c3-4746-a125-d95939fa4f32", "Grace Shaw-Fletcher", "SA", 30),
        new("a96d49bc-e2fa-4182-8345-95ca038db67a", "Sarah Bailey", "SA", 30),
        new("cb401e6c-94fa-442a-a7ba-ee8bb481be2c", "Annie Morgan", "SA", 21),
        new("ad808db5-e5c1-41da-96b9-566c70435455", "Megan Carter", "SA", 21),
        new("f075c757-892d-45df-9512-1c9b85945e56", "Cody Sutton", "JSA", 0),
        new("37400ae9-2999-4c1f-acea-6e0691594f7c", "Miles Murphy", "JSA", 0),
        new("57158fce-449d-410b-b2e9-b4fa67335be9", "Tessa Marsh", "JSA", 0),
    ];

    // break_rules verbatim (note: max_shift_hours caps PAID hours).
    public static readonly IReadOnlyDictionary<string, BreakRule> BreakRules = new Dictionary<string, BreakRule>
    {
        ["SM"] = new() { MinShiftHours = 3, MaxShiftHours = 12, GapHours = 0, BreakType = "flat_30_unpaid", WeeklyCap = 45, ConsecDayCap = 6, Ot15AfterHours = null, Ot19AfterHours = null },
        ["ASM"] = new() { MinShiftHours = 3, MaxShiftHours = 12, GapHours = 0, BreakType = "flat_30_unpaid", WeeklyCap = 45, ConsecDayCap = 6, Ot15AfterHours = null, Ot19AfterHours = null },
        ["DSM"] = new() { MinShiftHours = 3, MaxShiftHours = 9, GapHours = 11, BreakType = "flat_30_unpaid", WeeklyCap = 38, ConsecDayCap = 6, Ot15AfterHours = null, Ot19AfterHours = null },
        ["RSM"] = new() { MinShiftHours = 3, MaxShiftHours = 9, GapHours = 11, BreakType = "tiered", WeeklyCap = 38, ConsecDayCap = 6, Ot15AfterHours = 9.5, Ot19AfterHours = null },
        ["SA"] = new() { MinShiftHours = 3, MaxShiftHours = 9, GapHours = 11, BreakType = "tiered", WeeklyCap = 38, ConsecDayCap = 6, Ot15AfterHours = 9.5, Ot19AfterHours = null },
        ["JSA"] = new() { MinShiftHours = 3, MaxShiftHours = 6, GapHours = 11, BreakType = "tiered", WeeklyCap = 15, ConsecDayCap = 6, Ot15AfterHours = 9.5, Ot19AfterHours = null },
    };

    public static readonly IReadOnlyList<G999Leave> Leave =
    [
        new("f78474ca-6f44-4a44-a9b2-f4270524bc3b", "2026-07-06", "2026-08-16"), // Stuart
        new("8a6b9a4b-8dfa-4055-b82b-f03d17834dfc", "2026-08-03", "2026-08-16"), // Rhys
        new("92de7e83-9869-4c75-a74b-6c55628279a1", "2026-08-03", "2026-08-16"), // Ella
        new("ad808db5-e5c1-41da-96b9-566c70435455", "2026-08-03", "2026-08-09"), // Megan wk1
    ];

    // Grace's fixed roster: Mon/Tue 06:00-15:00, Thu 12:00-21:00.
    public static readonly IReadOnlyList<G999Fixed> Fixed =
    [
        new("d42a4642-d1c3-4746-a125-d95939fa4f32", 0, 360, 900),
        new("d42a4642-d1c3-4746-a125-d95939fa4f32", 1, 360, 900),
        new("d42a4642-d1c3-4746-a125-d95939fa4f32", 3, 720, 1260),
    ];

    // Weekend-off markers as saved on the Weekends page (only sat/sun TRUE rows
    // matter; false rows are "works that day").
    public static readonly IReadOnlyList<G999Marker> Markers =
    [
        new("a76eec7d-261c-4add-8830-56568e14c973", "2026-08-08", true, true), // Noah wk1
        new("34986aaa-2847-4505-aeca-d21f6779ef2f", "2026-08-15", true, true), // Harvey wk2
        new("781a5e57-3c74-4f6e-aa3f-0d4960bff475", "2026-08-08", true, true), // Rory wk1
        new("b22f891d-fff0-4ad2-9443-c9aae847f188", "2026-08-15", true, true), // Simon wk2
        new("13f322f6-7343-49f6-b77d-e8452bddadd5", "2026-08-15", true, true), // Marcus wk2
        new("9b2cd304-7cfa-4330-be05-c5282ef284b6", "2026-08-08", true, true), // Adam wk1
        new("f16821a4-4185-410f-bc57-f26a8b2f3f9d", "2026-08-15", true, true), // Angus wk2
        new("9efec109-2b51-48ef-876a-0b9a5b88efdd", "2026-08-15", true, true), // Blake wk2
        new("b4450157-57b5-4659-a5e3-1b569f3a5ce7", "2026-08-08", true, true), // Cara wk1
        new("8a6b9a4b-8dfa-4055-b82b-f03d17834dfc", "2026-08-15", true, true), // Rhys (on leave anyway)
    ];

    public static readonly IReadOnlyList<G999DayOff> DayOffs =
    [
        new("13f322f6-7343-49f6-b77d-e8452bddadd5", "2026-08-06"), // Marcus
        new("a96d49bc-e2fa-4182-8345-95ca038db67a", "2026-08-08"), // Sarah
    ];

    public static readonly IReadOnlySet<string> SchoolDays = new HashSet<string>
    {
        "2026-08-03", "2026-08-04", "2026-08-05", "2026-08-06", "2026-08-07", "2026-08-10", "2026-08-11", "2026-08-12", "2026-08-13", "2026-08-14",
    };

    // The shaper's slot inventory, verbatim from plan snapshot v14 (12/08 closed).
    public static readonly IReadOnlyList<G999Day> Days =
    [
        new("2026-08-03", 360, 1230, 510, 1200, false, [new("mgr-open", "MGR", 360, 780), new("mgr-close", "MGR", 660, 1230), new("sa-1", "SA", 360, 630), new("sa-2", "SA", 360, 720), new("sa-3", "SA", 450, 870), new("sa-4", "SA", 750, 1080), new("sa-5", "SA", 720, 1230)]),
        new("2026-08-04", 360, 1230, 510, 1200, false, [new("mgr-open", "MGR", 360, 780), new("mgr-close", "MGR", 660, 1230), new("sa-1", "SA", 360, 630), new("sa-2", "SA", 360, 930), new("sa-3", "SA", 435, 975), new("sa-4", "SA", 870, 1215), new("sa-5", "SA", 690, 1230)]),
        new("2026-08-05", 360, 1230, 510, 1200, false, [new("mgr-open", "MGR", 360, 780), new("mgr-close", "MGR", 660, 1230), new("sa-1", "SA", 360, 630), new("sa-2", "SA", 360, 720), new("sa-3", "SA", 375, 885), new("sa-4", "SA", 750, 1095), new("sa-5", "SA", 705, 1230)]),
        new("2026-08-06", 360, 1290, 510, 1260, false, [new("mgr-open", "MGR", 360, 840), new("mgr-close", "MGR", 780, 1260), new("sa-1", "SA", 360, 630), new("sa-2", "SA", 360, 840), new("sa-3", "SA", 360, 870), new("sa-4", "SA", 585, 915), new("sa-5", "SA", 870, 1095), new("sa-6", "SA", 810, 1260)]),
        new("2026-08-07", 360, 1230, 510, 1200, false, [new("mgr-open", "MGR", 360, 780), new("mgr-close", "MGR", 660, 1230), new("sa-1", "SA", 360, 720), new("sa-2", "SA", 360, 930), new("sa-3", "SA", 495, 1065), new("sa-4", "SA", 750, 1080), new("sa-5", "SA", 810, 1230), new("sa-6", "SA", 990, 1230)]),
        new("2026-08-08", 360, 1170, 510, 1140, false, [new("mgr-open", "MGR", 360, 750), new("mgr-close", "MGR", 630, 1170), new("sa-1", "SA", 360, 630), new("sa-2", "SA", 360, 690), new("sa-3", "SA", 420, 840), new("sa-4", "SA", 360, 930), new("sa-6", "SA", 840, 1170), new("sa-7", "SA", 705, 1170), new("jsa-1", "JSA", 720, 1050)]),
        new("2026-08-09", 360, 1170, 510, 1140, false, [new("mgr-open", "MGR", 360, 750), new("mgr-close", "MGR", 630, 1170), new("sa-1", "SA", 360, 690), new("sa-2", "SA", 405, 960), new("sa-3", "SA", 915, 1140), new("sa-4", "SA", 660, 1170)]),
        new("2026-08-10", 360, 1230, 510, 1200, false, [new("mgr-open", "MGR", 360, 780), new("mgr-close", "MGR", 660, 1230), new("sa-1", "SA", 360, 630), new("sa-2", "SA", 360, 720), new("sa-3", "SA", 705, 900), new("sa-4", "SA", 450, 1020), new("sa-5", "SA", 750, 1110), new("sa-6", "SA", 930, 1230)]),
        new("2026-08-11", 360, 1230, 510, 1200, false, [new("mgr-open", "MGR", 360, 780), new("mgr-close", "MGR", 690, 1230), new("sa-1", "SA", 360, 705), new("sa-2", "SA", 360, 915), new("sa-3", "SA", 390, 960), new("sa-4", "SA", 750, 960), new("sa-5", "SA", 870, 1200), new("sa-6", "SA", 870, 1230)]),
        new("2026-08-12", 0, 0, 0, 0, true, []),
        new("2026-08-13", 360, 1290, 510, 1260, false, [new("mgr-open", "MGR", 360, 840), new("mgr-close", "MGR", 780, 1260), new("sa-1", "SA", 360, 840), new("sa-2", "SA", 360, 900), new("sa-3", "SA", 525, 960), new("sa-4", "SA", 870, 1110), new("sa-5", "SA", 810, 1260)]),
        new("2026-08-14", 360, 1230, 510, 1200, false, [new("mgr-open", "MGR", 360, 780), new("mgr-close", "MGR", 660, 1230), new("sa-1", "SA", 360, 720), new("sa-2", "SA", 360, 930), new("sa-3", "SA", 390, 960), new("sa-4", "SA", 750, 1050), new("sa-5", "SA", 810, 1230), new("sa-6", "SA", 870, 1230)]),
        new("2026-08-15", 360, 1170, 510, 1140, false, [new("mgr-open", "MGR", 360, 750), new("mgr-close", "MGR", 630, 1170), new("sa-1", "SA", 360, 630), new("sa-2", "SA", 360, 690), new("sa-3", "SA", 360, 885), new("sa-4", "SA", 405, 975), new("sa-6", "SA", 660, 1170), new("sa-7", "SA", 885, 1170), new("jsa-1", "JSA", 705, 1065)]),
        new("2026-08-16", 360, 1170, 510, 1140, false, [new("mgr-open", "MGR", 360, 750), new("mgr-close", "MGR", 630, 1170), new("sa-1", "SA", 360, 630), new("sa-2", "SA", 360, 690), new("sa-3", "SA", 510, 1050), new("sa-4", "SA", 705, 1170), new("sa-5", "SA", 975, 1170)]),
    ];
}
