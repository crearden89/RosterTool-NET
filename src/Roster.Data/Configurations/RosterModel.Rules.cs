using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;

namespace Roster.Data.Configurations;

internal static partial class RosterModel
{
    private static readonly string[] PlacementStrategies =
        ["spread", "follow_demand", "backfill", "forward_fill", "peak_split", "avoid_peak"];

    private static void Rules(ModelBuilder mb, SqlDialect d)
    {
        mb.Entity<RosteringRule>(e =>
        {
            e.ToTable("rostering_rules");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).Text(100);
            e.Property(x => x.UpdatedAt).Now(d, alsoOnUpdate: true);
        });

        mb.Entity<RosterOrderStep>(e =>
        {
            e.ToTable("roster_order_steps", t =>
            {
                t.HasCheckConstraint("roster_order_steps_step_type_check", d.In("step_type", RosterStepTypes.All));
                t.HasCheckConstraint("roster_order_steps_day_order_check", d.IsJson("day_order"));
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.StepType).IsRequired().Text(32);
            e.Property(x => x.DayOrder).Json(d);
            e.Property(x => x.UpdatedAt).Now(d, alsoOnUpdate: true);
        });

        mb.Entity<BreakRule>(e =>
        {
            e.ToTable("break_rules", t =>
            {
                t.HasCheckConstraint("break_rules_role_check", d.In("role", Roles.All));
                t.HasCheckConstraint("break_rules_break_type_check", d.In("break_type", BreakTypes.Flat30Unpaid, BreakTypes.Tiered));
            });
            e.HasKey(x => x.Role);
            e.Property(x => x.Role).Text(8);
            e.Property(x => x.BreakType).IsRequired().Text(32);
            e.Property(x => x.Ot15AfterHours).HasColumnName("ot_1_5_after_hours");
            e.Property(x => x.Ot19AfterHours).HasColumnName("ot_1_9_after_hours");
        });

        mb.Entity<LabourGroupRule>(e =>
        {
            e.ToTable("labour_group_rules", t =>
            {
                t.HasCheckConstraint("labour_group_rules_day_of_week_check", "[day_of_week] BETWEEN 0 AND 6");
                t.HasCheckConstraint("labour_group_rules_window_hours_check", "[window_hours] IS NULL OR [window_hours] > 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.LabourGroup).IsRequired().Text(32);
            e.Property(x => x.WindowStart).Time(d);
            e.Property(x => x.WindowEnd).Time(d);
            e.HasIndex(x => new { x.DayOfWeek, x.LabourGroup }).HasDatabaseName("labour_group_rules_day_idx");
        });

        mb.Entity<LabourGroupPriority>(e =>
        {
            e.ToTable("labour_group_priority");
            e.HasKey(x => x.LabourGroup);
            e.Property(x => x.LabourGroup).Text(32);
        });

        mb.Entity<AdminTask>(e =>
        {
            e.ToTable("admin_tasks", t =>
            {
                t.HasCheckConstraint("admin_tasks_schedule_type_check", d.In("schedule_type", "daily", "weekly", "monthly_nth", "planning_week"));
                t.HasCheckConstraint("admin_tasks_day_of_week_check", "[day_of_week] BETWEEN 0 AND 6");
                t.HasCheckConstraint("admin_tasks_nth_week_check", "[nth_week] BETWEEN 1 AND 5");
                t.HasCheckConstraint("admin_tasks_duration_type_check", d.In("duration_type", "fixed", "per_employee", "per_register", "till_calibration"));
                t.HasCheckConstraint("admin_tasks_fixed_minutes_check", "[fixed_minutes] >= 0");
                t.HasCheckConstraint("admin_tasks_minutes_per_unit_check", "[minutes_per_unit] >= 0");
                t.HasCheckConstraint("admin_tasks_min_minutes_check", "[min_minutes] >= 0");
                t.HasCheckConstraint("admin_tasks_window_start_kind_check", d.In("window_start_kind", WindowKinds.All));
                t.HasCheckConstraint("admin_tasks_window_end_kind_check", d.In("window_end_kind", WindowKinds.All));
                t.HasCheckConstraint("admin_tasks_region_days_check", d.IsJson("region_days"));
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.Name).IsRequired().Text(200);
            e.HasIndex(x => x.Name).IsUnique().HasDatabaseName("admin_tasks_name_key");
            e.Property(x => x.ScheduleType).IsRequired().Text(16);
            e.Property(x => x.RegionDays).Json(d);
            e.Property(x => x.DurationType).IsRequired().Text(32);
            e.Property(x => x.WindowStartKind).IsRequired().Text(8).Default(WindowKinds.None);
            e.Property(x => x.WindowStartTime).Time(d);
            e.Property(x => x.WindowStartOffsetMin).Default(0);
            e.Property(x => x.WindowEndKind).IsRequired().Text(8).Default(WindowKinds.None);
            e.Property(x => x.WindowEndTime).Time(d);
            e.Property(x => x.WindowEndOffsetMin).Default(0);
            e.Property(x => x.SortOrder).Default(0);
            e.Property(x => x.Active).Default(true);
        });

        mb.Entity<ResetTask>(e =>
        {
            e.ToTable("reset_tasks", t =>
            {
                t.HasCheckConstraint("reset_tasks_share_pct_check", "[share_pct] > 0 AND [share_pct] <= 100");
                t.HasCheckConstraint("reset_tasks_fixed_minutes_check", "[fixed_minutes] > 0");
                t.HasCheckConstraint("reset_tasks_one_sizing", "[share_pct] IS NULL OR [fixed_minutes] IS NULL");
                t.HasCheckConstraint("reset_tasks_window_start_kind_check", d.In("window_start_kind", WindowKinds.All));
                t.HasCheckConstraint("reset_tasks_window_end_kind_check", d.In("window_end_kind", WindowKinds.All));
                t.HasCheckConstraint("reset_tasks_placement_check", d.In("placement", "spread", "avoid_peak", "follow_demand", "late", "early"));
                t.HasCheckConstraint("reset_tasks_sub_task_check", $"[sub_task] IS NULL OR {d.In("sub_task", "re_runs", "decarding")}");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.Name).IsRequired().Text(200);
            e.HasIndex(x => x.Name).IsUnique().HasDatabaseName("reset_tasks_name_key");
            e.Property(x => x.WindowStartKind).IsRequired().Text(8).Default(WindowKinds.None);
            e.Property(x => x.WindowStartTime).Time(d);
            e.Property(x => x.WindowStartOffsetMin).Default(0);
            e.Property(x => x.WindowEndKind).IsRequired().Text(8).Default(WindowKinds.None);
            e.Property(x => x.WindowEndTime).Time(d);
            e.Property(x => x.WindowEndOffsetMin).Default(0);
            e.Property(x => x.SortOrder).Default(0);
            e.Property(x => x.Active).Default(true);
            e.Property(x => x.Placement).IsRequired().Text(16).Default("spread");
            e.Property(x => x.SubTask).Text(32);
        });

        mb.Entity<PlanPlacementRule>(e =>
        {
            e.ToTable("plan_placement_rules", t =>
            {
                t.HasCheckConstraint("plan_placement_rules_labour_group_check",
                    d.In("labour_group", "checkout", "load_running", "reset", "admin", "specials", "cleaning"));
                t.HasCheckConstraint("plan_placement_rules_strategy_check", d.In("strategy", PlacementStrategies));
                t.HasCheckConstraint("plan_placement_rules_anchor_kind_check", d.In("anchor_kind", WindowKinds.All));
                t.HasCheckConstraint("plan_placement_rules_pre_peak_pct_check", "[pre_peak_pct] BETWEEN 0 AND 100");
                t.HasCheckConstraint("plan_placement_rules_post_peak_pct_check", "[post_peak_pct] BETWEEN 0 AND 100");
                t.HasCheckConstraint("plan_placement_rules_avoid_strength_check", "[avoid_strength] BETWEEN 0 AND 100");
                t.HasCheckConstraint("plan_placement_rules_night_trader_strategy_check", d.In("night_trader_strategy", PlacementStrategies));
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.LabourGroup).IsRequired().Text(32);
            e.HasIndex(x => x.LabourGroup).IsUnique().HasDatabaseName("plan_placement_rules_labour_group_key");
            e.Property(x => x.Strategy).IsRequired().Text(32);
            e.Property(x => x.AnchorKind).IsRequired().Text(8).Default(WindowKinds.None);
            e.Property(x => x.AnchorTime).Time(d);
            e.Property(x => x.AnchorOffsetMin).Default(0);
            e.Property(x => x.NightTraderStrategy).Text(32);
            e.Property(x => x.UpdatedAt).Now(d, alsoOnUpdate: true);
        });

        mb.Entity<PlanPlacementSettings>(e =>
        {
            e.ToTable("plan_placement_settings", t =>
            {
                // id boolean primary key default true check (id): a second row cannot exist.
                t.HasCheckConstraint("plan_placement_settings_id_check", "[id] = 1");
                t.HasCheckConstraint("plan_placement_settings_peak_basis_check", d.In("peak_basis", "hourly", "rolling"));
                t.HasCheckConstraint("plan_placement_settings_peak_smoothing_blocks_check", "[peak_smoothing_blocks] BETWEEN 1 AND 16");
                t.HasCheckConstraint("plan_placement_settings_peak_threshold_pct_check", "[peak_threshold_pct] BETWEEN 1 AND 100");
                t.HasCheckConstraint("plan_placement_settings_night_trader_share_pct_check", "[night_trader_share_pct] BETWEEN 0 AND 100");
                t.HasCheckConstraint("plan_placement_settings_step_order_check", d.IsJson("step_order"));
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).Default(true);
            e.Property(x => x.Enabled).Default(false);
            e.Property(x => x.PeakBasis).IsRequired().Text(16).Default("hourly");
            e.Property(x => x.PeakSmoothingBlocks).Default(3);
            e.Property(x => x.PeakThresholdPct).Default(85.0);
            e.Property(x => x.NightTraderFrom).Time(d).Default(new TimeOnly(17, 0));
            e.Property(x => x.NightTraderSharePct).Default(25.0);
            e.Property(x => x.UpdatedAt).Now(d, alsoOnUpdate: true);
            e.Property(x => x.StepOrder).IsRequired().Json(d)
                .HasDefaultValueSql(d.Text("[\"admin\",\"load_running\",\"reset\",\"specials\",\"cleaning\"]"))
                .ValueGeneratedNever();
        });

        mb.Entity<VarianceSettings>(e =>
        {
            e.ToTable("variance_settings", t =>
            {
                t.HasCheckConstraint("variance_settings_id_check", "[id] = 1");
                t.HasCheckConstraint("variance_settings_data_light_pct_check", "[data_light_pct] BETWEEN 0 AND 100");
                t.HasCheckConstraint("variance_settings_metric_pct_check", "[metric_pct] BETWEEN 0 AND 500");
                t.HasCheckConstraint("variance_settings_hours_pct_check", "[hours_pct] BETWEEN 0 AND 500");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).Default(true);
            e.Property(x => x.DataLightPct).Default(15.0);
            e.Property(x => x.MetricPct).Default(70.0);
            e.Property(x => x.HoursPct).Default(20.0);
            e.Property(x => x.UpdatedAt).Now(d, alsoOnUpdate: true);
        });

        mb.Entity<ProbationBuffer>(e =>
        {
            e.ToTable("probation_buffers", t =>
            {
                t.HasCheckConstraint("probation_buffers_max_days_check", "[max_days] > 0");
                t.HasCheckConstraint("probation_buffers_uplift_pct_check", "[uplift_pct] >= 0 AND [uplift_pct] <= 300");
            });
            e.HasKey(x => new { x.LabourGroup, x.MaxDays });
            e.Property(x => x.LabourGroup).Text(32).Default("*");
            e.Property(x => x.UpliftPct).Default(0.0);
        });
    }

    private static void Payroll(ModelBuilder mb, SqlDialect d)
    {
        mb.Entity<PayRate>(e =>
        {
            e.ToTable("pay_rates", t =>
            {
                t.HasCheckConstraint("pay_rates_day_type_check", d.In("day_type", PayDayTypes.All));
                t.HasCheckConstraint("pay_rates_hour_check", "[hour] BETWEEN 0 AND 23");
            });
            e.HasKey(x => new { x.DayType, x.Hour, x.Role });
            e.Property(x => x.DayType).Text(16);
            e.Property(x => x.Role).Text(32);
            // 0020: the position catalogue is the authority for the rate grid's keys.
            e.HasOne<Position>().WithMany().HasForeignKey(x => x.Role).OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("pay_rates_role_fkey");
            e.HasIndex(x => x.Role).HasDatabaseName("pay_rates_role_idx");
        });

        mb.Entity<PaySettings>(e =>
        {
            e.ToTable("pay_settings", t => t.HasCheckConstraint("pay_settings_id_check", "[id] = 1"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).Default(true);
            e.Property(x => x.RsmAllowance).Default(9.0);
            e.Property(x => x.SuperRate).Default(0.12);
            e.Property(x => x.Overtime15Multiplier).HasColumnName("overtime_1_5_multiplier").Default(1.5);
            e.Property(x => x.Overtime19Multiplier).HasColumnName("overtime_1_9_multiplier").Default(1.9);
            e.Property(x => x.DoubleTimeMultiplier).Default(2.0);
            e.Property(x => x.SundayOtFromHour).Default(0.0);
            e.Property(x => x.SundayOtToHour).Default(9.0);
        });

        mb.Entity<PayrollTaxRate>(e =>
        {
            e.ToTable("payroll_tax_rates", t => t.HasCheckConstraint("payroll_tax_rates_state_check", d.In("state", AuStates.All.ToArray())));
            e.HasKey(x => x.State);
            e.Property(x => x.State).Text(3);
            e.Property(x => x.Rate).Default(0.0);
        });
    }

    private static void Leave(ModelBuilder mb, SqlDialect d)
    {
        mb.Entity<LeaveLiabilitySnapshot>(e =>
        {
            e.ToTable("leave_liability_snapshots", t => t.HasCheckConstraint("leave_liability_snapshots_role_class_check", d.In("role_class", RoleClasses.All)));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.UkgEmployeeId).IsRequired().Text(64);
            e.Property(x => x.UkgLocation).Text(64);
            e.Property(x => x.EmployeeName).IsRequired().Text(200);
            e.Property(x => x.Job).Text(200);
            e.Property(x => x.RoleClass).IsRequired().Text(8);
            e.Property(x => x.ContractHours).Default(0.0);
            e.Property(x => x.NotionalHours).Default(0.0);
            e.Property(x => x.SickHours).Default(0.0);
            e.Property(x => x.AnnualHours).Default(0.0);
            e.Property(x => x.LieuHours).Default(0.0);
            e.Property(x => x.BankedHours).Default(0.0);
            e.Property(x => x.Banked2Hours).HasColumnName("banked2_hours").Default(0.0);
            e.Property(x => x.CreatedAt).Now(d);
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<UploadBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.StoreId, x.PeriodEnd, x.UkgEmployeeId }).IsUnique()
                .HasDatabaseName("leave_liability_snapshots_store_id_period_end_ukg_employee_id_key");
            e.HasIndex(x => new { x.StoreId, x.PeriodEnd }).HasDatabaseName("lls_store_period_idx");
            e.HasIndex(x => x.BatchId).HasDatabaseName("lls_batch_idx");
        });

        mb.Entity<LeaveRequestUkg>(e =>
        {
            e.ToTable("leave_requests_ukg");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.UkgEmployeeId).IsRequired().Text(64);
            e.Property(x => x.EmployeeName).IsRequired().Text(200);
            e.Property(x => x.LeaveType).IsRequired().Text(200);
            e.Property(x => x.Status).IsRequired().Text(64);
            e.Property(x => x.Approver).Text(200);
            e.Property(x => x.CreatedAt).Now(d);
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<UploadBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.StoreId, x.UkgEmployeeId, x.LeaveType, x.StartDate, x.EndDate }).IsUnique()
                .HasDatabaseName("leave_requests_ukg_store_id_ukg_employee_id_leave_type_start_date_end_date_key");
            e.HasIndex(x => new { x.StoreId, x.StartDate, x.EndDate }).HasDatabaseName("lru_store_dates_idx");
            e.HasIndex(x => x.BatchId).HasDatabaseName("lru_batch_idx");
        });

        mb.Entity<LeaveAccrualBand>(e =>
        {
            e.ToTable("leave_accrual_bands", t => t.HasCheckConstraint("leave_accrual_bands_role_class_check", d.In("role_class", RoleClasses.Banded)));
            e.HasKey(x => new { x.ContractHours, x.RoleClass });
            e.Property(x => x.RoleClass).Text(8);
        });

        mb.Entity<LeaveSettings>(e =>
        {
            e.ToTable("leave_settings", t => t.HasCheckConstraint("leave_settings_id_check", "[id] = 1"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).Default(true);
            e.Property(x => x.TargetHoursPerFte).Default(76.0);
            e.Property(x => x.FteDivisor).Default(76.0);
            e.Property(x => x.UpdatedAt).Now(d, alsoOnUpdate: true);
        });
    }

    private static void Kiosk(ModelBuilder mb, SqlDialect d)
    {
        mb.Entity<LoadType>(e =>
        {
            e.ToTable("load_types", t =>
            {
                t.HasCheckConstraint("load_types_minutes_per_pallet_check", "[minutes_per_pallet] > 0");
                t.HasCheckConstraint("load_types_minutes_per_pallet_lt90_check", "[minutes_per_pallet_lt90] > 0");
                t.HasCheckConstraint("load_types_minutes_per_pallet_lt60_check", "[minutes_per_pallet_lt60] > 0");
                t.HasCheckConstraint("load_types_minutes_per_pallet_lt30_check", "[minutes_per_pallet_lt30] > 0");
                t.HasCheckConstraint("load_types_sub_task_check", $"[sub_task] IS NULL OR {d.In("sub_task", LoadSubTasks.All.ToArray())}");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.Name).IsRequired().Text(100);
            e.HasIndex(x => x.Name).IsUnique().HasDatabaseName("load_types_name_key");
            e.Property(x => x.Color).IsRequired().Text(16).Default("#2563eb");
            e.Property(x => x.SortOrder).Default(0);
            e.Property(x => x.MinutesPerPalletLt90).HasColumnName("minutes_per_pallet_lt90");
            e.Property(x => x.MinutesPerPalletLt60).HasColumnName("minutes_per_pallet_lt60");
            e.Property(x => x.MinutesPerPalletLt30).HasColumnName("minutes_per_pallet_lt30");
            e.Property(x => x.Priority).Default(100);
            e.Property(x => x.SubTask).Text(32);
        });

        mb.Entity<DailyLoad>(e =>
        {
            e.ToTable("daily_loads", t => t.HasCheckConstraint("daily_loads_qty_check", "[qty] >= 0 AND [qty] <= 99"));
            e.HasKey(x => new { x.StoreId, x.Date, x.LoadTypeId });
            e.Property(x => x.UpdatedAt).Now(d, alsoOnUpdate: true);
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<LoadType>().WithMany().HasForeignKey(x => x.LoadTypeId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.LoadTypeId).HasDatabaseName("daily_loads_type_idx");
        });

        mb.Entity<LoadAssignment>(e =>
        {
            e.ToTable("load_assignments");
            e.HasKey(x => new { x.StoreId, x.Date, x.EmployeeId });
            e.Property(x => x.EmployeeName).IsRequired().Text(200);
            e.Property(x => x.LoadType).IsRequired().Text(100);
            e.Property(x => x.Manual).Default(false);
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<StoreWalkNote>(e =>
        {
            e.ToTable("store_walk_notes", t =>
                t.HasCheckConstraint("store_walk_notes_note_check", $"{d.TrimmedLen("note")} BETWEEN 1 AND 500"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.Note).IsRequired().Text(500);
            e.Property(x => x.AssignedName).Text(200);
            e.Property(x => x.CreatedAt).Now(d);
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
            // Postgres ON DELETE SET NULL; a second path from stores here (code cascade).
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.AssignedEmployeeId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.StoreId, x.RaisedDate }).HasFilter("[completed_at] IS NULL").HasDatabaseName("swn_open_idx");
            e.HasIndex(x => x.AssignedEmployeeId).HasDatabaseName("store_walk_notes_assigned_idx");
        });

        mb.Entity<AdminTaskCheck>(e =>
        {
            e.ToTable("admin_task_checks");
            e.HasKey(x => new { x.StoreId, x.Date, x.TaskName });
            e.Property(x => x.TaskName).Text(200);
            e.Property(x => x.CheckedAt).Now(d);
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
