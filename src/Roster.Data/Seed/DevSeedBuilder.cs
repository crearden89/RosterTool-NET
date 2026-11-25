// New: writes the development data set. Everything is invented: the organisation in
// SeedCatalogue, the people (names drawn from the lists below), the trade, the rosters.
// The rules defaults are the award values the TypeScript app's fixtures carry
// (src/lib/stage2/g999.fixture.ts break_rules, g999.fortnight.fixture.ts labour_group_rules,
// migration 0012's pay-rule thresholds). Seeded rosters are simple invented shifts, not
// the engine's: build a fortnight in the app to see the engine's.
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Users;
using static Roster.Engine.Core.DateOnlyExtensions;
using Dates = Roster.Engine.Core.Dates;
using Js = Roster.Engine.Core.Js;
using Period = Roster.Engine.Core.Period;

namespace Roster.Data.Seed;

internal sealed class DevSeedBuilder(RosterDbContext db, DateOnly today, FirstSuperUserOptions? firstSuperUser)
{
    private readonly SeedRandom _rnd = new(20261125);
    private readonly Dictionary<string, int> _counts = [];
    private readonly Dictionary<string, Guid> _groups = [], _regions = [], _areas = [], _users = [];
    private readonly List<SeedSite> _sites = [];
    private readonly Dictionary<string, Guid> _batches = [];
    private readonly Dictionary<DateOnly, Guid> _periods = [];

    private DateOnly Previous => Period.AddPeriods(Current, -1);
    private DateOnly Current => Period.CurrentPeriodStart(today);
    private DateOnly Planning => Period.AddPeriods(Current, 1);

    public IReadOnlyDictionary<string, int> Counts => _counts;
    public List<DevSeedLogin> Logins { get; } = [];
    public List<DevSeedKioskLink> KioskLinks { get; } = [];

    /// <summary>A seeded store and what the rest of the seed needs to know about it.</summary>
    private sealed class SeedSite
    {
        public required SeedStore Def { get; init; }
        public required Store Store { get; init; }
        public required int[] Open { get; init; } // minutes, Monday first
        public required int[] Close { get; init; }
        public required double Scale { get; init; }
        public required bool HasSco { get; init; }
        public List<Employee> Team { get; } = [];
        public HashSet<(Guid, DateOnly)> Away { get; } = [];
        public Dictionary<(Guid Id, int Dow, int Week), (int Start, int End)> Fixed { get; } = [];
        public Dictionary<Guid, int> CycleWeeks { get; } = [];
        public Dictionary<(Guid, int), (bool Available, int? From, int? To)> Availability { get; } = [];
        public int Number => int.Parse(Def.Code[1..]);
    }

    // ── Store profiles (invented): trading hours Monday..Sunday, size, checkouts ──

    private static readonly Dictionary<string, (string Open, string Close, double Scale, bool Sco, bool NewModel, int Mco, int ScoLanes, double Share, bool NightFill, int MaxLeave, string Opened)> Profiles = new()
    {
        // G001 keeps scripts/seed.mjs's store: Mon-Sat 07:00-21:00, Sun 09:00-18:00, SCO (new model).
        ["G001"] = ("07:00 07:00 07:00 07:00 07:00 07:00 09:00", "21:00 21:00 21:00 21:00 21:00 21:00 18:00", 1.00, true, true, 6, 4, 0.66, false, 3, "2012-03-14"),
        ["G102"] = ("08:30 08:30 08:30 08:30 08:30 08:30 09:00", "20:00 20:00 20:00 21:00 20:00 19:00 19:00", 0.90, true, false, 5, 6, 0.60, false, 3, "2016-08-02"),
        ["G215"] = ("08:00 08:00 08:00 08:00 08:00 08:00 09:00", "20:00 20:00 20:00 20:00 20:00 19:00 18:00", 1.15, false, false, 6, 0, 0.00, false, 3, "2009-11-20"),
        ["G218"] = ("08:30 08:30 08:30 08:30 08:30 08:30 09:00", "20:30 20:30 20:30 20:30 20:30 20:00 19:00", 1.05, true, true, 6, 6, 0.66, true, 3, "2019-05-07"),
        ["G327"] = ("08:00 08:00 08:00 08:00 08:00 08:00 09:00", "21:00 21:00 21:00 21:00 21:00 19:00 18:00", 0.85, true, true, 5, 4, 0.62, false, 2, "2014-02-25"),
        ["G340"] = ("08:00 08:00 08:00 08:00 08:00 08:00 11:00", "21:00 21:00 21:00 21:00 21:00 17:00 17:00", 0.80, true, false, 4, 4, 0.55, false, 2, "2021-10-12"),
    };

    /// <summary>Stores whose rosters are published: the previous and current fortnight (and G215 into the planning one).</summary>
    private static readonly HashSet<string> RosteredStores = ["G001", "G102", "G215"];

    private static readonly string[] FirstNames =
    [
        "Ari", "Bea", "Cal", "Dana", "Eli", "Fern", "Gus", "Hana", "Ivo", "Jade", "Kai", "Lena", "Milo", "Nell", "Otis",
        "Pia", "Quin", "Rhea", "Seb", "Tara", "Uma", "Vic", "Wren", "Xan", "Yara", "Zane", "Ada", "Bryn", "Cleo", "Dex",
        "Esme", "Finn", "Gia", "Hugo", "Iris", "Jem", "Kit", "Lulu", "Max", "Noor",
    ];

    private static readonly string[] Surnames =
    [
        "Abbott", "Barrow", "Calder", "Dunmore", "Ellery", "Fairley", "Garnet", "Hollis", "Ingram", "Jarvis", "Kettle",
        "Lowry", "Merritt", "Nash", "Oakes", "Pruett", "Quarry", "Redfern", "Sallow", "Thorne", "Upton", "Vance", "Whitlow",
        "Yardley", "Ashdown", "Bramble", "Corrie", "Dalby", "Eastwood", "Foley", "Gable", "Huxley", "Ivers", "Jessop",
        "Kember", "Linden", "Marlowe", "Norrell", "Orme", "Pellow",
    ];

    /// <summary>The team shape every store gets: role, fortnight contract, position (null = by band), name rank prefix.</summary>
    private static readonly (string Role, double Contract, string? Position, string Prefix)[] TeamShape =
    [
        (Roles.Sm, 90, null, "1-"), (Roles.Asm, 80, null, "2-"), (Roles.Dsm, 70, "DSM", "3-"), (Roles.Dsm, 70, "DSM", "3-"),
        (Roles.Rsm, 60, "RSM", "4-"), (Roles.Sa, 50, "SA-FF", "4-"), (Roles.Sa, 50, "SA-FF", "4-"), (Roles.Sa, 50, "SA-FF", "4-"),
        (Roles.Sa, 50, "SA-LR", "5L-"), (Roles.Sa, 40, "SA-FF", "4-"), (Roles.Sa, 40, "SA-FF", ""), (Roles.Sa, 30, "SA-LR", "5-"),
        (Roles.Sa, 30, "SA-LR", "5-"), (Roles.Sa, 21, "SA-FF", "5-"), (Roles.Sa, 21, "SA-FF", "5-"), (Roles.Jsa, 0, "JSA-16", "6-"),
        (Roles.Jsa, 0, "JSA-16", "6-"), (Roles.Jsa, 0, "JSA-17", "6C-"), (Roles.Sa, 40, "SA-FF", "4-"), // the last one has left (inactive)
    ];

    private readonly HashSet<string> _usedNames = [];

    public async Task RunAsync(CancellationToken ct)
    {
        await PeriodsAsync(ct);
        await OrganisationAsync(ct);
        await UsersAsync(ct);
        await CalendarsAsync(ct);
        await RulesAsync(ct);
        await PayRatesAsync(ct);
        await TeamAsync(ct);
        await TradeAsync(ct);
        await RostersAsync(ct);
        await KioskAsync(ct);
        await LeaveLiabilityAsync(ct);
        await ExtrasAsync(ct);
    }

