using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;

namespace Roster.Data.Model;

/// <summary>
/// The schema, table by table, in its final state after the 49 Postgres migrations plus
/// the tables the live database held that no migration recorded. Delete behaviour:
/// SQL Server refuses a foreign key that opens a second cascade path to a table (error
/// 1785), so where Postgres cascaded or set null along more than one path, one of the
/// keys is NO ACTION here and Services/DeletionService performs the same cascade in code.
/// Every such case is marked "(code cascade)".
/// </summary>
internal static partial class RosterModel
{
    public static void Configure(ModelBuilder mb, SqlDialect d)
    {
        Organisation(mb, d);
        Users(mb, d);
        Calendar(mb, d);
        Ingest(mb, d);
        Team(mb, d);
        Planning(mb, d);
        Reviews(mb, d);
        Rules(mb, d);
        Payroll(mb, d);
        Leave(mb, d);
        Kiosk(mb, d);
    }

    private static void Organisation(ModelBuilder mb, SqlDialect d)
    {
        mb.Entity<DirectorGroup>(e =>
        {
            e.ToTable("director_groups");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.Name).IsRequired().Text(200);
            e.Property(x => x.CreatedAt).Now(d);
        });

        mb.Entity<Region>(e =>
        {
            e.ToTable("regions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.Name).IsRequired().Text(200);
            e.Property(x => x.CreatedAt).Now(d);
            e.HasOne(x => x.DirectorGroup).WithMany().HasForeignKey(x => x.DirectorGroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.DirectorGroupId).HasDatabaseName("regions_dg_idx");
        });

        mb.Entity<Area>(e =>
        {
            e.ToTable("areas");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.Name).IsRequired().Text(200);
            e.HasIndex(x => x.Name).IsUnique().HasDatabaseName("areas_name_key");
            e.HasOne(x => x.Region).WithMany().HasForeignKey(x => x.RegionId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => x.RegionId).HasDatabaseName("areas_region_idx");
        });

        mb.Entity<Store>(e =>
        {
            e.ToTable("stores", t => t.HasCheckConstraint("stores_state_check", d.In("state", AuStates.All.ToArray())));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.Code).IsRequired().Text(16);
            e.HasIndex(x => x.Code).IsUnique().HasDatabaseName("stores_code_key");
            e.Property(x => x.Name).IsRequired().Text(200);
            e.Property(x => x.Slug).IsRequired().Text(64);
            e.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("stores_slug_key");
            e.Property(x => x.State).IsRequired().Text(3);
            e.Property(x => x.CreatedAt).Now(d);
            e.Property(x => x.KioskToken).IsRequired().Text(64).DefaultSql(d.RandomHex32);
            e.HasIndex(x => x.KioskToken).IsUnique().HasDatabaseName("stores_kiosk_token_key");
            e.Property(x => x.DsoName).Text(200);
            // RESTRICT in Postgres: a region with stores cannot be deleted.
            e.HasOne(x => x.Region).WithMany().HasForeignKey(x => x.RegionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Area).WithMany().HasForeignKey(x => x.AreaId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => x.RegionId).HasDatabaseName("stores_region_idx");
            e.HasIndex(x => x.AreaId).HasDatabaseName("stores_area_idx");
            e.HasOne(x => x.Attributes).WithOne().HasForeignKey<StoreAttributes>(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.TradingHours).WithOne().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<StoreTradingHours>(e =>
        {
            e.ToTable("store_trading_hours", t => t.HasCheckConstraint("store_trading_hours_day_of_week_check", "[day_of_week] BETWEEN 0 AND 6"));
            e.HasKey(x => new { x.StoreId, x.DayOfWeek });
            e.Property(x => x.OpenTime).Time(d);
            e.Property(x => x.CloseTime).Time(d);
            e.Property(x => x.Closed).Default(false);
        });

        mb.Entity<StoreAttributes>(e =>
        {
            e.ToTable("store_attributes", t =>
            {
                t.HasCheckConstraint("store_attributes_sco_share_check", "[sco_share] BETWEEN 0 AND 0.90");
                t.HasCheckConstraint("store_attributes_max_staff_on_leave_check", "[max_staff_on_leave] >= 0");
            });
            e.HasKey(x => x.StoreId);
            e.Property(x => x.HasSco).Default(false);
            e.Property(x => x.IsNewScoModel).Default(false);
            e.Property(x => x.McoRegisters).Default(0);
            e.Property(x => x.ScoLanes).Default(0);
            e.Property(x => x.ScoEfficiency).Default(0.45m);
            e.Property(x => x.FirstStep).Default(1.2m);
            e.Property(x => x.ScoShare).Default(0.66m);
            e.Property(x => x.OeTargetPct).Default(90m);
            e.Property(x => x.UpdatedAt).Now(d, alsoOnUpdate: true);
            e.Property(x => x.MaxStaffOnLeave).Default((short)3);
            e.Property(x => x.NightFill).Default(false);
            // A second path from stores (store_id already cascades): NO ACTION (code cascade).
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.PairStoreId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => x.PairStoreId).HasDatabaseName("store_attributes_pair_idx");
        });

