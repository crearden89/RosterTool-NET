using Roster.Data.Entities;

namespace Roster.Data.Scope;

/// <summary>Who may read a table's rows. Each maps to the table's Postgres SELECT policy.</summary>
public enum ReadRule
{
    /// <summary>Any signed-in user (or kiosk) reads every row: "using (auth.uid() is not null)" / "to authenticated using (true)".</summary>
    AnySignedIn,
    /// <summary>Rows of the stores the caller can see: "store_id in (select visible_store_ids())".</summary>
    VisibleStore,
    /// <summary>Rows of employees of the stores the caller can see.</summary>
    VisibleStoreViaEmployee,
    /// <summary>Rows of published shifts of the stores the caller can see.</summary>
    VisibleStoreViaShift,
    /// <summary>app_users: your own row, or every row for a super user (app_users_self).</summary>
    SelfOrSuperUser,
    /// <summary>notifications: your own, or every row for a super user (notif_sel).</summary>
    OwnOrSuperUser,
    /// <summary>Super user only (upload_batches ub_all).</summary>
    SuperUserOnly,
    /// <summary>RLS enabled with no policy: server code only (feed_tokens, cron_runs).</summary>
    ServerOnly,
    /// <summary>RLS enabled with no policy, read by the kiosk for its store (load_assignments).</summary>
    KioskStoreOrServer,
}

/// <summary>Who may insert, update or delete a table's rows.</summary>
public enum WriteRule
{
    /// <summary>Super user only (the Group A/B "_ins/_upd/_del" policies, and every admin screen).</summary>
    SuperUser,
    /// <summary>Stores the caller may WRITE: "store_id in (select writable_store_ids())" (0044) — authz.ts denyStoreWrite.</summary>
    WritableStore,
    /// <summary>The owning employee's store must be writable — authz.ts denyEmployeeWrite.</summary>
    WritableStoreViaEmployee,
    /// <summary>The owning shift's store must be writable.</summary>
    WritableStoreViaShift,
    /// <summary>The kiosk of that store only (service-role kiosk actions, token-checked).</summary>
    KioskStore,
    /// <summary>notifications: insert/delete super user; update own or super user.</summary>
    Notification,
    /// <summary>Server code only (cron_runs).</summary>
    ServerOnly,
}

/// <summary>A table's policy pair, and which feed may also write it.</summary>
public sealed record TablePolicy(ReadRule Read, WriteRule Write, bool FeedMayWrite = false, string? Source = null);

/// <summary>
/// The final (post-0049) row-level-security policy of every table, restated for SQL Server.
/// RosterDbContext turns the read rules into global query filters and checks the write
/// rules in SaveChanges. Tables created outside the migrations carry the policy their
/// group in 0006/0044 gave tables of the same kind.
/// </summary>
public static class TablePolicies
{
    private static TablePolicy A(string source) => new(ReadRule.AnySignedIn, WriteRule.SuperUser, Source: source);
    private static TablePolicy B(string source, bool feed = false) => new(ReadRule.VisibleStore, WriteRule.SuperUser, feed, source);
    private static TablePolicy C(string source) => new(ReadRule.VisibleStore, WriteRule.WritableStore, Source: source);