    // ── Writing ──────────────────────────────────────────────────────────────

    private async Task SaveAsync<T>(string table, IEnumerable<T> rows, CancellationToken ct) where T : class
    {
        var list = rows.ToList();
        if (list.Count == 0) return;
        db.Set<T>().AddRange(list);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        _counts[table] = _counts.GetValueOrDefault(table) + list.Count;
    }

    private static TimeOnly T(int minutes) => new(minutes / 60, minutes % 60);

    private static int Min(string hhmm) => int.Parse(hhmm[..2]) * 60 + int.Parse(hhmm[3..]);

    /// <summary>A Sydney wall-clock time on a date (AEST, as an instant).</summary>
    private static DateTimeOffset At(DateOnly date, int hour, int minute = 0) =>
        new(date.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.FromHours(10));

    private static double R2(double x) => Js.Round(x * 100) / 100;

    private static string Hex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    private IEnumerable<DateOnly> SeededDates() => Enumerable.Range(0, 42).Select(i => Previous.AddDays(i));

    // ── Periods, organisation, users ─────────────────────────────────────────

    private async Task PeriodsAsync(CancellationToken ct)
    {
        var rows = new[] { Previous, Current, Planning, Period.AddPeriods(Current, 2) }
            .Select(start => new RosterPeriod { Id = Guid.NewGuid(), StartDate = start, EndDate = Period.PeriodEndFrom(start) })
            .ToList();
        foreach (var r in rows) _periods[r.StartDate] = r.Id;
        await SaveAsync("roster_periods", rows, ct);
    }

    private async Task OrganisationAsync(CancellationToken ct)
    {
        var groups = SeedCatalogue.DirectorGroups.Select(n => new DirectorGroup { Id = Guid.NewGuid(), Name = n }).ToList();
        foreach (var g in groups) _groups[g.Name] = g.Id;
        await SaveAsync("director_groups", groups, ct);

        var regions = SeedCatalogue.Regions.Select(r => new Region { Id = Guid.NewGuid(), Name = r.Name, DirectorGroupId = _groups[r.DirectorGroup] }).ToList();
        foreach (var r in regions) _regions[r.Name] = r.Id;
        await SaveAsync("regions", regions, ct);

        var areas = SeedCatalogue.Areas.Select(a => new Area { Id = Guid.NewGuid(), Name = a.Name, RegionId = _regions[a.Region] }).ToList();
        foreach (var a in areas) _areas[a.Name] = a.Id;
        await SaveAsync("areas", areas, ct);

        foreach (var def in SeedCatalogue.Stores)
        {
            var p = Profiles[def.Code];
            var store = new Store
            {
                Id = Guid.NewGuid(), Code = def.Code, Name = def.Name, Slug = def.Slug, State = def.State,
                RegionId = _regions[def.RegionName], AreaId = _areas[def.AreaName], DsoName = def.DsoName, KioskToken = Hex(16),
            };
            _sites.Add(new SeedSite
            {
                Def = def, Store = store, Scale = p.Scale, HasSco = p.Sco,
                Open = p.Open.Split(' ').Select(Min).ToArray(), Close = p.Close.Split(' ').Select(Min).ToArray(),
            });
        }
        await SaveAsync("stores", _sites.Select(s => s.Store), ct);

        var attributes = _sites.Select(s =>
        {
            var p = Profiles[s.Def.Code];
            return new StoreAttributes
            {
                StoreId = s.Store.Id, HasSco = p.Sco, IsNewScoModel = p.NewModel, McoRegisters = p.Mco, ScoLanes = p.ScoLanes,
                ScoEfficiency = 0.45, FirstStep = 1.2, ScoShare = p.Share, OeTargetPct = 90, MaxStaffOnLeave = p.MaxLeave,
                NightFill = p.NightFill, OpeningDate = DateOnly.Parse(p.Opened, System.Globalization.CultureInfo.InvariantCulture),
            };
        });
        await SaveAsync("store_attributes", attributes, ct);

        var hours = _sites.SelectMany(s => Enumerable.Range(0, 7).Select(d => new StoreTradingHours
        {
            StoreId = s.Store.Id, DayOfWeek = d, OpenTime = T(s.Open[d]), CloseTime = T(s.Close[d]), Closed = false,
        }));
        await SaveAsync("store_trading_hours", hours, ct);

        // Christmas Day closed, Boxing Day short, in the years the seed covers; one late night at G001.
        var special = new List<SpecialTradingHours>();
        foreach (var s in _sites)
            foreach (var year in new[] { today.Year, today.Year + 1 })
            {
                special.Add(new SpecialTradingHours { StoreId = s.Store.Id, StartDate = new DateOnly(year, 12, 25), EndDate = new DateOnly(year, 12, 25), Closed = true, Note = "Christmas Day" });
                special.Add(new SpecialTradingHours { StoreId = s.Store.Id, StartDate = new DateOnly(year, 12, 26), EndDate = new DateOnly(year, 12, 26), OpenTime = new(9, 0), CloseTime = new(17, 0), Note = "Boxing Day" });
            }
        var lateNight = Planning.AddDays(3); // the planning fortnight's first Thursday
        special.Add(new SpecialTradingHours { StoreId = Site("G001").Store.Id, StartDate = lateNight, EndDate = lateNight, OpenTime = new(7, 0), CloseTime = new(22, 0), Note = "Late night trading" });
        await SaveAsync("special_trading_hours", special, ct);

        foreach (var s in _sites)
            KioskLinks.Add(new DevSeedKioskLink(s.Def.Code, $"/floor/{s.Store.KioskToken}", $"/load/{s.Store.KioskToken}"));
    }

    private SeedSite Site(string code) => _sites.Single(s => s.Def.Code == code);

    private async Task UsersAsync(CancellationToken ct)
    {
        var users = new List<AppUser>();
        foreach (var u in SeedCatalogue.Users)
        {
            var tier = Entities.Tiers.FromKey(u.TierKey);
            var row = new AppUser
            {
                Id = Guid.NewGuid(), Username = u.Username, DisplayName = u.DisplayName, Tier = tier,
                StoreId = u.StoreCode is null ? null : Site(u.StoreCode).Store.Id,
                AreaId = u.AreaName is null ? null : _areas[u.AreaName],
                RegionId = u.RegionName is null ? null : _regions[u.RegionName],
                DirectorGroupId = u.DirectorGroupName is null ? null : _groups[u.DirectorGroupName],
            };
            users.Add(row);
            _users[u.Username] = row.Id;
            var scope = u.StoreCode ?? u.AreaName ?? u.RegionName ?? u.DirectorGroupName ?? "everything";
            Logins.Add(new DevSeedLogin(u.Username, Entities.Tiers.Label(tier), scope));
        }

        // The configured first Super User (Roster:Users:FirstSuperUser), so they can sign in straight away.
        if (firstSuperUser is { IsConfigured: true } first)
        {
            var upn = first.UserPrincipalName?.Trim().ToLowerInvariant();
            var windows = first.WindowsAccount?.Trim().ToLowerInvariant();
            var username = upn ?? windows ?? first.ObjectId!.Trim().ToLowerInvariant();
            if (users.All(x => x.Username != username))
            {
                users.Add(new AppUser
                {
                    Id = Guid.NewGuid(), Username = username, Tier = Tier.SuperUser, DisplayName = first.DisplayName,
                    UserPrincipalName = upn, WindowsAccount = windows, EntraObjectId = first.ObjectId?.Trim().ToLowerInvariant(),
                });
                Logins.Add(new DevSeedLogin(username, Entities.Tiers.Label(Tier.SuperUser), "everything (Roster:Users:FirstSuperUser)"));
            }
        }
        await SaveAsync("app_users", users, ct);
    }

    // ── Calendars ────────────────────────────────────────────────────────────