        mb.Entity<SpecialTradingHours>(e =>
        {
            e.ToTable("special_trading_hours");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.Closed).Default(false);
            e.Property(x => x.OpenTime).Time(d);
            e.Property(x => x.CloseTime).Time(d);
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.StoreId, x.StartDate }).HasDatabaseName("special_trading_hours_store_idx");
        });
    }

    private static void Users(ModelBuilder mb, SqlDialect d)
    {
        mb.Entity<AppUser>(e =>
        {
            e.ToTable("app_users", t =>
            {
                t.HasCheckConstraint("app_users_tier_check",
                    d.In("tier", TierNames.SuperUser, TierNames.Store, TierNames.Pa, TierNames.Director, TierNames.Emso));
                // 0016's tier_scope: exactly the right scope column is set for each tier.
                t.HasCheckConstraint("tier_scope",
                    "([tier] = 'super_user' AND [store_id] IS NULL AND [region_id] IS NULL AND [director_group_id] IS NULL)"
                    + " OR ([tier] = 'store' AND [store_id] IS NOT NULL)"
                    + " OR ([tier] = 'pa')"
                    + " OR ([tier] = 'director')"
                    + " OR ([tier] = 'emso' AND [area_id] IS NOT NULL AND [store_id] IS NULL AND [director_group_id] IS NULL)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.Username).IsRequired().Text(256);
            e.HasIndex(x => x.Username).IsUnique().HasDatabaseName("app_users_username_key");
            e.Property(x => x.Tier).IsRequired().HasMaxLength(16)
                .HasConversion(v => TierNames.ToDb(v), v => TierNames.FromDb(v));
            e.Property(x => x.CreatedAt).Now(d);
            e.Property(x => x.EntraObjectId).Text(64);
            e.Property(x => x.UserPrincipalName).Text(256);
            e.Property(x => x.WindowsAccount).Text(256);
            e.Property(x => x.DisplayName).Text(256);
            e.HasIndex(x => x.EntraObjectId).IsUnique().HasFilter("[entra_object_id] IS NOT NULL").HasDatabaseName("app_users_entra_object_id_key");
            e.HasIndex(x => x.UserPrincipalName).IsUnique().HasFilter("[user_principal_name] IS NOT NULL").HasDatabaseName("app_users_user_principal_name_key");
            e.HasIndex(x => x.WindowsAccount).IsUnique().HasFilter("[windows_account] IS NOT NULL").HasDatabaseName("app_users_windows_account_key");

            // Postgres: store_id / region_id / director_group_id ON DELETE SET NULL. Only
            // region_id keeps it here; the other two would open second paths (code cascade).
            e.HasOne(x => x.Store).WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Region).WithMany().HasForeignKey(x => x.RegionId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.DirectorGroup).WithMany().HasForeignKey(x => x.DirectorGroupId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Area).WithMany().HasForeignKey(x => x.AreaId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => x.StoreId).HasDatabaseName("app_users_store_idx");
            e.HasIndex(x => x.RegionId).HasDatabaseName("app_users_region_idx");
            e.HasIndex(x => x.DirectorGroupId).HasDatabaseName("app_users_dg_idx");
            e.HasIndex(x => x.AreaId).HasDatabaseName("app_users_area_idx");
        });

        mb.Entity<Notification>(e =>
        {
            e.ToTable("notifications");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.Kind).IsRequired().Text(64);
            e.Property(x => x.Body).IsRequired();
            e.Property(x => x.Link).Text(500);
            e.Property(x => x.CreatedAt).Now(d);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.ReadAt }).HasDatabaseName("notifications_user_idx");
        });

        mb.Entity<TabVisibility>(e =>
        {
            e.ToTable("tab_visibility");
            e.HasKey(x => new { x.Level, x.TabKey });
            e.Property(x => x.Level).Text(16);
            e.Property(x => x.TabKey).Text(200);
            e.Property(x => x.Visible).Default(true);
        });
    }

    private static void Calendar(ModelBuilder mb, SqlDialect d)
    {
        var states = AuStates.All.ToArray();

        mb.Entity<PublicHolidayNational>(e =>
        {
            e.ToTable("public_holidays_national");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.Name).IsRequired().Text(200);
            e.HasIndex(x => new { x.Date, x.Name }).IsUnique().HasDatabaseName("public_holidays_national_date_name_key");
        });

        mb.Entity<PublicHolidayState>(e =>
        {
            e.ToTable("public_holidays_state", t => t.HasCheckConstraint("public_holidays_state_state_check", d.In("state", states)));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.State).IsRequired().Text(3);
            e.Property(x => x.Name).IsRequired().Text(200);
            e.HasIndex(x => new { x.State, x.Date, x.Name }).IsUnique().HasDatabaseName("public_holidays_state_state_date_name_key");
        });

        mb.Entity<PublicHolidayLocal>(e =>
        {
            e.ToTable("public_holidays_local");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.Name).IsRequired().Text(200);
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.StoreId, x.Date, x.Name }).IsUnique().HasDatabaseName("public_holidays_local_store_id_date_name_key");
        });

        mb.Entity<SchoolTerm>(e =>
        {
            e.ToTable("school_terms", t => t.HasCheckConstraint("school_terms_state_check", d.In("state", states)));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.State).IsRequired().Text(3);
            e.Property(x => x.TermLabel).IsRequired().Text(100);
        });

        mb.Entity<SchoolTermException>(e =>
        {
            e.ToTable("school_term_exceptions", t => t.HasCheckConstraint("school_term_exceptions_state_check", d.In("state", states)));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.State).IsRequired().Text(3);
        });

        mb.Entity<RosterPeriod>(e =>
        {
            e.ToTable("roster_periods");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.HasIndex(x => x.StartDate).IsUnique().HasDatabaseName("roster_periods_start_date_key");
            e.Property(x => x.CreatedAt).Now(d);
        });
    }
}
