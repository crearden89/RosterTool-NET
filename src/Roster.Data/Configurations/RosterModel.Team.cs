using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;

namespace Roster.Data.Configurations;

internal static partial class RosterModel
{
    private static void Team(ModelBuilder mb, SqlDialect d)
    {
        mb.Entity<Position>(e =>
        {
            e.ToTable("positions", t =>
            {
                t.HasCheckConstraint("positions_base_role_check", d.In("base_role", Roles.All));
                t.HasCheckConstraint("positions_weekly_hours_check", "[weekly_hours] >= 0");
                t.HasCheckConstraint("positions_year_band_check", "[year_band] >= 0");
            });
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).Text(32);
            e.Property(x => x.Label).IsRequired().Text(200);
            e.Property(x => x.BaseRole).IsRequired().Text(8);
            e.Property(x => x.HasRate).Default(true);
            e.Property(x => x.SortOrder).Default(0);
            e.Property(x => x.Active).Default(true);
        });

        mb.Entity<Employee>(e =>
        {
            e.ToTable("employees", t =>
            {
                t.HasCheckConstraint("employees_role_check", d.In("role", Roles.All));
                t.HasCheckConstraint("employees_notional_hours_check",
                    "[notional_hours] IS NULL OR ([notional_hours] >= 0 AND [notional_hours] <= 24)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.Name).IsRequired().Text(200);
            e.Property(x => x.Role).IsRequired().Text(8);
            e.Property(x => x.ContractHours).Default(0.0);
            e.Property(x => x.Active).Default(true);
            e.Property(x => x.CreatedAt).Now(d);
            e.Property(x => x.PositionCode).Text(32);
            e.Property(x => x.UkgId).Text(64);
            e.HasOne(x => x.Store).WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Position).WithMany().HasForeignKey(x => x.PositionCode).OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("employees_position_code_fkey");
            e.HasIndex(x => new { x.StoreId, x.Active }).HasDatabaseName("employees_store_idx");
            e.HasIndex(x => x.PositionCode).HasDatabaseName("employees_position_idx");
            // 0036: unique per store, not globally; only where a UKG id is known.
            e.HasIndex(x => new { x.StoreId, x.UkgId }).IsUnique()
                .HasFilter("[ukg_id] IS NOT NULL").HasDatabaseName("employees_store_ukg_idx");
        });

        mb.Entity<EmployeeAvailability>(e =>
        {
            e.ToTable("employee_availability", t => t.HasCheckConstraint("employee_availability_day_of_week_check", "[day_of_week] BETWEEN 0 AND 6"));
            e.HasKey(x => new { x.EmployeeId, x.DayOfWeek });
            e.Property(x => x.Available).Default(true);
            e.Property(x => x.FromTime).Time(d);
            e.Property(x => x.ToTime).Time(d);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<EmployeeLeave>(e =>
        {
            e.ToTable("employee_leave", t =>
            {
                t.HasCheckConstraint("employee_leave_type_check", d.In("leave_type", LeaveTypes.All));
                t.HasCheckConstraint("employee_leave_day_hours_check",
                    "[day_hours] IS NULL OR ([day_hours] > 0 AND [day_hours] <= 24)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.LeaveType).IsRequired().Text(8).Default(LeaveTypes.Annual);
            e.Property(x => x.CreatedAt).Now(d);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.EmployeeId, x.StartDate }).HasDatabaseName("employee_leave_employee_idx");
        });

        mb.Entity<EmployeeRestrictedTask>(e =>
        {
            e.ToTable("employee_restricted_tasks");
            e.HasKey(x => new { x.EmployeeId, x.LabourGroup });
            e.Property(x => x.LabourGroup).Text(32);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<DayOffRequest>(e =>
        {
            e.ToTable("day_off_requests", t => t.HasCheckConstraint("day_off_requests_status_check", d.In("status", DayOffStatuses.All)));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.SubmittedAt).Now(d);
            e.Property(x => x.Status).IsRequired().Text(16).Default(DayOffStatuses.Pending);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.EmployeeId, x.Date }).HasDatabaseName("day_off_requests_employee_idx");
        });

        mb.Entity<WeekendMarker>(e =>
        {
            e.ToTable("weekend_markers");
            e.HasKey(x => new { x.EmployeeId, x.WeekendStart });
            e.Property(x => x.SatOff).Default(false);
            e.Property(x => x.SunOff).Default(false);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.WeekendStart).HasDatabaseName("weekend_markers_weekend_idx");
        });

        mb.Entity<FixedRoster>(e =>
        {
            e.ToTable("fixed_rosters", t =>
            {
                t.HasCheckConstraint("fixed_rosters_day_of_week_check", "[day_of_week] BETWEEN 0 AND 6");
                t.HasCheckConstraint("fixed_rosters_week_index_check", "[week_index] >= 0 AND [week_index] <= 3");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.StartTime).Time(d);
            e.Property(x => x.EndTime).Time(d);
            e.Property(x => x.WeekIndex).Default(0);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.EmployeeId, x.WeekIndex, x.DayOfWeek }).IsUnique()
                .HasDatabaseName("fixed_rosters_employee_week_day_key");
        });

        mb.Entity<FixedRosterCycle>(e =>
        {
            e.ToTable("fixed_roster_cycles", t =>
            {
                t.HasCheckConstraint("fixed_roster_cycles_cycle_weeks_check", "[cycle_weeks] IN (1, 2, 4)");
                t.HasCheckConstraint("fixed_roster_cycles_start_date_check", d.IsMonday("start_date"));
            });
            e.HasKey(x => x.EmployeeId);
            e.Property(x => x.CycleWeeks).Default(1);
            e.Property(x => x.UpdatedAt).Now(d, alsoOnUpdate: true);
            e.HasOne(x => x.Employee).WithOne().HasForeignKey<FixedRosterCycle>(x => x.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