    private static DateOnly NthWeekday(int year, int month, DayOfWeek day, int n)
    {
        var d = new DateOnly(year, month, 1);
        while (d.DayOfWeek != day) d = d.AddDays(1);
        return d.AddDays(7 * (n - 1));
    }

    private static DateOnly LastWeekday(int year, int month, DayOfWeek day)
    {
        var d = new DateOnly(year, month, 1).AddMonths(1).AddDays(-1);
        while (d.DayOfWeek != day) d = d.AddDays(-1);
        return d;
    }

    /// <summary>Easter Sunday (the anonymous Gregorian computus).</summary>
    private static DateOnly Easter(int y)
    {
        int a = y % 19, b = y / 100, c = y % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451, month = (h + l - 7 * m + 114) / 31, day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateOnly(y, month, day);
    }

    private static DateOnly MondayOnOrAfter(int year, int month, int day)
    {
        var d = new DateOnly(year, month, day);
        while (d.DayOfWeek != DayOfWeek.Monday) d = d.AddDays(1);
        return d;
    }

    private async Task CalendarsAsync(CancellationToken ct)
    {
        var years = new[] { today.Year - 1, today.Year, today.Year + 1 };
        var national = new List<PublicHolidayNational>();
        var state = new List<PublicHolidayState>();
        var terms = new List<SchoolTerm>();
        var exceptions = new List<SchoolTermException>();
        foreach (var y in years)
        {
            var easter = Easter(y);
            national.AddRange(new (DateOnly, string)[]
            {
                (new(y, 1, 1), "New Year's Day"), (new(y, 1, 26), "Australia Day"), (easter.AddDays(-2), "Good Friday"),
                (easter.AddDays(1), "Easter Monday"), (new(y, 4, 25), "Anzac Day"), (new(y, 12, 25), "Christmas Day"), (new(y, 12, 26), "Boxing Day"),
            }.Select(h => new PublicHolidayNational { Date = h.Item1, Name = h.Item2 }));
            state.AddRange(new (string, DateOnly, string)[]
            {
                ("NSW", NthWeekday(y, 6, DayOfWeek.Monday, 2), "King's Birthday (NSW)"),
                ("NSW", NthWeekday(y, 10, DayOfWeek.Monday, 1), "Labour Day (NSW)"),
                ("VIC", NthWeekday(y, 3, DayOfWeek.Monday, 2), "Labour Day (VIC)"),
                ("VIC", NthWeekday(y, 6, DayOfWeek.Monday, 2), "King's Birthday (VIC)"),
                ("VIC", NthWeekday(y, 11, DayOfWeek.Tuesday, 1), "Melbourne Cup (VIC)"),
                ("QLD", NthWeekday(y, 5, DayOfWeek.Monday, 1), "Labour Day (QLD)"),
                ("QLD", NthWeekday(y, 10, DayOfWeek.Monday, 1), "King's Birthday (QLD)"),
                ("WA", NthWeekday(y, 3, DayOfWeek.Monday, 1), "Labour Day (WA)"),
                ("WA", NthWeekday(y, 6, DayOfWeek.Monday, 1), "WA Day"),
                ("WA", LastWeekday(y, 9, DayOfWeek.Monday), "King's Birthday (WA)"),
                ("SA", NthWeekday(y, 3, DayOfWeek.Monday, 2), "Adelaide Cup (SA)"),
                ("SA", NthWeekday(y, 6, DayOfWeek.Monday, 2), "King's Birthday (SA)"),
                ("SA", NthWeekday(y, 10, DayOfWeek.Monday, 1), "Labour Day (SA)"),
            }.Select(h => new PublicHolidayState { State = h.Item1, Date = h.Item2, Name = h.Item3 }));
            // Four ten-week terms (scripts/seed.mjs's NSW Term 3 2026 is 13/07-25/09: the third here).
            var starts = new[] { MondayOnOrAfter(y, 1, 28), MondayOnOrAfter(y, 4, 22), MondayOnOrAfter(y, 7, 13), MondayOnOrAfter(y, 10, 12) };
            foreach (var st in AuStates.All)
            {
                for (var t = 0; t < 4; t++)
                    terms.Add(new SchoolTerm { State = st, TermLabel = $"Term {t + 1} {y}", StartDate = starts[t], EndDate = starts[t].AddDays(74) });
                exceptions.Add(new SchoolTermException { State = st, Date = starts[3], Note = "Staff development day" });
            }
        }
        await SaveAsync("public_holidays_national", national, ct);
        await SaveAsync("public_holidays_state", state, ct);
        await SaveAsync("school_terms", terms, ct);
        await SaveAsync("school_term_exceptions", exceptions, ct);
        // A local show day inside the planning fortnight (G340), so a public holiday shows up in a plan.
        await SaveAsync("public_holidays_local", [new PublicHolidayLocal { StoreId = Site("G340").Store.Id, Date = Planning.AddDays(8), Name = "Harbour Point Show Day" }], ct);
    }

    // ── Rules defaults and pay ───────────────────────────────────────────────

    private async Task RulesAsync(CancellationToken ct)
    {
        // break_rules: the award values (g999.fixture.ts), with 0012's period overtime (76h for
        // SA, DSM, RSM) and double time after a break under 10h (JSA, SA, DSM, RSM).
        static BreakRule Rule(string role, double max, double gap, string type, double weekly, double? ot15) => new()
        {
            Role = role, MinShiftHours = 3, MaxShiftHours = max, GapHours = gap, BreakType = type, WeeklyCap = weekly, ConsecDayCap = 6,
            Ot15AfterHours = ot15, Ot19AfterHours = null,
            OtPeriodAfterHours = role is Roles.Sa or Roles.Dsm or Roles.Rsm ? 76 : null,
            DoubleTimeUnderGapHours = role is Roles.Jsa or Roles.Sa or Roles.Dsm or Roles.Rsm ? 10 : null,
        };
        await SaveAsync("break_rules", new[]
        {
            Rule(Roles.Sm, 12, 0, BreakTypes.Flat30Unpaid, 45, null), Rule(Roles.Asm, 12, 0, BreakTypes.Flat30Unpaid, 45, null),
            Rule(Roles.Dsm, 9, 11, BreakTypes.Flat30Unpaid, 38, null), Rule(Roles.Rsm, 9, 11, BreakTypes.Tiered, 38, 9.5),
            Rule(Roles.Sa, 9, 11, BreakTypes.Tiered, 38, 9.5), Rule(Roles.Jsa, 6, 11, BreakTypes.Tiered, 15, 9.5),
        }, ct);

        // labour_group_rules: the windows g999.fortnight.fixture.ts carries, every weekday.
        (string Group, int Start, int End, double? Hours)[] windows =
        [
            ("admin", 420, 1020, null), ("load_running", 360, 600, null), ("reset", 600, 1200, null), ("specials", 1020, 1260, null),
            ("specials", 1080, 1260, null), ("cleaning", 1080, 1260, null), ("cleaning", 540, 600, 1),
        ];
        await SaveAsync("labour_group_rules", Enumerable.Range(0, 7).SelectMany(d => windows.Select(w => new LabourGroupRule
        {
            LabourGroup = w.Group, DayOfWeek = d, WindowStart = T(w.Start), WindowEnd = T(w.End), WindowHours = w.Hours,
        })), ct);

        // labour_group_priority: the engine's TASK_PRIORITY order.
        string[] priority = ["checkout", "load_running", "reset", "admin", "specials", "cleaning"];
        await SaveAsync("labour_group_priority", priority.Select((g, i) => new LabourGroupPriority { LabourGroup = g, Priority = i + 1 }), ct);

        await SaveAsync("pay_settings", [new PaySettings()], ct);
        (string State, double Rate)[] tax = [("NSW", 0.0545), ("VIC", 0.0485), ("QLD", 0.0475), ("SA", 0.0495), ("WA", 0.055), ("TAS", 0.061), ("NT", 0.055), ("ACT", 0.0685)];
        await SaveAsync("payroll_tax_rates", tax.Select(t => new PayrollTaxRate { State = t.State, Rate = t.Rate }), ct);
    }

