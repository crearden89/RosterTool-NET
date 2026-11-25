using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Roster.Data.Scope;
using Roster.Data.Entities;
using Roster.Data.Configurations;

namespace Roster.Data;

/// <summary>
/// The roster database. Every instance works under one <see cref="AccessContext"/>:
/// reads are filtered by the tables' row-level-security policies (as global query
/// filters, see <see cref="ScopeFilters"/>) and writes are checked against the same
/// policies' write side before anything reaches the database (see <see cref="WriteScopeGuard"/>).
/// </summary>
public sealed partial class RosterDbContext : DbContext
{
    private readonly TimeProvider _clock;

    public RosterDbContext(DbContextOptions<RosterDbContext> options, IAccessContextAccessor accessor, TimeProvider? clock = null)
        : this(options, accessor.Current, clock) { }

    public RosterDbContext(DbContextOptions<RosterDbContext> options, AccessContext access, TimeProvider? clock = null)
        : base(options)
    {
        Access = access ?? throw new ArgumentNullException(nameof(access));
        _clock = clock ?? TimeProvider.System;

        var read = access.ReadScope;
        QfUnrestricted = access.Kind is AccessKind.System or AccessKind.Feed;
        QfSignedIn = access.Kind is AccessKind.User or AccessKind.Kiosk or AccessKind.Feed or AccessKind.System;
        QfAllStores = read is StoreSet.All;
        QfStoreId = read is StoreSet.OneStore s ? s.StoreId : null;
        QfAreaId = read is StoreSet.OneArea a ? a.AreaId : null;
        QfUserId = access.User?.Id;
        QfSuperUser = access.IsSuperUser;
        QfKioskStoreId = access.KioskStoreId;
    }

    /// <summary>Who this context reads and writes as.</summary>
    public AccessContext Access { get; }

    internal TimeProvider Clock => _clock;

    // ── Query-filter parameters: read by the global filters on every query ──
    internal bool QfUnrestricted { get; }
    internal bool QfSignedIn { get; }
    internal bool QfAllStores { get; }
    internal Guid? QfStoreId { get; }
    internal Guid? QfAreaId { get; }
    internal Guid? QfUserId { get; }
    internal bool QfSuperUser { get; }
    internal Guid? QfKioskStoreId { get; }

    public DatabaseProvider Provider => DatabaseProviderExtensions.FromProviderName(Database.ProviderName);

    // ── Organisation and users ──
    public DbSet<DirectorGroup> DirectorGroups => Set<DirectorGroup>();
    public DbSet<Region> Regions => Set<Region>();
    public DbSet<Area> Areas => Set<Area>();
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<StoreTradingHours> StoreTradingHours => Set<StoreTradingHours>();
    public DbSet<StoreAttributes> StoreAttributes => Set<StoreAttributes>();
    public DbSet<SpecialTradingHours> SpecialTradingHours => Set<SpecialTradingHours>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<TabVisibility> TabVisibility => Set<TabVisibility>();

    // ── Calendars ──
    public DbSet<PublicHolidayNational> PublicHolidaysNational => Set<PublicHolidayNational>();
    public DbSet<PublicHolidayState> PublicHolidaysState => Set<PublicHolidayState>();
    public DbSet<PublicHolidayLocal> PublicHolidaysLocal => Set<PublicHolidayLocal>();
    public DbSet<SchoolTerm> SchoolTerms => Set<SchoolTerm>();
    public DbSet<SchoolTermException> SchoolTermExceptions => Set<SchoolTermException>();
    public DbSet<RosterPeriod> RosterPeriods => Set<RosterPeriod>();

    // ── Uploads and feeds ──
    public DbSet<UploadBatch> UploadBatches => Set<UploadBatch>();
    public DbSet<BenchmarkDay> BenchmarkDays => Set<BenchmarkDay>();
    public DbSet<QuarterHourSegment> QuarterHourSegments => Set<QuarterHourSegment>();
    public DbSet<ActualsDay> ActualsDays => Set<ActualsDay>();
    public DbSet<QuarterHourAllocation> QuarterHourAllocations => Set<QuarterHourAllocation>();
    public DbSet<PosIntervalStat> PosIntervalStats => Set<PosIntervalStat>();
    public DbSet<FeedToken> FeedTokens => Set<FeedToken>();
    public DbSet<CronRun> CronRuns => Set<CronRun>();

    // ── Team ──
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<EmployeeAvailability> EmployeeAvailability => Set<EmployeeAvailability>();
    public DbSet<EmployeeLeave> EmployeeLeave => Set<EmployeeLeave>();
    public DbSet<EmployeeRestrictedTask> EmployeeRestrictedTasks => Set<EmployeeRestrictedTask>();
    public DbSet<DayOffRequest> DayOffRequests => Set<DayOffRequest>();
    public DbSet<WeekendMarker> WeekendMarkers => Set<WeekendMarker>();
    public DbSet<FixedRoster> FixedRosters => Set<FixedRoster>();
    public DbSet<FixedRosterCycle> FixedRosterCycles => Set<FixedRosterCycle>();