    public static readonly IReadOnlyDictionary<Type, TablePolicy> ByEntity = new Dictionary<Type, TablePolicy>
    {
        // ── Group A (0006): any signed-in user reads, super user writes ──
        [typeof(BreakRule)] = A("0006 br_*"),
        [typeof(DirectorGroup)] = A("0006 dg_*"),
        [typeof(LabourGroupPriority)] = A("0006 lgp_*"),
        [typeof(LabourGroupRule)] = A("0006 lgr_*"),
        [typeof(NonOpCategory)] = A("0006 noc_*"),
        [typeof(NonOpSubcategory)] = A("0006 nosc_*"),
        [typeof(PayRate)] = A("0006 pr_*"),
        [typeof(PaySettings)] = A("0006 ps_*"),
        [typeof(PayrollTaxRate)] = A("0006 ptr_*"),
        [typeof(PublicHolidayNational)] = A("0006 phn_*"),
        [typeof(PublicHolidayState)] = A("0006 phs_*"),
        [typeof(Region)] = A("0006 rg_*"),
        [typeof(RosterPeriod)] = A("0006 rp_*") with { FeedMayWrite = true },
        [typeof(SchoolTermException)] = A("0006 scte_*"),
        [typeof(SchoolTerm)] = A("0006 sct_*"),
        [typeof(TabVisibility)] = A("0006 tv_*"),
        [typeof(AdminTask)] = A("0019 at_*"),
        [typeof(Position)] = A("0020 pos_*"),
        [typeof(ResetTask)] = A("0038 rt_*"),
        // Read by any signed-in user; written only by server actions behind requireTier("super_user").
        [typeof(LoadType)] = A("0002 load_types_read"),
        [typeof(RosteringRule)] = A("0010 rostering_rules_read"),
        [typeof(RosterOrderStep)] = A("0011 roster_order_steps_read"),
        [typeof(Area)] = A("0016 areas readable"),
        [typeof(PlanPlacementRule)] = A("0021 plan_placement_rules_read"),
        [typeof(PlanPlacementSettings)] = A("0021 plan_placement_settings_read"),
        [typeof(VarianceSettings)] = A("0022 variance_settings_read"),
        [typeof(LeaveAccrualBand)] = A("0028 lab_read/lab_write, 0037"),
        [typeof(LeaveSettings)] = A("0028 lset_read/lset_write, 0037"),
        [typeof(ProbationBuffer)] = A("0030 pb_read/pb_write, 0037"),

        // ── Group B (0006): scoped read, super user writes ──
        [typeof(ActualsDay)] = B("0006 ad_*", feed: true),
        [typeof(BenchmarkDay)] = B("0006 bd_*", feed: true),
        [typeof(PublicHolidayLocal)] = B("0006 phl_*"),
        [typeof(QuarterHourAllocation)] = B("0006 qha_*"),
        [typeof(SpecialTradingHours)] = B("0006 sth_*"),
        [typeof(StoreAttributes)] = B("0006 sa_*"),
        [typeof(StoreTradingHours)] = B("0006 sth_*"),
        [typeof(Store)] = B("0006 st_* (keyed on id)"),
        [typeof(PosIntervalStat)] = B("0039 pis_*", feed: true),
        [typeof(QuarterHourSegment)] = B("0001 qh_read/qh_super"),

        // ── Group C (0006, write side 0044): scoped read, write where writable ──
        [typeof(DayPlan)] = C("0006 dp_write, 0044"),
        [typeof(Employee)] = C("0006 emp_write, 0044"),
        [typeof(PlannedBlock)] = C("0006 pb_write, 0044"),
        [typeof(GeneratedShift)] = C("0006 gs_all, 0044"),
        [typeof(NonOpEntry)] = C("0006 noe_all, 0044"),
        [typeof(RosterAudit)] = C("0006 ra_all, 0044"),
        [typeof(SalesOverride)] = C("0006 so_all, 0044"),
        [typeof(LeaveLiabilitySnapshot)] = C("0028 lls_all, 0044"),
        [typeof(LeaveRequestUkg)] = C("0028 lru_all, 0044"),
        // Read by the visible stores, as every other store table (0050: the owner tightened
        // 0003's "to authenticated using (true)"); written by the Plan page's actions behind
        // denyStoreWrite. Another store's held plan is read only through the peer gate
        // (IPlanSnapshotStore.ReadPeerPlanAsync), as the system.
        [typeof(PlanSnapshot)] = new(ReadRule.VisibleStore, WriteRule.WritableStore, Source: "0003, 0050 plan_snapshots_read"),
        // Reviews: scoped read; written by the review actions behind denyStoreWrite.
        [typeof(RosterReview)] = C("0045 roster_reviews_read"),
        [typeof(RosterReviewCheck)] = C("0045 roster_review_checks_read"),
        [typeof(RosterReviewDay)] = C("0045 roster_review_days_read"),
        [typeof(RosterReviewEvent)] = C("0045 roster_review_events_read"),

        // ── Employee-owned (frc_all 0025, and the live-database tables like it; 0044) ──
        [typeof(FixedRosterCycle)] = new(ReadRule.VisibleStoreViaEmployee, WriteRule.WritableStoreViaEmployee, Source: "0025 frc_all, 0044"),
        [typeof(FixedRoster)] = new(ReadRule.VisibleStoreViaEmployee, WriteRule.WritableStoreViaEmployee, Source: "live database, as frc_all"),
        [typeof(EmployeeAvailability)] = new(ReadRule.VisibleStoreViaEmployee, WriteRule.WritableStoreViaEmployee, Source: "live database, as frc_all"),
        [typeof(EmployeeLeave)] = new(ReadRule.VisibleStoreViaEmployee, WriteRule.WritableStoreViaEmployee, Source: "live database, as frc_all"),
        [typeof(EmployeeRestrictedTask)] = new(ReadRule.VisibleStoreViaEmployee, WriteRule.WritableStoreViaEmployee, Source: "live database, as frc_all"),
        [typeof(DayOffRequest)] = new(ReadRule.VisibleStoreViaEmployee, WriteRule.WritableStoreViaEmployee, Source: "live database, as frc_all"),
        [typeof(WeekendMarker)] = new(ReadRule.VisibleStoreViaEmployee, WriteRule.WritableStoreViaEmployee, Source: "live database, as frc_all"),
        [typeof(ShiftTaskBlock)] = new(ReadRule.VisibleStoreViaShift, WriteRule.WritableStoreViaShift, Source: "live database, as gs_all"),

        // ── Kiosk tables: scoped read; written by the token-checked kiosk actions ──
        // Read by the visible stores (0050: the owner tightened 0002's "to authenticated using
        // (true)"): a kiosk reads its own store's, as its context's one visible store.
        [typeof(DailyLoad)] = new(ReadRule.VisibleStore, WriteRule.KioskStore, Source: "0002, 0050 daily_loads_read"),
        [typeof(StoreWalkNote)] = new(ReadRule.VisibleStore, WriteRule.KioskStore, Source: "0034 swn_read"),
        [typeof(AdminTaskCheck)] = new(ReadRule.VisibleStore, WriteRule.KioskStore, Source: "0035 atc_read"),
        [typeof(LoadAssignment)] = new(ReadRule.KioskStoreOrServer, WriteRule.KioskStore, Source: "0017 (no policies)"),

        // ── Self-scoped ──
        [typeof(AppUser)] = new(ReadRule.SelfOrSuperUser, WriteRule.SuperUser, Source: "0006 app_users_self"),
        [typeof(Notification)] = new(ReadRule.OwnOrSuperUser, WriteRule.Notification, Source: "0006 notif_*"),

        // ── Super user / server only ──
        [typeof(UploadBatch)] = new(ReadRule.SuperUserOnly, WriteRule.SuperUser, FeedMayWrite: true, Source: "0006 ub_all"),
        // No policies (0015): only server code reads tokens — token checks run as System,
        // and Admin → Data Feed is a super-user screen.
        [typeof(FeedToken)] = new(ReadRule.SuperUserOnly, WriteRule.SuperUser, Source: "0015 (no policies; Admin → Data Feed)"),
        [typeof(CronRun)] = new(ReadRule.ServerOnly, WriteRule.ServerOnly, Source: "0043 (no policies)"),
    };

    public static TablePolicy For(Type entityType) =>
        ByEntity.TryGetValue(entityType, out var p)
            ? p
            : throw new InvalidOperationException($"No access policy is defined for {entityType.Name}.");
}