    /// <summary>Invented hourly rates for every position with a rate: 24 hours x 4 day types.</summary>
    private async Task PayRatesAsync(CancellationToken ct)
    {
        var positions = await db.Positions.AsNoTracking().Where(p => p.HasRate).ToListAsync(ct);
        var rows = new List<PayRate>();
        foreach (var p in positions)
        {
            var bandStep = p.YearBand switch { 2 => 1.5, 3 => 3.0, 4 => 4.5, 7 => 7.0, _ => 0.0 };
            var (baseRate, salaried) = p.BaseRole switch
            {
                Roles.Sm => (40.0 + bandStep + ((p.WeeklyHours ?? 45) - 45) * 0.1, true),
                Roles.Asm => (34.0 + bandStep, true),
                Roles.Dsm => (31.2, false),
                Roles.Jsa => (p.Code.EndsWith("16", StringComparison.Ordinal) ? 14.5 : 16.9, false),
                _ => (p.Code == "SA-LR" ? 26.1 : 27.4, false),
            };
            foreach (var dayType in PayDayTypes.All)
                for (var hour = 0; hour < 24; hour++)
                {
                    // Salaried managers are flat; everyone else has weekend, holiday and
                    // early/evening loadings.
                    var factor = salaried ? 1.0 : dayType switch
                    {
                        PayDayTypes.Saturday => 1.25,
                        PayDayTypes.Sunday => 1.5,
                        PayDayTypes.PublicHoliday => 2.25,
                        _ => hour < 7 ? 1.15 : hour >= 18 ? 1.25 : 1.0,
                    };
                    rows.Add(new PayRate { DayType = dayType, Hour = hour, Role = p.Code, Rate = R2(baseRate * factor) });
                }
        }
        await SaveAsync("pay_rates", rows, ct);
    }

    // ── Teams ────────────────────────────────────────────────────────────────

    private string NewName(string prefix)
    {
        while (true)
        {
            var first = _rnd.Pick(FirstNames);
            var last = _rnd.Pick(Surnames);
            if (!_usedNames.Add($"{first} {last}")) continue;
            return prefix.Length == 0 ? $"{first} {last}" : $"{prefix}{last}, {first}";
        }
    }

    private async Task TeamAsync(CancellationToken ct)
    {
        var employees = new List<Employee>();
        foreach (var s in _sites)
        {
            for (var i = 0; i < TeamShape.Length; i++)
            {
                var (role, contract, position, prefix) = TeamShape[i];
                int? band = role == Roles.Sm ? _rnd.Pick(new[] { 1, 2, 3, 4, 7 }) : role == Roles.Asm ? _rnd.Pick(new[] { 1, 2 }) : (int?)null;
                var code = position ?? (role == Roles.Sm ? $"SM-45-{band}" : $"ASM-40-{band}");
                var e = new Employee
                {
                    Id = Guid.NewGuid(), StoreId = s.Store.Id, Name = NewName(prefix), Role = role, ContractHours = contract,
                    SmYearBand = band, PositionCode = code, Active = i != TeamShape.Length - 1,
                    UkgId = $"9{s.Number:000}{i + 1:00}",
                    // Two recent starters (probation: slower pallet minutes on the Load Planner).
                    StartDate = i == 13 ? today.AddDays(-20) : i == 16 ? today.AddDays(-75) : null,
                    Notes = i == 8 ? "Limited roster: school hours on weekdays." : null,
                };
                employees.Add(e);
                s.Team.Add(e);
            }
        }
        await SaveAsync("employees", employees, ct);

        var availability = new List<EmployeeAvailability>();
        var restricted = new List<EmployeeRestrictedTask>();
        var leave = new List<EmployeeLeave>();
        var requests = new List<DayOffRequest>();
        var markers = new List<WeekendMarker>();
        var fixedRosters = new List<FixedRoster>();
        var cycles = new List<FixedRosterCycle>();
        foreach (var s in _sites)
        {
            var t = s.Team;
            void Avail(Employee e, int dow, bool available, int? from = null, int? to = null)
            {
                availability.Add(new EmployeeAvailability { EmployeeId = e.Id, DayOfWeek = dow, Available = available, FromTime = from is null ? null : T(from.Value), ToTime = to is null ? null : T(to.Value) });
                s.Availability[(e.Id, dow)] = (available, from, to);
            }
            // Juniors after school on weekdays; the limited-roster assistant in school hours, no weekends.
            foreach (var j in t.Where(e => e.Role == Roles.Jsa))
                for (var d = 0; d < 5; d++) Avail(j, d, true, 15 * 60 + 30);
            for (var d = 0; d < 7; d++) Avail(t[8], d, d < 5, d < 5 ? 9 * 60 : null, d < 5 ? 15 * 60 : null);
            Avail(t[12], 6, false); // no Sundays
            Avail(t[3], 1, false);  // a DSM studying on Tuesdays

            restricted.Add(new EmployeeRestrictedTask { EmployeeId = t[8].Id, LabourGroup = "load_running" });

            void Away(Employee e, DateOnly from, DateOnly to, string type, double? dayHours = null)
            {
                leave.Add(new EmployeeLeave { EmployeeId = e.Id, StartDate = from, EndDate = to, LeaveType = type, DayHours = dayHours, CreatedAt = At(today.AddDays(-30), 9) });
                for (var d = from; d <= to; d = d.AddDays(1)) s.Away.Add((e.Id, d));
            }
            Away(t[6], Planning, Planning.AddDays(4), LeaveTypes.Annual);                 // a week of annual leave
            Away(t[9], Previous.AddDays(2), Previous.AddDays(3), LeaveTypes.Sick);          // two sick days last fortnight
            Away(t[4], Planning.AddDays(9), Planning.AddDays(11), LeaveTypes.LongService);
            Away(t[13], today.AddDays(-1), today.AddDays(-1), LeaveTypes.Carers, 4);

            // Day-off requests in the planning fortnight: two waiting, one granted, one declined.
            requests.Add(new DayOffRequest { EmployeeId = t[7].Id, Date = Planning.AddDays(5), Status = DayOffStatuses.Pending, Notes = "Family wedding", SubmittedAt = At(today.AddDays(-2), 14) });
            requests.Add(new DayOffRequest { EmployeeId = t[4].Id, Date = Planning.AddDays(4), Status = DayOffStatuses.Pending, Notes = "Medical appointment", SubmittedAt = At(today.AddDays(-1), 10) });
            requests.Add(new DayOffRequest { EmployeeId = t[10].Id, Date = Planning.AddDays(2), Status = DayOffStatuses.Granted, Notes = "Moving house", SubmittedAt = At(today.AddDays(-6), 11) });
            requests.Add(new DayOffRequest { EmployeeId = t[15].Id, Date = Planning.AddDays(13), Status = DayOffStatuses.Declined, Notes = "Concert", SubmittedAt = At(today.AddDays(-4), 16) });
            s.Away.Add((t[10].Id, Planning.AddDays(2)));

            // Weekend-off markers (keyed on the Saturday): managers alternate.
            foreach (var (e, week) in new[] { (t[0], 0), (t[1], 1), (t[2], 0), (t[3], 1), (t[4], 1), (t[5], 0), (t[9], 1) })
            {
                var sat = Planning.AddDays(5 + 7 * week);
                markers.Add(new WeekendMarker { EmployeeId = e.Id, WeekendStart = sat, SatOff = true, SunOff = true });
                s.Away.Add((e.Id, sat));
                s.Away.Add((e.Id, sat.AddDays(1)));
            }

            // Fixed rosters: an assistant on Monday and Tuesday opens and a Thursday close every
            // week, and a DSM on a two-week cycle (a Wednesday close, then a Saturday open).
            void Fix(Employee e, int cycleWeeks, params (int Week, int Dow, int Start, int End)[] days)
            {
                foreach (var (week, dow, start, end) in days)
                {
                    fixedRosters.Add(new FixedRoster { EmployeeId = e.Id, DayOfWeek = dow, StartTime = T(start), EndTime = T(end), WeekIndex = week });
                    s.Fixed[(e.Id, dow, week)] = (start, end);
                }
                cycles.Add(new FixedRosterCycle { EmployeeId = e.Id, CycleWeeks = cycleWeeks, StartDate = Previous });
                s.CycleWeeks[e.Id] = cycleWeeks;
            }
            var open = s.Open[0] - 60;
            Fix(t[5], 1, (0, 0, open, open + 9 * 60), (0, 1, open, open + 9 * 60), (0, 3, s.Close[3] + 30 - 9 * 60, s.Close[3] + 30));
            Fix(t[2], 2, (0, 2, s.Close[2] + 30 - 8 * 60, s.Close[2] + 30), (1, 5, s.Open[5] - 60, s.Open[5] - 60 + 8 * 60));
        }
        await SaveAsync("employee_availability", availability, ct);
        await SaveAsync("employee_restricted_tasks", restricted, ct);
        await SaveAsync("employee_leave", leave, ct);
        await SaveAsync("day_off_requests", requests, ct);
        await SaveAsync("weekend_markers", markers, ct);
        await SaveAsync("fixed_rosters", fixedRosters, ct);
        await SaveAsync("fixed_roster_cycles", cycles, ct);
    }