    // ── Plan and roster ──
    public DbSet<DayPlan> DayPlans => Set<DayPlan>();
    public DbSet<PlannedBlock> PlannedBlocks => Set<PlannedBlock>();
    public DbSet<PlanSnapshot> PlanSnapshots => Set<PlanSnapshot>();
    public DbSet<SalesOverride> SalesOverrides => Set<SalesOverride>();
    public DbSet<NonOpCategory> NonOpCategories => Set<NonOpCategory>();
    public DbSet<NonOpSubcategory> NonOpSubcategories => Set<NonOpSubcategory>();
    public DbSet<NonOpEntry> NonOpEntries => Set<NonOpEntry>();
    public DbSet<RosterAudit> RosterAudit => Set<RosterAudit>();
    public DbSet<GeneratedShift> GeneratedShifts => Set<GeneratedShift>();
    public DbSet<ShiftTaskBlock> ShiftTaskBlocks => Set<ShiftTaskBlock>();
    public DbSet<RosterReview> RosterReviews => Set<RosterReview>();
    public DbSet<RosterReviewCheck> RosterReviewChecks => Set<RosterReviewCheck>();
    public DbSet<RosterReviewDay> RosterReviewDays => Set<RosterReviewDay>();
    public DbSet<RosterReviewEvent> RosterReviewEvents => Set<RosterReviewEvent>();

    // ── Rules and pay ──
    public DbSet<RosteringRule> RosteringRules => Set<RosteringRule>();
    public DbSet<RosterOrderStep> RosterOrderSteps => Set<RosterOrderStep>();
    public DbSet<BreakRule> BreakRules => Set<BreakRule>();
    public DbSet<LabourGroupRule> LabourGroupRules => Set<LabourGroupRule>();
    public DbSet<LabourGroupPriority> LabourGroupPriority => Set<LabourGroupPriority>();
    public DbSet<AdminTask> AdminTasks => Set<AdminTask>();
    public DbSet<ResetTask> ResetTasks => Set<ResetTask>();
    public DbSet<PlanPlacementRule> PlanPlacementRules => Set<PlanPlacementRule>();
    public DbSet<PlanPlacementSettings> PlanPlacementSettings => Set<PlanPlacementSettings>();
    public DbSet<VarianceSettings> VarianceSettings => Set<VarianceSettings>();
    public DbSet<ProbationBuffer> ProbationBuffers => Set<ProbationBuffer>();
    public DbSet<PayRate> PayRates => Set<PayRate>();
    public DbSet<PaySettings> PaySettings => Set<PaySettings>();
    public DbSet<PayrollTaxRate> PayrollTaxRates => Set<PayrollTaxRate>();

    // ── Leave ──
    public DbSet<LeaveLiabilitySnapshot> LeaveLiabilitySnapshots => Set<LeaveLiabilitySnapshot>();
    public DbSet<LeaveRequestUkg> LeaveRequestsUkg => Set<LeaveRequestUkg>();
    public DbSet<LeaveAccrualBand> LeaveAccrualBands => Set<LeaveAccrualBand>();
    public DbSet<LeaveSettings> LeaveSettings => Set<LeaveSettings>();

    // ── Kiosk ──
    public DbSet<LoadType> LoadTypes => Set<LoadType>();
    public DbSet<DailyLoad> DailyLoads => Set<DailyLoad>();
    public DbSet<LoadAssignment> LoadAssignments => Set<LoadAssignment>();
    public DbSet<StoreWalkNote> StoreWalkNotes => Set<StoreWalkNote>();
    public DbSet<AdminTaskCheck> AdminTaskChecks => Set<AdminTaskCheck>();

    protected override void ConfigureConventions(ModelConfigurationBuilder cb)
    {
        // Postgres numeric → float: every number the app holds is a JavaScript double, so
        // the column stores exactly the double written and reads it back unchanged.
        cb.Properties<string>().AreUnicode();

        if (Provider == DatabaseProvider.Sqlite)
        {
            // SQLite (tests only) cannot compare or order DateTimeOffset text.
            cb.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
            cb.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var dialect = new SqlDialect(Provider);
        RosterModel.Configure(modelBuilder, dialect);
        SnakeCaseNames.Apply(modelBuilder);
        ScopeFilters.Apply(modelBuilder, this);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareForSave();
        WriteScopeGuard.EnforceAsync(this, CancellationToken.None).GetAwaiter().GetResult();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        PrepareForSave();
        await WriteScopeGuard.EnforceAsync(this, cancellationToken);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Stamp default(now()) timestamps the code left unset, as Postgres defaults would.</summary>
    private void PrepareForSave()
    {
        ChangeTracker.DetectChanges();
        var now = _clock.GetUtcNow();
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
            foreach (var p in entry.Properties)
            {
                if (p.Metadata.ClrType != typeof(DateTimeOffset)) continue;
                var onInsert = p.Metadata.FindAnnotation(ModelBuilderExtensions.StampOnInsert) is not null;
                var onUpdate = p.Metadata.FindAnnotation(ModelBuilderExtensions.StampOnUpdate) is not null;
                if (entry.State == EntityState.Added && onInsert && (DateTimeOffset)p.CurrentValue! == default)
                    p.CurrentValue = now;
                else if (entry.State == EntityState.Modified && onUpdate && !p.IsModified)
                    p.CurrentValue = now;
            }
        }
    }
}