    // ── Demand and trade ─────────────────────────────────────────────────────

    // Benchmark hours at 100% by weekday (Monday first): invented, about a mid-sized store.
    private static readonly double[] LrBase = [12.5, 12.0, 13.0, 13.5, 14.5, 14.5, 12.0];
    private static readonly double[] CleaningBase = [4.0, 2.7, 2.2, 3.3, 2.2, 2.2, 0.5];
    private static readonly double[] SpecialsBase = [1.5, 8.0, 1.6, 1.6, 6.5, 1.7, 1.5];
    private static readonly double[] ResetBase = [6.8, 6.9, 7.4, 7.9, 8.6, 9.8, 8.6];
    private static readonly double[] AdminBase = [9.3, 7.0, 5.5, 5.0, 5.0, 4.8, 4.8];
    private static readonly double[] CheckoutBase = [12.0, 11.5, 11.5, 12.0, 13.0, 14.0, 12.0];
    private static readonly double[] SalesBase = [42000, 40000, 41000, 45000, 52000, 58000, 47000];

    private readonly Dictionary<(Guid, DateOnly), BenchmarkDay> _bench = [];

    private async Task TradeAsync(CancellationToken ct)
    {
        var batchRows = new List<UploadBatch>();
        UploadBatch Batch(string key, string type, string file, DateOnly day, DateOnly? period, int rows)
        {
            var b = new UploadBatch
            {
                Id = Guid.NewGuid(), Type = type, Filename = file, UploadedBy = _users["superuser"], UploadedAt = At(day, 9, 30),
                RosterPeriodId = period is { } p && _periods.TryGetValue(p, out var id) ? id : null,
                TotalRows = rows, AcceptedRows = rows, RejectedRows = 0,
            };
            batchRows.Add(b);
            _batches[key] = b.Id;
            return b;
        }
        foreach (var start in new[] { Previous, Current, Planning })
            Batch($"benchmark:{start.ToIso()}", UploadTypes.Benchmark, $"Benchmark_{start:yyyyMMdd}.csv", start.AddDays(-3), start, 14 * _sites.Count);
        Batch("actuals", UploadTypes.Actuals, $"Actuals_{today.AddDays(-1):yyyyMMdd}.csv", today, null, 0);
        Batch("pos", UploadTypes.PosQuarterHour, $"POS_QuarterHour_{today.AddDays(-1):yyyyMMdd}.csv", today, null, 0);
        Batch("contacts", UploadTypes.Contacts, "UKG_Contacts.csv", today.AddDays(-10), null, _sites.Sum(s => s.Team.Count));
        await SaveAsync("upload_batches", batchRows, ct);

        var bench = new List<BenchmarkDay>();
        foreach (var s in _sites)
            foreach (var date in SeededDates())
            {
                var d = Period.DowMon0(date);
                double N(double x) => R2(x * s.Scale * _rnd.Between(0.92, 1.08));
                var sales = Js.Round(SalesBase[d] * s.Scale * _rnd.Between(0.94, 1.06));
                var b = new BenchmarkDay
                {
                    StoreId = s.Store.Id, Date = date, IsSco = s.HasSco, SalesForecast = sales, Items = Js.Round(sales / 13.5),
                    CustomerCount = Js.Round(sales / 27), LrHours100 = N(LrBase[d]), CleaningHours100 = N(CleaningBase[d]),
                    SpecialsHours100 = N(SpecialsBase[d]), ResetHours100 = N(ResetBase[d]), AdminHours100 = N(AdminBase[d]),
                    CheckoutHours100 = N(CheckoutBase[d] * (s.HasSco ? 1 : 1.7)),
                    EmployeeCount = s.Team.Count(e => e.Active),
                    RosterPeriodId = _periods[Period.PeriodStartFor(date)],
                    BatchId = _batches[$"benchmark:{Period.PeriodStartFor(date).ToIso()}"],
                };
                bench.Add(b);
                _bench[(s.Store.Id, date)] = b;
            }
        await SaveAsync("benchmark_days", bench, ct);

        // What was actually traded and worked, up to yesterday.
        var actuals = new List<ActualsDay>();
        foreach (var s in _sites)
            foreach (var date in SeededDates().Where(d => d < today))
            {
                var b = _bench[(s.Store.Id, date)];
                double A(double x) => R2(x * _rnd.Between(0.9, 1.15));
                var a = new ActualsDay
                {
                    StoreId = s.Store.Id, Date = date, ActualSales = Js.Round(b.SalesForecast * _rnd.Between(0.9, 1.12)),
                    LrHours = A(b.LrHours100), CleaningHours = A(b.CleaningHours100), SpecialsHours = A(b.SpecialsHours100),
                    ResetHours = A(b.ResetHours100), AdminHours = A(b.AdminHours100), CheckoutHours = A(b.CheckoutHours100),
                    BatchId = _batches["actuals"],
                };
                a.ActualItems = Js.Round(a.ActualSales / 13.5);
                a.ActualCustomers = Js.Round(a.ActualSales / 27);
                a.ActualHours100 = R2(a.LrHours!.Value + a.CleaningHours!.Value + a.SpecialsHours!.Value + a.ResetHours!.Value + a.AdminHours!.Value + a.CheckoutHours!.Value);
                a.UkgActualHours = R2(a.ActualHours100 * _rnd.Between(1.03, 1.08));
                actuals.Add(a);
            }
        await SaveAsync("actuals_days", actuals, ct);

        // The trade curve: measured POS statistics for G001 and G102 (the four weeks to
        // yesterday), the legacy weekly allocation for the others.
        var pos = new List<PosIntervalStat>();
        foreach (var s in _sites.Where(x => x.Def.Code is "G001" or "G102"))
            for (var back = 28; back >= 1; back--)
            {
                var date = today.AddDays(-back);
                var d = Period.DowMon0(date);
                var curve = Curve(s.Open[d], s.Close[d]);
                var customers = SalesBase[d] * s.Scale / 27;
                for (var i = 0; i < curve.Count; i++)
                {
                    var c = customers * curve[i] * _rnd.Between(0.85, 1.15);
                    var share = s.HasSco ? 0.55 : 0;
                    pos.Add(new PosIntervalStat
                    {
                        StoreId = s.Store.Id, Date = date, BlockTime = T(s.Open[d] + 15 * i),
                        McoCustomers = Js.Round(c * (1 - share)), ScoCustomers = Js.Round(c * share),
                        McoItems = Js.Round(c * (1 - share) * 16), ScoItems = Js.Round(c * share * 7),
                        BatchId = _batches["pos"],
                    });
                }
            }
        await SaveAsync("pos_interval_stats", pos, ct);
        var allocation = new List<QuarterHourAllocation>();
        foreach (var s in _sites.Where(x => x.Def.Code is not ("G001" or "G102")))
            for (var d = 0; d < 7; d++)
            {
                var curve = Curve(s.Open[d], s.Close[d]);
                for (var i = 0; i < curve.Count; i++)
                    allocation.Add(new QuarterHourAllocation { StoreId = s.Store.Id, DayOfWeek = d, BlockTime = T(s.Open[d] + 15 * i), Allocation = Js.Round(curve[i] * 1e6) / 1e6 });
            }
        await SaveAsync("quarter_hour_allocation", allocation, ct);

        // A couple of Sales-page adjustments in the planning fortnight.
        var overrides = new List<SalesOverride>
        {
            new() { StoreId = Site("G001").Store.Id, Date = Planning.AddDays(3), Method = "pct", Pct = 110, Reason = "Local festival", Notes = "Street festival on the main road." },
            new() { StoreId = Site("G102").Store.Id, Date = Planning.AddDays(5), Method = "value", Value = Js.Round(_bench[(Site("G102").Store.Id, Planning.AddDays(5))].SalesForecast * 1.2), Reason = "School holidays" },
        };
        await SaveAsync("sales_overrides", overrides, ct);

        // Non-op bookings, by name from the catalogue.
        var subs = await db.NonOpSubcategories.AsNoTracking().ToDictionaryAsync(x => x.Name, x => x.Id, ct);
        var nonOp = new List<NonOpEntry>();
        void Book(SeedSite s, DateOnly date, string item, Employee? who, int minutes, string? description = null)
        {
            if (subs.TryGetValue(item, out var id))
                nonOp.Add(new NonOpEntry { StoreId = s.Store.Id, Date = date, SubcategoryId = id, EmployeeId = who?.Id, Minutes = minutes, Description = description });
        }
        var g001 = Site("G001");
        Book(g001, Planning.AddDays(1), "Store Manager Meetings", g001.Team[0], 120);
        Book(g001, Planning.AddDays(2), "JSA Pathway (Day 1)", g001.Team[17], 330, "New junior's first pathway day");
        Book(g001, Planning.AddDays(3), "Fire Drills (per person)", null, 15);
        Book(g001, Planning.AddDays(8), "Career Conversations (Leader)", g001.Team[1], 30);
        var g102 = Site("G102");
        Book(g102, Current.AddDays(2), "Store Manager Meetings", g102.Team[0], 120);
        Book(g102, Current.AddDays(9), "Quarterly H&S Audits and Regional Safety Day", null, 120);
        await SaveAsync("non_op_entries", nonOp, ct);
    }

    /// <summary>A day's trade share per 15-minute block from open to close: a morning and an afternoon peak, summing to 1.</summary>
    private static List<double> Curve(int open, int close)
    {
        var n = Math.Max(1, (close - open) / 15);
        var raw = new List<double>(n);
        for (var i = 0; i < n; i++)
        {
            var t = open + 15 * i + 7.5;
            var v = 0.25 + Math.Exp(-Math.Pow((t - 11.5 * 60) / 110, 2)) + 0.9 * Math.Exp(-Math.Pow((t - 17 * 60) / 120, 2));
            raw.Add(v);
        }
        var sum = raw.Sum();
        return raw.Select(v => v / sum).ToList();
    }

    // ── Plans and rosters ────────────────────────────────────────────────────

    private sealed record SeedShift(Employee Employee, int Start, int End, string Kind);

    private async Task RostersAsync(CancellationToken ct)
    {
        var dayPlans = new List<DayPlan>();
        var shifts = new List<GeneratedShift>();
        var planned = new List<PlannedBlock>();
        var audit = new List<RosterAudit>();
        foreach (var s in _sites.Where(x => RosteredStores.Contains(x.Def.Code)))
        {
            var dates = new List<DateOnly>();
            dates.AddRange(Period.PeriodDatesFrom(Previous));
            dates.AddRange(s.Def.Code == "G215" ? Period.PeriodDatesFrom(Current).Take(11) : Period.PeriodDatesFrom(Current));
            if (s.Def.Code == "G215") dates.AddRange(Period.PeriodDatesFrom(Planning).Take(7)); // approved early: week 1 only
            foreach (var date in dates)
            {
                var approvedAt = At(Period.PeriodStartFor(date).AddDays(-4), 16);
                dayPlans.Add(new DayPlan
                {
                    StoreId = s.Store.Id, Date = date, PlanStatus = DayStatuses.Approved, RosterStatus = DayStatuses.Approved,
                    PlanGeneratedAt = approvedAt.AddHours(-3), RosterGeneratedAt = approvedAt,
                });
                var day = BuildDay(s, date);
                var tiles = new Dictionary<(string, int), int>();
                foreach (var sh in day)
                {
                    var breakMinutes = BreakMinutes(sh.Employee.Role, sh.End - sh.Start);
                    var row = new GeneratedShift
                    {
                        Id = Guid.NewGuid(), StoreId = s.Store.Id, Date = date, EmployeeId = sh.Employee.Id, EmployeeName = sh.Employee.Name,
                        Role = sh.Employee.Role, StartTime = T(sh.Start), EndTime = T(sh.End), BreakMinutes = breakMinutes,
                        CreatedAt = approvedAt,
                    };
                    var mealFrom = breakMinutes >= 30 ? sh.Start + ((sh.End - sh.Start) / 2 / 15) * 15 : -1;
                    for (var t = sh.Start; t < sh.End; t += 15)
                    {
                        if (t >= mealFrom && t < mealFrom + 30) continue; // the meal break
                        var group = GroupAt(s, date, sh, t);
                        row.Blocks.Add(new ShiftTaskBlock { BlockTime = T(t), LabourGroup = group });
                        tiles[(group, t)] = tiles.GetValueOrDefault((group, t)) + 1;
                    }
                    shifts.Add(row);
                }
                if (Period.PeriodStartFor(date) == Current)
                    foreach (var ((group, t), count) in tiles)
                        planned.Add(new PlannedBlock { StoreId = s.Store.Id, Date = date, LabourGroup = group, BlockTime = T(t), Headcount = count });
            }
            foreach (var start in dates.Select(Period.PeriodStartFor).Distinct())
            {
                var approved = dates.Where(d => Period.PeriodStartFor(d) == start).Select(d => d.ToIso()).ToList();
                audit.Add(new RosterAudit
                {
                    StoreId = s.Store.Id, UserId = s.Def.Code == "G001" ? _users["store1"] : _users["superuser"], Action = "approve_roster",
                    Detail = JsonSerializer.Serialize(new { dates = approved }), CreatedAt = At(start.AddDays(-4), 16),
                });
            }
        }
        await SaveAsync("day_plans", dayPlans, ct);
        foreach (var chunk in shifts.Chunk(400)) await SaveAsync("generated_shifts", chunk, ct);
        _counts["shift_task_blocks"] = shifts.Sum(x => x.Blocks.Count);
        foreach (var chunk in planned.Chunk(4000)) await SaveAsync("planned_blocks", chunk, ct);
        await SaveAsync("roster_audit", audit, ct);

        // Fortnight reviews: one approved, one waiting for the EMSO, one returned.
        var reviews = new List<RosterReview>
        {
            new() { StoreId = Site("G001").Store.Id, PeriodStart = Previous, Status = ReviewStatuses.Approved, SubmittedAt = At(Previous.AddDays(-5), 15), SubmittedBy = _users["store1"], DecidedAt = At(Previous.AddDays(-4), 10), DecidedBy = _users["emso1"] },
            new() { StoreId = Site("G102").Store.Id, PeriodStart = Current, Status = ReviewStatuses.Submitted, SubmittedAt = At(Current.AddDays(-5), 17), SubmittedBy = _users["superuser"] },
            new() { StoreId = Site("G215").Store.Id, PeriodStart = Current, Status = ReviewStatuses.Returned, SubmittedAt = At(Current.AddDays(-6), 12), SubmittedBy = _users["store2"], DecidedAt = At(Current.AddDays(-5), 9), DecidedBy = _users["emso2"], ReturnMessage = "Please add evening cover on the second Thursday." },
        };
        await SaveAsync("roster_reviews", reviews, ct);
        await SaveAsync("roster_review_days", [new RosterReviewDay { StoreId = Site("G215").Store.Id, PeriodStart = Current, Date = Current.AddDays(10), Verdict = ReviewVerdicts.Query, Query = "Only one adult on after 6 pm.", DecidedBy = _users["emso2"], DecidedAt = At(Current.AddDays(-5), 9) }], ct);
        RosterReviewEvent Event(string code, DateOnly period, string user, string tier, string kind, string body, DateTimeOffset at) =>
            new() { StoreId = Site(code).Store.Id, PeriodStart = period, ActorId = _users[user], ActorName = SeedCatalogue.Users.Single(u => u.Username == user).DisplayName, ActorTier = tier, Kind = kind, Body = body, CreatedAt = at };
        await SaveAsync("roster_review_events", new[]
        {
            Event("G001", Previous, "store1", "store", "submitted", "Submitted for review.", At(Previous.AddDays(-5), 15)),
            Event("G001", Previous, "emso1", "emso", "approved", "Approved.", At(Previous.AddDays(-4), 10)),
            Event("G102", Current, "superuser", "super_user", "submitted", "Submitted for review.", At(Current.AddDays(-5), 17)),
            Event("G215", Current, "store2", "store", "submitted", "Submitted for review.", At(Current.AddDays(-6), 12)),
            Event("G215", Current, "emso2", "emso", "returned", "Please add evening cover on the second Thursday.", At(Current.AddDays(-5), 9)),
        }, ct);
    }

    /// <summary>A day's invented shifts: two managers, assistants across the day, juniors after school or at the weekend.</summary>
    private List<SeedShift> BuildDay(SeedSite s, DateOnly date)
    {
        var d = Period.DowMon0(date);
        int staffStart = s.Open[d] - 60, staffEnd = s.Close[d] + 30;
        var dayIndex = date.DayNumber;
        bool Free(Employee e) => e.Active && !s.Away.Contains((e.Id, date))
            && (!s.Availability.TryGetValue((e.Id, d), out var a) || a.Available);
        var shifts = new List<SeedShift>();
        var used = new HashSet<Guid>();
        void Take(IEnumerable<Employee> pool, int start, int end, string kind)
        {
            var list = pool.Where(e => Free(e) && !used.Contains(e.Id)).ToList();
            if (list.Count == 0) return;
            var e = list[(int)(dayIndex % list.Count)];
            if (s.Availability.TryGetValue((e.Id, d), out var a))
            {
                if (a.From is { } from) start = Math.Max(start, from);
                if (a.To is { } to) end = Math.Min(end, to);
            }
            if (end - start < 180) return;
            used.Add(e.Id);
            shifts.Add(new SeedShift(e, start, end, kind));
        }

        // Fixed rosters first, as the engine honours them.
        foreach (var ((id, dow, week), (start, end)) in s.Fixed)
        {
            var e = s.Team.Single(x => x.Id == id);
            var cycleWeek = ((date.DayNumber - Previous.DayNumber) / 7 % s.CycleWeeks[id] + s.CycleWeeks[id]) % s.CycleWeeks[id];
            if (dow != d || week != cycleWeek || !Free(e) || used.Contains(id)) continue;
            used.Add(id);
            shifts.Add(new SeedShift(e, start, end, Roles.Managers.Contains(e.Role) ? "manager" : start < s.Open[d] ? "open" : "close"));
        }
        var managers = s.Team.Where(e => Roles.Managers.Contains(e.Role)).ToList();
        var assistants = s.Team.Where(e => e.Role == Roles.Sa).ToList();
        var juniors = s.Team.Where(e => e.Role == Roles.Jsa).ToList();
        Take(managers, staffStart, Math.Min(staffEnd, staffStart + 9 * 60), "manager");
        Take(managers, Math.Max(staffStart, staffEnd - 9 * 60), staffEnd, "manager");
        Take(assistants, staffStart, staffStart + 8 * 60, "open");
        Take(assistants, staffStart, staffStart + 6 * 60, "open");
        Take(assistants, 10 * 60, 16 * 60, "mid");
        if (d >= 3) Take(assistants, 11 * 60, 19 * 60, "mid");
        Take(assistants, staffEnd - 7 * 60, staffEnd, "close");
        Take(assistants, staffEnd - 5 * 60, staffEnd, "close");
        if (d < 5) Take(juniors, 16 * 60, staffEnd, "junior");
        else { Take(juniors, 10 * 60, 16 * 60, "junior"); Take(juniors, 14 * 60, staffEnd, "junior"); }
        return shifts.OrderBy(x => x.Start).ThenBy(x => x.Employee.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Time off the floor: 30 minutes for managers past 5 hours; tea and meal breaks for the rest.</summary>
    private static int BreakMinutes(string role, int minutes) =>
        role is Roles.Sm or Roles.Asm or Roles.Dsm
            ? (minutes > 300 ? 30 : 0)
            : minutes >= 456 ? 50 : minutes >= 300 ? 40 : minutes >= 240 ? 10 : 0;

    /// <summary>The labour group a 15-minute tile is painted with.</summary>
    private static string GroupAt(SeedSite s, DateOnly date, SeedShift sh, int t)
    {
        var d = Period.DowMon0(date);
        int open = s.Open[d], close = s.Close[d];
        if (t >= close) return "cleaning";
        if (sh.Kind == "manager") return (t - sh.Start) % 120 < 30 ? "admin" : t < open ? "load_running" : "checkout";
        if (t < open) return sh.Employee.Role == Roles.Jsa ? "cleaning" : "load_running";
        if (sh.Employee.Role == Roles.Jsa) return "checkout";
        return sh.Kind switch
        {
            "open" => t < open + 120 ? "load_running" : (t / 60) % 2 == 0 ? "reset" : "checkout",
            "mid" => (d is 1 or 4) && t >= 13 * 60 && t < 15 * 60 ? "specials" : t < 13 * 60 ? "reset" : "checkout",
            _ => t >= close - 60 ? "specials" : "checkout",
        };
    }

    // ── Kiosk pages ──────────────────────────────────────────────────────────

    private async Task KioskAsync(CancellationToken ct)
    {
        var types = await db.LoadTypes.AsNoTracking().OrderBy(x => x.SortOrder).ToListAsync(ct);
        var loads = new List<DailyLoad>();
        foreach (var s in _sites.Where(x => x.Def.Code is "G001" or "G102"))
            for (var back = 56; back >= 0; back--)
            {
                var date = today.AddDays(-back);
                foreach (var lt in types)
                {
                    var qty = lt.Name switch
                    {
                        "Ambient" => _rnd.Int(4, 12), "Produce" => _rnd.Int(3, 7), "Bread" => _rnd.Int(1, 3),
                        "Short Life" => _rnd.Int(1, 4), "Freezer" => Period.DowMon0(date) is 0 or 3 ? _rnd.Int(2, 5) : 0,
                        "Long Life" => _rnd.Int(0, 2), "Freeze to Thaw" => _rnd.Chance(0.5) ? 1 : 0, _ => _rnd.Chance(0.3) ? _rnd.Int(1, 3) : 0,
                    };
                    if (qty > 0) loads.Add(new DailyLoad { StoreId = s.Store.Id, Date = date, LoadTypeId = lt.Id, Qty = qty, UpdatedAt = At(date, 5, 45) });
                }
            }
        await SaveAsync("daily_loads", loads, ct);

        // Today's load crew at G001: whoever starts before the doors open.
        var g001 = Site("G001");
        var crew = BuildDay(g001, today).Where(x => x.Start < g001.Open[Period.DowMon0(today)] && x.Employee.Role != Roles.Jsa).ToList();
        string[] categories = ["Ambient", "Produce", "Freezer", "Bread"];
        await SaveAsync("load_assignments", crew.Select((x, i) => new LoadAssignment
        {
            StoreId = g001.Store.Id, Date = today, EmployeeId = x.Employee.Id, EmployeeName = x.Employee.Name,
            LoadType = categories[i % categories.Length], Manual = i == 0,
        }), ct);

        var notes = new List<StoreWalkNote>();
        foreach (var s in _sites.Where(x => x.Def.Code is "G001" or "G102"))
        {
            notes.Add(new StoreWalkNote { StoreId = s.Store.Id, Note = "Pallet jack 2 needs a new wheel", AssignedEmployeeId = s.Team[5].Id, AssignedName = s.Team[5].Name, RaisedDate = today.AddDays(-3), CreatedAt = At(today.AddDays(-3), 8) });
            notes.Add(new StoreWalkNote { StoreId = s.Store.Id, Note = "Re-face the snack aisle before the afternoon peak", RaisedDate = today, CreatedAt = At(today, 7, 30) });
            notes.Add(new StoreWalkNote { StoreId = s.Store.Id, Note = "Wipe down the freezer door seals", AssignedEmployeeId = s.Team[11].Id, AssignedName = s.Team[11].Name, RaisedDate = today.AddDays(-7), CreatedAt = At(today.AddDays(-7), 9), CompletedAt = At(today.AddDays(-1), 15) });
        }
        await SaveAsync("store_walk_notes", notes, ct);
        await SaveAsync("admin_task_checks", new[] { "STM Date Check", "Fresh Ordering" }.Select((n, i) => new AdminTaskCheck
        {
            StoreId = g001.Store.Id, Date = today, TaskName = n, CheckedAt = At(today, 8 + i),
        }), ct);
    }

    // ── Leave liability (the UKG exports) ────────────────────────────────────

    private async Task LeaveLiabilityAsync(CancellationToken ct)
    {
        var bands = await db.LeaveAccrualBands.AsNoTracking().ToListAsync(ct);
        var ends = new[] { 3, 2, 1, 0 }.Select(k => Previous.AddDays(-1 - 14 * k)).ToList(); // the last four pay-period ends
        var batches = ends.Select(end => new UploadBatch
        {
            Id = Guid.NewGuid(), Type = UploadTypes.LeaveLiability, Filename = $"Leave_Liability_{end:yyyy-MM-dd}.csv",
            UploadedBy = _users["superuser"], UploadedAt = At(end.AddDays(2), 11), TotalRows = 0,
        }).ToList();
        var requestBatch = new UploadBatch { Id = Guid.NewGuid(), Type = UploadTypes.LeaveRequests, Filename = "Leave_Requests.csv", UploadedBy = _users["superuser"], UploadedAt = At(today.AddDays(-3), 11) };
        var snapshots = new List<LeaveLiabilitySnapshot>();
        var requests = new List<LeaveRequestUkg>();
        foreach (var s in _sites)
        {
            var people = s.Team.Where(e => e.Active).ToList();
            var balance = people.ToDictionary(e => e.Id, e => (Annual: R2(_rnd.Between(20, 160)), Sick: R2(_rnd.Between(0, 90))));
            for (var k = 0; k < ends.Count; k++)
                foreach (var e in people)
                {
                    var roleClass = e.Role switch { Roles.Sm => "SM", Roles.Asm => "ASM", Roles.Dsm => "DSM", Roles.Jsa => "CASUAL", _ => "SA" };
                    var band = bands.FirstOrDefault(b => b.ContractHours == e.ContractHours && b.RoleClass == roleClass);
                    var (annual, sick) = balance[e.Id];
                    // Balances grow by a fortnight's accrual from one period end to the next.
                    var accrual = band is null ? 0 : band.AnnualAccrualHours / 26;
                    snapshots.Add(new LeaveLiabilitySnapshot
                    {
                        StoreId = s.Store.Id, PeriodEnd = ends[k], UkgEmployeeId = e.UkgId!, UkgLocation = $"{s.Def.Code} {s.Def.Name}",
                        EmployeeName = e.Name, RoleClass = roleClass, ContractHours = e.ContractHours,
                        Job = e.Role switch { Roles.Sm => "ST STORE MANAGER", Roles.Asm => "ST ASSIST MANAGER", Roles.Dsm => "ST DUTY STORE MANAGER", Roles.Jsa => "ST ASSIST JNR CASUAL", _ => "ST ASSIST" },
                        NotionalHours = band?.LeaveNotionalHours ?? R2(e.ContractHours / 10),
                        AnnualHours = roleClass == "CASUAL" ? 0 : R2(annual + accrual * k), SickHours = roleClass == "CASUAL" ? 0 : R2(sick + 3.08 * k),
                        LieuHours = e == people[1] ? -2.5 : 0, BankedHours = 0, Banked2Hours = 0,
                        BatchId = batches[k].Id, CreatedAt = batches[k].UploadedAt,
                    });
                }
            string[] types = ["REQ - Annual Leave", "REQ - Annual Leave", "REQ - Sick Leave", "REQ - Carers Leave", "REQ - Unpaid Leave", "REQ - Annual Leave"];
            string[] statuses = ["Approved", "Submitted", "Approved", "Approved", "Refused", "Cancelled"];
            for (var i = 0; i < types.Length; i++)
            {
                var e = people[(i * 3 + 2) % people.Count];
                var start = today.AddDays(-40 + i * 23);
                requests.Add(new LeaveRequestUkg
                {
                    StoreId = s.Store.Id, UkgEmployeeId = e.UkgId!, EmployeeName = e.Name, LeaveType = types[i], StartDate = start,
                    EndDate = start.AddDays(i % 2 == 0 ? 4 : 0), Status = statuses[i], Approver = people[0].Name, BatchId = requestBatch.Id,
                    CreatedAt = requestBatch.UploadedAt,
                });
            }
        }
        foreach (var b in batches) b.TotalRows = b.AcceptedRows = snapshots.Count(x => x.BatchId == b.Id);
        requestBatch.TotalRows = requestBatch.AcceptedRows = requests.Count;
        batches.Add(requestBatch);
        await SaveAsync("upload_batches", batches, ct);
        await SaveAsync("leave_liability_snapshots", snapshots, ct);
        await SaveAsync("leave_requests_ukg", requests, ct);
    }

    // ── Odds and ends ────────────────────────────────────────────────────────

    private async Task ExtrasAsync(CancellationToken ct)
    {
        await SaveAsync("feed_tokens", [new FeedToken { Label = "Example integration (development)", Token = Hex(32), CreatedAt = At(today.AddDays(-30), 10) }], ct);
        // Last fortnight's EMSO alerts, already read (the seeder runs this fortnight's check itself).
        var label = Period.PeriodLabel(Current);
        await SaveAsync("notifications", new[] { "emso1", "emso2" }.Select(u => new Notification
        {
            UserId = _users[u], Kind = Jobs.EmsoAlert.Kind, Link = Jobs.EmsoAlert.Link,
            Body = $"1 store(s) without an approved roster for {label}: {(u == "emso1" ? "G102" : "G218")}",
            CreatedAt = At(Previous.AddDays(9), 18), ReadAt = At(Previous.AddDays(10), 8),
        }), ct);
    }
}
