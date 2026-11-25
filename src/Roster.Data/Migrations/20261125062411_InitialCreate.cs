using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Roster.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    schedule_type = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    day_of_week = table.Column<int>(type: "int", nullable: true),
                    nth_week = table.Column<int>(type: "int", nullable: true),
                    region_days = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    duration_type = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    fixed_minutes = table.Column<double>(type: "float", nullable: true),
                    minutes_per_unit = table.Column<double>(type: "float", nullable: true),
                    min_minutes = table.Column<double>(type: "float", nullable: true),
                    window_start_kind = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false, defaultValue: "none"),
                    window_start_time = table.Column<TimeOnly>(type: "time(0)", nullable: true),
                    window_start_offset_min = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    window_end_kind = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false, defaultValue: "none"),
                    window_end_time = table.Column<TimeOnly>(type: "time(0)", nullable: true),
                    window_end_offset_min = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    sort_order = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    active = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("admin_tasks_pkey", x => x.id);
                    table.CheckConstraint("admin_tasks_day_of_week_check", "[day_of_week] BETWEEN 0 AND 6");
                    table.CheckConstraint("admin_tasks_duration_type_check", "[duration_type] IN (N'fixed', N'per_employee', N'per_register', N'till_calibration')");
                    table.CheckConstraint("admin_tasks_fixed_minutes_check", "[fixed_minutes] >= 0");
                    table.CheckConstraint("admin_tasks_min_minutes_check", "[min_minutes] >= 0");
                    table.CheckConstraint("admin_tasks_minutes_per_unit_check", "[minutes_per_unit] >= 0");
                    table.CheckConstraint("admin_tasks_nth_week_check", "[nth_week] BETWEEN 1 AND 5");
                    table.CheckConstraint("admin_tasks_region_days_check", "[region_days] IS NULL OR ISJSON([region_days]) = 1");
                    table.CheckConstraint("admin_tasks_schedule_type_check", "[schedule_type] IN (N'daily', N'weekly', N'monthly_nth', N'planning_week')");
                    table.CheckConstraint("admin_tasks_window_end_kind_check", "[window_end_kind] IN (N'none', N'clock', N'open', N'close')");
                    table.CheckConstraint("admin_tasks_window_start_kind_check", "[window_start_kind] IN (N'none', N'clock', N'open', N'close')");
                });

            migrationBuilder.CreateTable(
                name: "break_rules",
                columns: table => new
                {
                    role = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    min_shift_hours = table.Column<double>(type: "float", nullable: false),
                    max_shift_hours = table.Column<double>(type: "float", nullable: false),
                    gap_hours = table.Column<double>(type: "float", nullable: false),
                    break_type = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    weekly_cap = table.Column<double>(type: "float", nullable: true),
                    consec_day_cap = table.Column<int>(type: "int", nullable: true),
                    ot_1_5_after_hours = table.Column<double>(type: "float", nullable: true),
                    ot_1_9_after_hours = table.Column<double>(type: "float", nullable: true),
                    ot_period_after_hours = table.Column<double>(type: "float", nullable: true),
                    double_time_under_gap_hours = table.Column<double>(type: "float", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("break_rules_pkey", x => x.role);
                    table.CheckConstraint("break_rules_break_type_check", "[break_type] IN (N'flat_30_unpaid', N'tiered')");
                    table.CheckConstraint("break_rules_role_check", "[role] IN (N'SM', N'ASM', N'DSM', N'RSM', N'SA', N'JSA')");
                });

            migrationBuilder.CreateTable(
                name: "cron_runs",
                columns: table => new
                {
                    job = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    last_run_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()"),
                    run_count = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    last_result = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("cron_runs_pkey", x => x.job);
                    table.CheckConstraint("cron_runs_last_result_check", "[last_result] IS NULL OR ISJSON([last_result]) = 1");
                });

            migrationBuilder.CreateTable(
                name: "director_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("director_groups_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "feed_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    token = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, defaultValueSql: "LOWER(CONVERT(varchar(32), HASHBYTES('MD5', CONVERT(varchar(36), NEWID())), 2) + CONVERT(varchar(32), HASHBYTES('MD5', CONVERT(varchar(36), NEWID())), 2))"),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()"),
                    last_used_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("feed_tokens_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "labour_group_priority",
                columns: table => new
                {
                    labour_group = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    priority = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("labour_group_priority_pkey", x => x.labour_group);
                });

            migrationBuilder.CreateTable(
                name: "labour_group_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    labour_group = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    day_of_week = table.Column<int>(type: "int", nullable: false),
                    window_start = table.Column<TimeOnly>(type: "time(0)", nullable: false),
                    window_end = table.Column<TimeOnly>(type: "time(0)", nullable: false),
                    window_hours = table.Column<double>(type: "float", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("labour_group_rules_pkey", x => x.id);
                    table.CheckConstraint("labour_group_rules_day_of_week_check", "[day_of_week] BETWEEN 0 AND 6");
                    table.CheckConstraint("labour_group_rules_window_hours_check", "[window_hours] IS NULL OR [window_hours] > 0");
                });

            migrationBuilder.CreateTable(
                name: "leave_accrual_bands",
                columns: table => new
                {
                    contract_hours = table.Column<double>(type: "float", nullable: false),
                    role_class = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    leave_notional_hours = table.Column<double>(type: "float", nullable: false),
                    accrual_weeks = table.Column<double>(type: "float", nullable: false),
                    annual_accrual_hours = table.Column<double>(type: "float", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("leave_accrual_bands_pkey", x => new { x.contract_hours, x.role_class });
                    table.CheckConstraint("leave_accrual_bands_role_class_check", "[role_class] IN (N'SM', N'ASM', N'DSM', N'SA')");
                });

            migrationBuilder.CreateTable(
                name: "leave_settings",
                columns: table => new
                {
                    id = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    target_hours_per_fte = table.Column<double>(type: "float", nullable: false, defaultValue: 76.0),
                    fte_divisor = table.Column<double>(type: "float", nullable: false, defaultValue: 76.0),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("leave_settings_pkey", x => x.id);
                    table.CheckConstraint("leave_settings_id_check", "[id] = 1");
                });

            migrationBuilder.CreateTable(
                name: "load_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    minutes_per_pallet = table.Column<double>(type: "float", nullable: false),
                    color = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, defaultValue: "#2563eb"),
                    sort_order = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    minutes_per_pallet_lt90 = table.Column<double>(type: "float", nullable: true),
                    minutes_per_pallet_lt60 = table.Column<double>(type: "float", nullable: true),
                    minutes_per_pallet_lt30 = table.Column<double>(type: "float", nullable: true),
                    priority = table.Column<int>(type: "int", nullable: false, defaultValue: 100),
                    sub_task = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("load_types_pkey", x => x.id);
                    table.CheckConstraint("load_types_minutes_per_pallet_check", "[minutes_per_pallet] > 0");
                    table.CheckConstraint("load_types_minutes_per_pallet_lt30_check", "[minutes_per_pallet_lt30] > 0");
                    table.CheckConstraint("load_types_minutes_per_pallet_lt60_check", "[minutes_per_pallet_lt60] > 0");
                    table.CheckConstraint("load_types_minutes_per_pallet_lt90_check", "[minutes_per_pallet_lt90] > 0");
                    table.CheckConstraint("load_types_sub_task_check", "[sub_task] IS NULL OR [sub_task] IN (N'produce', N'long_life', N'freezer', N'ambient', N'bread', N'short_life', N'freeze_to_thaw', N'ambient_last')");
                });

            migrationBuilder.CreateTable(
                name: "non_op_categories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    sort = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("non_op_categories_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pay_settings",
                columns: table => new
                {
                    id = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    rsm_allowance = table.Column<double>(type: "float", nullable: false, defaultValue: 9.0),
                    super_rate = table.Column<double>(type: "float", nullable: false, defaultValue: 0.12),
                    overtime_1_5_multiplier = table.Column<double>(type: "float", nullable: false, defaultValue: 1.5),
                    overtime_1_9_multiplier = table.Column<double>(type: "float", nullable: false, defaultValue: 1.8999999999999999),
                    double_time_multiplier = table.Column<double>(type: "float", nullable: false, defaultValue: 2.0),
                    sunday_ot_from_hour = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    sunday_ot_to_hour = table.Column<double>(type: "float", nullable: false, defaultValue: 9.0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pay_settings_pkey", x => x.id);
                    table.CheckConstraint("pay_settings_id_check", "[id] = 1");
                });

            migrationBuilder.CreateTable(
                name: "payroll_tax_rates",
                columns: table => new
                {
                    state = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    rate = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("payroll_tax_rates_pkey", x => x.state);
                    table.CheckConstraint("payroll_tax_rates_state_check", "[state] IN (N'NSW', N'VIC', N'QLD', N'SA', N'WA', N'TAS', N'NT', N'ACT')");
                });

            migrationBuilder.CreateTable(
                name: "plan_placement_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    position = table.Column<int>(type: "int", nullable: false),
                    labour_group = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    strategy = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    anchor_kind = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false, defaultValue: "none"),
                    anchor_time = table.Column<TimeOnly>(type: "time(0)", nullable: true),
                    anchor_offset_min = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    pre_peak_pct = table.Column<double>(type: "float", nullable: true),
                    post_peak_pct = table.Column<double>(type: "float", nullable: true),
                    avoid_strength = table.Column<double>(type: "float", nullable: true),
                    night_trader_strategy = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("plan_placement_rules_pkey", x => x.id);
                    table.CheckConstraint("plan_placement_rules_anchor_kind_check", "[anchor_kind] IN (N'none', N'clock', N'open', N'close')");
                    table.CheckConstraint("plan_placement_rules_avoid_strength_check", "[avoid_strength] BETWEEN 0 AND 100");
                    table.CheckConstraint("plan_placement_rules_labour_group_check", "[labour_group] IN (N'checkout', N'load_running', N'reset', N'admin', N'specials', N'cleaning')");
                    table.CheckConstraint("plan_placement_rules_night_trader_strategy_check", "[night_trader_strategy] IN (N'spread', N'follow_demand', N'backfill', N'forward_fill', N'peak_split', N'avoid_peak')");
                    table.CheckConstraint("plan_placement_rules_post_peak_pct_check", "[post_peak_pct] BETWEEN 0 AND 100");
                    table.CheckConstraint("plan_placement_rules_pre_peak_pct_check", "[pre_peak_pct] BETWEEN 0 AND 100");
                    table.CheckConstraint("plan_placement_rules_strategy_check", "[strategy] IN (N'spread', N'follow_demand', N'backfill', N'forward_fill', N'peak_split', N'avoid_peak')");
                });

            migrationBuilder.CreateTable(
                name: "plan_placement_settings",
                columns: table => new
                {
                    id = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    enabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    peak_basis = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, defaultValue: "hourly"),
                    peak_smoothing_blocks = table.Column<int>(type: "int", nullable: false, defaultValue: 3),
                    peak_threshold_pct = table.Column<double>(type: "float", nullable: false, defaultValue: 85.0),
                    night_trader_from = table.Column<TimeOnly>(type: "time(0)", nullable: false, defaultValue: new TimeOnly(17, 0, 0)),
                    night_trader_share_pct = table.Column<double>(type: "float", nullable: false, defaultValue: 25.0),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()"),
                    step_order = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValueSql: "N'[\"admin\",\"load_running\",\"reset\",\"specials\",\"cleaning\"]'")
                },
                constraints: table =>
                {
                    table.PrimaryKey("plan_placement_settings_pkey", x => x.id);
                    table.CheckConstraint("plan_placement_settings_id_check", "[id] = 1");
                    table.CheckConstraint("plan_placement_settings_night_trader_share_pct_check", "[night_trader_share_pct] BETWEEN 0 AND 100");
                    table.CheckConstraint("plan_placement_settings_peak_basis_check", "[peak_basis] IN (N'hourly', N'rolling')");
                    table.CheckConstraint("plan_placement_settings_peak_smoothing_blocks_check", "[peak_smoothing_blocks] BETWEEN 1 AND 16");
                    table.CheckConstraint("plan_placement_settings_peak_threshold_pct_check", "[peak_threshold_pct] BETWEEN 1 AND 100");
                    table.CheckConstraint("plan_placement_settings_step_order_check", "[step_order] IS NULL OR ISJSON([step_order]) = 1");
                });

            migrationBuilder.CreateTable(
                name: "positions",
                columns: table => new
                {
                    code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    base_role = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    weekly_hours = table.Column<double>(type: "float", nullable: true),
                    year_band = table.Column<int>(type: "int", nullable: true),
                    has_rate = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    sort_order = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    active = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("positions_pkey", x => x.code);
                    table.CheckConstraint("positions_base_role_check", "[base_role] IN (N'SM', N'ASM', N'DSM', N'RSM', N'SA', N'JSA')");
                    table.CheckConstraint("positions_weekly_hours_check", "[weekly_hours] >= 0");
                    table.CheckConstraint("positions_year_band_check", "[year_band] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "probation_buffers",
                columns: table => new
                {
                    labour_group = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, defaultValue: "*"),
                    max_days = table.Column<int>(type: "int", nullable: false),
                    uplift_pct = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("probation_buffers_pkey", x => new { x.labour_group, x.max_days });
                    table.CheckConstraint("probation_buffers_max_days_check", "[max_days] > 0");
                    table.CheckConstraint("probation_buffers_uplift_pct_check", "[uplift_pct] >= 0 AND [uplift_pct] <= 300");
                });

            migrationBuilder.CreateTable(
                name: "public_holidays_national",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("public_holidays_national_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "public_holidays_state",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    state = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("public_holidays_state_pkey", x => x.id);
                    table.CheckConstraint("public_holidays_state_state_check", "[state] IN (N'NSW', N'VIC', N'QLD', N'SA', N'WA', N'TAS', N'NT', N'ACT')");
                });

            migrationBuilder.CreateTable(
                name: "reset_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    share_pct = table.Column<double>(type: "float", nullable: true),
                    fixed_minutes = table.Column<double>(type: "float", nullable: true),
                    window_start_kind = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false, defaultValue: "none"),
                    window_start_time = table.Column<TimeOnly>(type: "time(0)", nullable: true),
                    window_start_offset_min = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    window_end_kind = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false, defaultValue: "none"),
                    window_end_time = table.Column<TimeOnly>(type: "time(0)", nullable: true),
                    window_end_offset_min = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    sort_order = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    active = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    placement = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, defaultValue: "spread"),
                    sub_task = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("reset_tasks_pkey", x => x.id);
                    table.CheckConstraint("reset_tasks_fixed_minutes_check", "[fixed_minutes] > 0");
                    table.CheckConstraint("reset_tasks_one_sizing", "[share_pct] IS NULL OR [fixed_minutes] IS NULL");
                    table.CheckConstraint("reset_tasks_placement_check", "[placement] IN (N'spread', N'avoid_peak', N'follow_demand', N'late', N'early')");
                    table.CheckConstraint("reset_tasks_share_pct_check", "[share_pct] > 0 AND [share_pct] <= 100");
                    table.CheckConstraint("reset_tasks_sub_task_check", "[sub_task] IS NULL OR [sub_task] IN (N're_runs', N'decarding')");
                    table.CheckConstraint("reset_tasks_window_end_kind_check", "[window_end_kind] IN (N'none', N'clock', N'open', N'close')");
                    table.CheckConstraint("reset_tasks_window_start_kind_check", "[window_start_kind] IN (N'none', N'clock', N'open', N'close')");
                });

            migrationBuilder.CreateTable(
                name: "roster_order_steps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    position = table.Column<int>(type: "int", nullable: false),
                    step_type = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    day_order = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("roster_order_steps_pkey", x => x.id);
                    table.CheckConstraint("roster_order_steps_day_order_check", "[day_order] IS NULL OR ISJSON([day_order]) = 1");
                    table.CheckConstraint("roster_order_steps_step_type_check", "[step_type] IN (N'ALLOCATE_SM', N'ALLOCATE_ASM', N'ALLOCATE_DSM', N'FILL_FLOOR', N'SHUFFLE_JSA', N'CONTRACT_CHECK')");
                });

            migrationBuilder.CreateTable(
                name: "roster_periods",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("roster_periods_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rostering_rules",
                columns: table => new
                {
                    key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    value = table.Column<double>(type: "float", nullable: false),
                    note = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("rostering_rules_pkey", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "school_term_exceptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    state = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    note = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("school_term_exceptions_pkey", x => x.id);
                    table.CheckConstraint("school_term_exceptions_state_check", "[state] IN (N'NSW', N'VIC', N'QLD', N'SA', N'WA', N'TAS', N'NT', N'ACT')");
                });

            migrationBuilder.CreateTable(
                name: "school_terms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    state = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    term_label = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("school_terms_pkey", x => x.id);
                    table.CheckConstraint("school_terms_state_check", "[state] IN (N'NSW', N'VIC', N'QLD', N'SA', N'WA', N'TAS', N'NT', N'ACT')");
                });

            migrationBuilder.CreateTable(
                name: "tab_visibility",
                columns: table => new
                {
                    level = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    tab_key = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    visible = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("tab_visibility_pkey", x => new { x.level, x.tab_key });
                });

            migrationBuilder.CreateTable(
                name: "variance_settings",
                columns: table => new
                {
                    id = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    data_light_pct = table.Column<double>(type: "float", nullable: false, defaultValue: 15.0),
                    metric_pct = table.Column<double>(type: "float", nullable: false, defaultValue: 70.0),
                    hours_pct = table.Column<double>(type: "float", nullable: false, defaultValue: 20.0),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("variance_settings_pkey", x => x.id);
                    table.CheckConstraint("variance_settings_data_light_pct_check", "[data_light_pct] BETWEEN 0 AND 100");
                    table.CheckConstraint("variance_settings_hours_pct_check", "[hours_pct] BETWEEN 0 AND 500");
                    table.CheckConstraint("variance_settings_id_check", "[id] = 1");
                    table.CheckConstraint("variance_settings_metric_pct_check", "[metric_pct] BETWEEN 0 AND 500");
                });

            migrationBuilder.CreateTable(
                name: "regions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    director_group_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("regions_pkey", x => x.id);
                    table.ForeignKey(
                        name: "regions_director_group_id_fkey",
                        column: x => x.director_group_id,
                        principalTable: "director_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "non_op_subcategories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    category_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    min_minutes = table.Column<int>(type: "int", nullable: true),
                    max_minutes = table.Column<int>(type: "int", nullable: true),
                    days_allowed = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValueSql: "N'[0,1,2,3,4,5,6]'"),
                    default_minutes = table.Column<int>(type: "int", nullable: true),
                    note = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("non_op_subcategories_pkey", x => x.id);
                    table.CheckConstraint("non_op_subcategories_days_allowed_check", "[days_allowed] IS NULL OR ISJSON([days_allowed]) = 1");
                    table.ForeignKey(
                        name: "non_op_subcategories_category_id_fkey",
                        column: x => x.category_id,
                        principalTable: "non_op_categories",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "pay_rates",
                columns: table => new
                {
                    day_type = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    hour = table.Column<int>(type: "int", nullable: false),
                    role = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    rate = table.Column<double>(type: "float", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pay_rates_pkey", x => new { x.day_type, x.hour, x.role });
                    table.CheckConstraint("pay_rates_day_type_check", "[day_type] IN (N'weekday', N'saturday', N'sunday', N'public_holiday')");
                    table.CheckConstraint("pay_rates_hour_check", "[hour] BETWEEN 0 AND 23");
                    table.ForeignKey(
                        name: "pay_rates_role_fkey",
                        column: x => x.role,
                        principalTable: "positions",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "areas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    region_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("areas_pkey", x => x.id);
                    table.ForeignKey(
                        name: "areas_region_id_fkey",
                        column: x => x.region_id,
                        principalTable: "regions",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "stores",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    code = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    region_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    state = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()"),
                    kiosk_token = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, defaultValueSql: "LOWER(CONVERT(varchar(32), HASHBYTES('MD5', CONVERT(varchar(36), NEWID())), 2))"),
                    area_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    dso_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("stores_pkey", x => x.id);
                    table.CheckConstraint("stores_state_check", "[state] IN (N'NSW', N'VIC', N'QLD', N'SA', N'WA', N'TAS', N'NT', N'ACT')");
                    table.ForeignKey(
                        name: "stores_area_id_fkey",
                        column: x => x.area_id,
                        principalTable: "areas",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "stores_region_id_fkey",
                        column: x => x.region_id,
                        principalTable: "regions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "admin_task_checks",
                columns: table => new
                {
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    task_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    checked_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("admin_task_checks_pkey", x => new { x.store_id, x.date, x.task_name });
                    table.ForeignKey(
                        name: "admin_task_checks_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "app_users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    username = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    tier = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    region_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    director_group_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    area_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()"),
                    entra_object_id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    user_principal_name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    windows_account = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    display_name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    last_sign_in_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("app_users_pkey", x => x.id);
                    table.CheckConstraint("app_users_tier_check", "[tier] IN (N'super_user', N'store', N'pa', N'director', N'emso')");
                    table.CheckConstraint("tier_scope", "([tier] = 'super_user' AND [store_id] IS NULL AND [region_id] IS NULL AND [director_group_id] IS NULL) OR ([tier] = 'store' AND [store_id] IS NOT NULL) OR ([tier] = 'pa') OR ([tier] = 'director') OR ([tier] = 'emso' AND [area_id] IS NOT NULL AND [store_id] IS NULL AND [director_group_id] IS NULL)");
                    table.ForeignKey(
                        name: "app_users_area_id_fkey",
                        column: x => x.area_id,
                        principalTable: "areas",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "app_users_director_group_id_fkey",
                        column: x => x.director_group_id,
                        principalTable: "director_groups",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "app_users_region_id_fkey",
                        column: x => x.region_id,
                        principalTable: "regions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "app_users_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "daily_loads",
                columns: table => new
                {
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    load_type_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    qty = table.Column<int>(type: "int", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("daily_loads_pkey", x => new { x.store_id, x.date, x.load_type_id });
                    table.CheckConstraint("daily_loads_qty_check", "[qty] >= 0 AND [qty] <= 99");
                    table.ForeignKey(
                        name: "daily_loads_load_type_id_fkey",
                        column: x => x.load_type_id,
                        principalTable: "load_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "daily_loads_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "day_plans",
                columns: table => new
                {
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    plan_status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, defaultValue: "none"),
                    roster_status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, defaultValue: "none"),
                    plan_generated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    roster_generated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ph_load_reallocated = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("day_plans_pkey", x => new { x.store_id, x.date });
                    table.CheckConstraint("day_plans_plan_status_check", "[plan_status] IN (N'none', N'generated', N'approved')");
                    table.CheckConstraint("day_plans_roster_status_check", "[roster_status] IN (N'none', N'generated', N'approved')");
                    table.ForeignKey(
                        name: "day_plans_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "employees",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    role = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    contract_hours = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    sm_year_band = table.Column<int>(type: "int", nullable: true),
                    notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    active = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()"),
                    notional_hours = table.Column<double>(type: "float", nullable: true),
                    position_code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    ukg_id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("employees_pkey", x => x.id);
                    table.CheckConstraint("employees_notional_hours_check", "[notional_hours] IS NULL OR ([notional_hours] >= 0 AND [notional_hours] <= 24)");
                    table.CheckConstraint("employees_role_check", "[role] IN (N'SM', N'ASM', N'DSM', N'RSM', N'SA', N'JSA')");
                    table.ForeignKey(
                        name: "employees_position_code_fkey",
                        column: x => x.position_code,
                        principalTable: "positions",
                        principalColumn: "code",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "employees_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "load_assignments",
                columns: table => new
                {
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    employee_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    employee_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    load_type = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    manual = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("load_assignments_pkey", x => new { x.store_id, x.date, x.employee_id });
                    table.ForeignKey(
                        name: "load_assignments_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "planned_blocks",
                columns: table => new
                {
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    labour_group = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    block_time = table.Column<TimeOnly>(type: "time(0)", nullable: false),
                    headcount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("planned_blocks_pkey", x => new { x.store_id, x.date, x.labour_group, x.block_time });
                    table.CheckConstraint("planned_blocks_headcount_check", "[headcount] >= 0");
                    table.ForeignKey(
                        name: "planned_blocks_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "public_holidays_local",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("public_holidays_local_pkey", x => x.id);
                    table.ForeignKey(
                        name: "public_holidays_local_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sales_overrides",
                columns: table => new
                {
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    closed = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    method = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    pct = table.Column<double>(type: "float", nullable: true),
                    value = table.Column<double>(type: "float", nullable: true),
                    reason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    notes = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("sales_overrides_pkey", x => new { x.store_id, x.date });
                    table.ForeignKey(
                        name: "sales_overrides_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "special_trading_hours",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    closed = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    open_time = table.Column<TimeOnly>(type: "time(0)", nullable: true),
                    close_time = table.Column<TimeOnly>(type: "time(0)", nullable: true),
                    note = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("special_trading_hours_pkey", x => x.id);
                    table.ForeignKey(
                        name: "special_trading_hours_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "store_attributes",
                columns: table => new
                {
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    has_sco = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    is_new_sco_model = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    mco_registers = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    sco_lanes = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    sco_efficiency = table.Column<double>(type: "float", nullable: false, defaultValue: 0.45000000000000001),
                    first_step = table.Column<double>(type: "float", nullable: false, defaultValue: 1.2),
                    sco_share = table.Column<double>(type: "float", nullable: false, defaultValue: 0.66000000000000003),
                    oe_target_pct = table.Column<double>(type: "float", nullable: false, defaultValue: 90.0),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()"),
                    max_staff_on_leave = table.Column<int>(type: "int", nullable: false, defaultValue: 3),
                    night_fill = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    opening_date = table.Column<DateOnly>(type: "date", nullable: true),
                    pair_store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("store_attributes_pkey", x => x.store_id);
                    table.CheckConstraint("store_attributes_max_staff_on_leave_check", "[max_staff_on_leave] >= 0");
                    table.CheckConstraint("store_attributes_sco_share_check", "[sco_share] BETWEEN 0 AND 0.90");
                    table.ForeignKey(
                        name: "store_attributes_pair_store_id_fkey",
                        column: x => x.pair_store_id,
                        principalTable: "stores",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "store_attributes_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "store_trading_hours",
                columns: table => new
                {
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    day_of_week = table.Column<int>(type: "int", nullable: false),
                    open_time = table.Column<TimeOnly>(type: "time(0)", nullable: true),
                    close_time = table.Column<TimeOnly>(type: "time(0)", nullable: true),
                    closed = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("store_trading_hours_pkey", x => new { x.store_id, x.day_of_week });
                    table.CheckConstraint("store_trading_hours_day_of_week_check", "[day_of_week] BETWEEN 0 AND 6");
                    table.ForeignKey(
                        name: "store_trading_hours_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    link = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    read_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("notifications_pkey", x => x.id);
                    table.ForeignKey(
                        name: "notifications_user_id_fkey",
                        column: x => x.user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plan_snapshots",
                columns: table => new
                {
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    generated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()"),
                    generated_by = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    version = table.Column<int>(type: "int", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("plan_snapshots_pkey", x => new { x.store_id, x.period_start, x.kind });
                    table.CheckConstraint("plan_snapshots_kind_check", "[kind] IN (N'plan', N'roster')");
                    table.CheckConstraint("plan_snapshots_payload_check", "[payload] IS NULL OR ISJSON([payload]) = 1");
                    table.ForeignKey(
                        name: "plan_snapshots_generated_by_fkey",
                        column: x => x.generated_by,
                        principalTable: "app_users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "plan_snapshots_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "roster_audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: true),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    detail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("roster_audit_pkey", x => x.id);
                    table.CheckConstraint("roster_audit_detail_check", "[detail] IS NULL OR ISJSON([detail]) = 1");
                    table.ForeignKey(
                        name: "roster_audit_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "roster_audit_user_id_fkey",
                        column: x => x.user_id,
                        principalTable: "app_users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "roster_review_checks",
                columns: table => new
                {
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    check_key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    checked_by = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    checked_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("roster_review_checks_pkey", x => new { x.store_id, x.period_start, x.check_key });
                    table.CheckConstraint("roster_review_checks_note_check", "[note] IS NULL OR LEN([note]) <= 1000");
                    table.ForeignKey(
                        name: "roster_review_checks_checked_by_fkey",
                        column: x => x.checked_by,
                        principalTable: "app_users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "roster_review_checks_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "roster_review_days",
                columns: table => new
                {
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    verdict = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true),
                    query = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    reply = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    replied_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    prev_query = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    prev_reply = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    decided_by = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("roster_review_days_pkey", x => new { x.store_id, x.period_start, x.date });
                    table.CheckConstraint("roster_review_days_query_check", "[query] IS NULL OR LEN([query]) <= 1000");
                    table.CheckConstraint("roster_review_days_reply_check", "[reply] IS NULL OR LEN([reply]) <= 1000");
                    table.CheckConstraint("roster_review_days_verdict_check", "[verdict] IN (N'ok', N'query')");
                    table.ForeignKey(
                        name: "roster_review_days_decided_by_fkey",
                        column: x => x.decided_by,
                        principalTable: "app_users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "roster_review_days_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "roster_review_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    actor_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    actor_name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    actor_tier = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    body = table.Column<string>(type: "nvarchar(3000)", maxLength: 3000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("roster_review_events_pkey", x => x.id);
                    table.CheckConstraint("roster_review_events_body_check", "LEN([body]) <= 3000");
                    table.ForeignKey(
                        name: "roster_review_events_actor_id_fkey",
                        column: x => x.actor_id,
                        principalTable: "app_users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "roster_review_events_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "roster_reviews",
                columns: table => new
                {
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, defaultValue: "in_progress"),
                    snapshot_key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    submitted_by = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    decided_by = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    return_message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("roster_reviews_pkey", x => new { x.store_id, x.period_start });
                    table.CheckConstraint("roster_reviews_return_message_check", "[return_message] IS NULL OR LEN([return_message]) <= 2000");
                    table.CheckConstraint("roster_reviews_status_check", "[status] IN (N'in_progress', N'submitted', N'returned', N'approved')");
                    table.ForeignKey(
                        name: "roster_reviews_decided_by_fkey",
                        column: x => x.decided_by,
                        principalTable: "app_users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "roster_reviews_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "roster_reviews_submitted_by_fkey",
                        column: x => x.submitted_by,
                        principalTable: "app_users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "upload_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    type = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    filename = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    uploaded_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()"),
                    roster_period_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    total_rows = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    accepted_rows = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    rejected_rows = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    error_report = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValueSql: "N'[]'")
                },
                constraints: table =>
                {
                    table.PrimaryKey("upload_batches_pkey", x => x.id);
                    table.CheckConstraint("upload_batches_error_report_check", "[error_report] IS NULL OR ISJSON([error_report]) = 1");
                    table.CheckConstraint("upload_batches_type_check", "[type] IN (N'benchmark', N'actuals', N'quarter_hour', N'leave_liability', N'leave_requests', N'contacts', N'pos_quarter_hour')");
                    table.ForeignKey(
                        name: "upload_batches_roster_period_id_fkey",
                        column: x => x.roster_period_id,
                        principalTable: "roster_periods",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "upload_batches_uploaded_by_fkey",
                        column: x => x.uploaded_by,
                        principalTable: "app_users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "day_off_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    employee_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()"),
                    notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, defaultValue: "pending")
                },
                constraints: table =>
                {
                    table.PrimaryKey("day_off_requests_pkey", x => x.id);
                    table.CheckConstraint("day_off_requests_status_check", "[status] IN (N'pending', N'granted', N'declined')");
                    table.ForeignKey(
                        name: "day_off_requests_employee_id_fkey",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "employee_availability",
                columns: table => new
                {
                    employee_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    day_of_week = table.Column<int>(type: "int", nullable: false),
                    available = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    from_time = table.Column<TimeOnly>(type: "time(0)", nullable: true),
                    to_time = table.Column<TimeOnly>(type: "time(0)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("employee_availability_pkey", x => new { x.employee_id, x.day_of_week });
                    table.CheckConstraint("employee_availability_day_of_week_check", "[day_of_week] BETWEEN 0 AND 6");
                    table.ForeignKey(
                        name: "employee_availability_employee_id_fkey",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "employee_leave",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    employee_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    leave_type = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false, defaultValue: "ANN"),
                    day_hours = table.Column<double>(type: "float", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("employee_leave_pkey", x => x.id);
                    table.CheckConstraint("employee_leave_day_hours_check", "[day_hours] IS NULL OR ([day_hours] > 0 AND [day_hours] <= 24)");
                    table.CheckConstraint("employee_leave_type_check", "[leave_type] IN (N'ANN', N'LS', N'BRV', N'SCK', N'CAR', N'UNP')");
                    table.ForeignKey(
                        name: "employee_leave_employee_id_fkey",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "employee_restricted_tasks",
                columns: table => new
                {
                    employee_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    labour_group = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("employee_restricted_tasks_pkey", x => new { x.employee_id, x.labour_group });
                    table.ForeignKey(
                        name: "employee_restricted_tasks_employee_id_fkey",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "fixed_roster_cycles",
                columns: table => new
                {
                    employee_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    cycle_weeks = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("fixed_roster_cycles_pkey", x => x.employee_id);
                    table.CheckConstraint("fixed_roster_cycles_cycle_weeks_check", "[cycle_weeks] IN (1, 2, 4)");
                    table.CheckConstraint("fixed_roster_cycles_start_date_check", "DATEDIFF(DAY, CONVERT(date, '19000101', 112), [start_date]) % 7 = 0");
                    table.ForeignKey(
                        name: "fixed_roster_cycles_employee_id_fkey",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "fixed_rosters",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    employee_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    day_of_week = table.Column<int>(type: "int", nullable: false),
                    start_time = table.Column<TimeOnly>(type: "time(0)", nullable: false),
                    end_time = table.Column<TimeOnly>(type: "time(0)", nullable: false),
                    week_index = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("fixed_rosters_pkey", x => x.id);
                    table.CheckConstraint("fixed_rosters_day_of_week_check", "[day_of_week] BETWEEN 0 AND 6");
                    table.CheckConstraint("fixed_rosters_week_index_check", "[week_index] >= 0 AND [week_index] <= 3");
                    table.ForeignKey(
                        name: "fixed_rosters_employee_id_fkey",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "generated_shifts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    employee_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    employee_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    role = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    start_time = table.Column<TimeOnly>(type: "time(0)", nullable: false),
                    end_time = table.Column<TimeOnly>(type: "time(0)", nullable: false),
                    break_minutes = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    locked = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    locked_by = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    locked_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("generated_shifts_pkey", x => x.id);
                    table.CheckConstraint("generated_shifts_break_minutes_check", "[break_minutes] >= 0");
                    table.CheckConstraint("generated_shifts_role_check", "[role] IN (N'SM', N'ASM', N'DSM', N'RSM', N'SA', N'JSA')");
                    table.ForeignKey(
                        name: "generated_shifts_employee_id_fkey",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "generated_shifts_locked_by_fkey",
                        column: x => x.locked_by,
                        principalTable: "app_users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "generated_shifts_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "non_op_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    subcategory_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    employee_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    minutes = table.Column<int>(type: "int", nullable: false),
                    description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("non_op_entries_pkey", x => x.id);
                    table.CheckConstraint("non_op_entries_minutes_check", "[minutes] >= 0");
                    table.ForeignKey(
                        name: "non_op_entries_employee_id_fkey",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "non_op_entries_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "non_op_entries_subcategory_id_fkey",
                        column: x => x.subcategory_id,
                        principalTable: "non_op_subcategories",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "store_walk_notes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    assigned_employee_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    assigned_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    raised_date = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()"),
                    completed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("store_walk_notes_pkey", x => x.id);
                    table.CheckConstraint("store_walk_notes_note_check", "LEN(LTRIM(RTRIM([note]))) BETWEEN 1 AND 500");
                    table.ForeignKey(
                        name: "store_walk_notes_assigned_employee_id_fkey",
                        column: x => x.assigned_employee_id,
                        principalTable: "employees",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "store_walk_notes_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "weekend_markers",
                columns: table => new
                {
                    employee_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    weekend_start = table.Column<DateOnly>(type: "date", nullable: false),
                    sat_off = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    sun_off = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("weekend_markers_pkey", x => new { x.employee_id, x.weekend_start });
                    table.ForeignKey(
                        name: "weekend_markers_employee_id_fkey",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "actuals_days",
                columns: table => new
                {
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    actual_sales = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    actual_items = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    actual_hours_100 = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    ukg_actual_hours = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    lr_hours = table.Column<double>(type: "float", nullable: true),
                    cleaning_hours = table.Column<double>(type: "float", nullable: true),
                    specials_hours = table.Column<double>(type: "float", nullable: true),
                    reset_hours = table.Column<double>(type: "float", nullable: true),
                    admin_hours = table.Column<double>(type: "float", nullable: true),
                    checkout_hours = table.Column<double>(type: "float", nullable: true),
                    is_mhs = table.Column<bool>(type: "bit", nullable: true),
                    has_esl = table.Column<bool>(type: "bit", nullable: true),
                    has_baler = table.Column<bool>(type: "bit", nullable: true),
                    actual_customers = table.Column<double>(type: "float", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("actuals_days_pkey", x => new { x.store_id, x.date });
                    table.ForeignKey(
                        name: "actuals_days_batch_id_fkey",
                        column: x => x.batch_id,
                        principalTable: "upload_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "actuals_days_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "benchmark_days",
                columns: table => new
                {
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    is_sco = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    sales_forecast = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    items = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    customer_count = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    lr_hours_100 = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    cleaning_hours_100 = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    specials_hours_100 = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    reset_hours_100 = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    admin_hours_100 = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    registers_hours_100 = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    sco_hours_100 = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    checkout_hours_100 = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    lr_hours_90 = table.Column<double>(type: "float", nullable: false, computedColumnSql: "[lr_hours_100] / 0.9", stored: true),
                    cleaning_hours_90 = table.Column<double>(type: "float", nullable: false, computedColumnSql: "[cleaning_hours_100] / 0.9", stored: true),
                    specials_hours_90 = table.Column<double>(type: "float", nullable: false, computedColumnSql: "[specials_hours_100] / 0.9", stored: true),
                    reset_hours_90 = table.Column<double>(type: "float", nullable: false, computedColumnSql: "[reset_hours_100] / 0.9", stored: true),
                    admin_hours_90 = table.Column<double>(type: "float", nullable: false, computedColumnSql: "[admin_hours_100] / 0.9", stored: true),
                    registers_hours_90 = table.Column<double>(type: "float", nullable: false, computedColumnSql: "[registers_hours_100] / 0.9", stored: true),
                    sco_hours_90 = table.Column<double>(type: "float", nullable: false, computedColumnSql: "[sco_hours_100] / 0.9", stored: true),
                    checkout_hours_90 = table.Column<double>(type: "float", nullable: false, computedColumnSql: "[checkout_hours_100] / 0.9", stored: true),
                    employee_count = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    is_mhs = table.Column<bool>(type: "bit", nullable: true),
                    has_esl = table.Column<bool>(type: "bit", nullable: true),
                    has_baler = table.Column<bool>(type: "bit", nullable: true),
                    roster_period_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("benchmark_days_pkey", x => new { x.store_id, x.date });
                    table.ForeignKey(
                        name: "benchmark_days_batch_id_fkey",
                        column: x => x.batch_id,
                        principalTable: "upload_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "benchmark_days_roster_period_id_fkey",
                        column: x => x.roster_period_id,
                        principalTable: "roster_periods",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "benchmark_days_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "leave_liability_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    period_end = table.Column<DateOnly>(type: "date", nullable: false),
                    ukg_employee_id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ukg_location = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    employee_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    job = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    role_class = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    contract_hours = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    notional_hours = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    sick_hours = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    annual_hours = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    lieu_hours = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    banked_hours = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    banked2_hours = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("leave_liability_snapshots_pkey", x => x.id);
                    table.CheckConstraint("leave_liability_snapshots_role_class_check", "[role_class] IN (N'SM', N'ASM', N'DSM', N'SA', N'CASUAL')");
                    table.ForeignKey(
                        name: "leave_liability_snapshots_batch_id_fkey",
                        column: x => x.batch_id,
                        principalTable: "upload_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "leave_liability_snapshots_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "leave_requests_ukg",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ukg_employee_id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    employee_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    leave_type = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    approver = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("leave_requests_ukg_pkey", x => x.id);
                    table.ForeignKey(
                        name: "leave_requests_ukg_batch_id_fkey",
                        column: x => x.batch_id,
                        principalTable: "upload_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "leave_requests_ukg_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pos_interval_stats",
                columns: table => new
                {
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    block_time = table.Column<TimeOnly>(type: "time(0)", nullable: false),
                    mco_customers = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    sco_customers = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    mco_items = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    sco_items = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pos_interval_stats_pkey", x => new { x.store_id, x.date, x.block_time });
                    table.CheckConstraint("pos_interval_stats_mco_customers_check", "[mco_customers] >= 0");
                    table.CheckConstraint("pos_interval_stats_mco_items_check", "[mco_items] >= 0");
                    table.CheckConstraint("pos_interval_stats_sco_customers_check", "[sco_customers] >= 0");
                    table.CheckConstraint("pos_interval_stats_sco_items_check", "[sco_items] >= 0");
                    table.ForeignKey(
                        name: "pos_interval_stats_batch_id_fkey",
                        column: x => x.batch_id,
                        principalTable: "upload_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "pos_interval_stats_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "quarter_hour_allocation",
                columns: table => new
                {
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    day_of_week = table.Column<int>(type: "int", nullable: false),
                    block_time = table.Column<TimeOnly>(type: "time(0)", nullable: false),
                    allocation = table.Column<double>(type: "float", nullable: false),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("quarter_hour_allocation_pkey", x => new { x.store_id, x.day_of_week, x.block_time });
                    table.CheckConstraint("quarter_hour_allocation_day_of_week_check", "[day_of_week] BETWEEN 0 AND 6");
                    table.ForeignKey(
                        name: "quarter_hour_allocation_batch_id_fkey",
                        column: x => x.batch_id,
                        principalTable: "upload_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "quarter_hour_allocation_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "quarter_hour_segments",
                columns: table => new
                {
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    block_time = table.Column<TimeOnly>(type: "time(0)", nullable: false),
                    segment = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    roster_period_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("quarter_hour_segments_pkey", x => new { x.store_id, x.date, x.block_time });
                    table.CheckConstraint("quarter_hour_segments_block_time_check", "DATEPART(MINUTE, [block_time]) % 15 = 0 AND DATEPART(SECOND, [block_time]) = 0");
                    table.CheckConstraint("quarter_hour_segments_segment_check", "[segment] >= 0");
                    table.ForeignKey(
                        name: "quarter_hour_segments_batch_id_fkey",
                        column: x => x.batch_id,
                        principalTable: "upload_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "quarter_hour_segments_roster_period_id_fkey",
                        column: x => x.roster_period_id,
                        principalTable: "roster_periods",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "quarter_hour_segments_store_id_fkey",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "shift_task_blocks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    shift_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    block_time = table.Column<TimeOnly>(type: "time(0)", nullable: false),
                    labour_group = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    sub_task = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("shift_task_blocks_pkey", x => x.id);
                    table.ForeignKey(
                        name: "shift_task_blocks_shift_id_fkey",
                        column: x => x.shift_id,
                        principalTable: "generated_shifts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "actuals_days_batch_idx",
                table: "actuals_days",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "actuals_days_date_idx",
                table: "actuals_days",
                column: "date");

            migrationBuilder.CreateIndex(
                name: "admin_tasks_name_key",
                table: "admin_tasks",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "app_users_area_idx",
                table: "app_users",
                column: "area_id");

            migrationBuilder.CreateIndex(
                name: "app_users_dg_idx",
                table: "app_users",
                column: "director_group_id");

            migrationBuilder.CreateIndex(
                name: "app_users_entra_object_id_key",
                table: "app_users",
                column: "entra_object_id",
                unique: true,
                filter: "[entra_object_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "app_users_region_idx",
                table: "app_users",
                column: "region_id");

            migrationBuilder.CreateIndex(
                name: "app_users_store_idx",
                table: "app_users",
                column: "store_id");

            migrationBuilder.CreateIndex(
                name: "app_users_user_principal_name_key",
                table: "app_users",
                column: "user_principal_name",
                unique: true,
                filter: "[user_principal_name] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "app_users_username_key",
                table: "app_users",
                column: "username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "app_users_windows_account_key",
                table: "app_users",
                column: "windows_account",
                unique: true,
                filter: "[windows_account] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "areas_name_key",
                table: "areas",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "areas_region_idx",
                table: "areas",
                column: "region_id");

            migrationBuilder.CreateIndex(
                name: "benchmark_days_batch_idx",
                table: "benchmark_days",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "benchmark_days_date_idx",
                table: "benchmark_days",
                column: "date");

            migrationBuilder.CreateIndex(
                name: "benchmark_days_period_idx",
                table: "benchmark_days",
                column: "roster_period_id");

            migrationBuilder.CreateIndex(
                name: "daily_loads_type_idx",
                table: "daily_loads",
                column: "load_type_id");

            migrationBuilder.CreateIndex(
                name: "day_off_requests_employee_idx",
                table: "day_off_requests",
                columns: new[] { "employee_id", "date" });

            migrationBuilder.CreateIndex(
                name: "day_plans_date_idx",
                table: "day_plans",
                columns: new[] { "date", "roster_status" });

            migrationBuilder.CreateIndex(
                name: "employee_leave_employee_idx",
                table: "employee_leave",
                columns: new[] { "employee_id", "start_date" });

            migrationBuilder.CreateIndex(
                name: "employees_position_idx",
                table: "employees",
                column: "position_code");

            migrationBuilder.CreateIndex(
                name: "employees_store_idx",
                table: "employees",
                columns: new[] { "store_id", "active" });

            migrationBuilder.CreateIndex(
                name: "employees_store_ukg_idx",
                table: "employees",
                columns: new[] { "store_id", "ukg_id" },
                unique: true,
                filter: "[ukg_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "feed_tokens_token_key",
                table: "feed_tokens",
                column: "token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "fixed_rosters_employee_week_day_key",
                table: "fixed_rosters",
                columns: new[] { "employee_id", "week_index", "day_of_week" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "generated_shifts_emp_idx",
                table: "generated_shifts",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "generated_shifts_locked_by_idx",
                table: "generated_shifts",
                column: "locked_by");

            migrationBuilder.CreateIndex(
                name: "generated_shifts_store_date_idx",
                table: "generated_shifts",
                columns: new[] { "store_id", "date" });

            migrationBuilder.CreateIndex(
                name: "labour_group_rules_day_idx",
                table: "labour_group_rules",
                columns: new[] { "day_of_week", "labour_group" });

            migrationBuilder.CreateIndex(
                name: "leave_liability_snapshots_store_id_period_end_ukg_employee_id_key",
                table: "leave_liability_snapshots",
                columns: new[] { "store_id", "period_end", "ukg_employee_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "lls_batch_idx",
                table: "leave_liability_snapshots",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "lls_store_period_idx",
                table: "leave_liability_snapshots",
                columns: new[] { "store_id", "period_end" });

            migrationBuilder.CreateIndex(
                name: "leave_requests_ukg_store_id_ukg_employee_id_leave_type_start_date_end_date_key",
                table: "leave_requests_ukg",
                columns: new[] { "store_id", "ukg_employee_id", "leave_type", "start_date", "end_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "lru_batch_idx",
                table: "leave_requests_ukg",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "lru_store_dates_idx",
                table: "leave_requests_ukg",
                columns: new[] { "store_id", "start_date", "end_date" });

            migrationBuilder.CreateIndex(
                name: "load_types_name_key",
                table: "load_types",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "non_op_entries_emp_idx",
                table: "non_op_entries",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "non_op_entries_store_date_idx",
                table: "non_op_entries",
                columns: new[] { "store_id", "date" });

            migrationBuilder.CreateIndex(
                name: "non_op_entries_sub_idx",
                table: "non_op_entries",
                column: "subcategory_id");

            migrationBuilder.CreateIndex(
                name: "non_op_subcategories_cat_idx",
                table: "non_op_subcategories",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "notifications_user_idx",
                table: "notifications",
                columns: new[] { "user_id", "read_at" });

            migrationBuilder.CreateIndex(
                name: "pay_rates_role_idx",
                table: "pay_rates",
                column: "role");

            migrationBuilder.CreateIndex(
                name: "plan_placement_rules_labour_group_key",
                table: "plan_placement_rules",
                column: "labour_group",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "plan_snapshots_by_idx",
                table: "plan_snapshots",
                column: "generated_by");

            migrationBuilder.CreateIndex(
                name: "pis_batch_idx",
                table: "pos_interval_stats",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "public_holidays_local_store_id_date_name_key",
                table: "public_holidays_local",
                columns: new[] { "store_id", "date", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "public_holidays_national_date_name_key",
                table: "public_holidays_national",
                columns: new[] { "date", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "public_holidays_state_state_date_name_key",
                table: "public_holidays_state",
                columns: new[] { "state", "date", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "qha_batch_idx",
                table: "quarter_hour_allocation",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "quarter_hour_segments_batch_idx",
                table: "quarter_hour_segments",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "quarter_hour_segments_period_idx",
                table: "quarter_hour_segments",
                column: "roster_period_id");

            migrationBuilder.CreateIndex(
                name: "regions_dg_idx",
                table: "regions",
                column: "director_group_id");

            migrationBuilder.CreateIndex(
                name: "reset_tasks_name_key",
                table: "reset_tasks",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "roster_audit_store_idx",
                table: "roster_audit",
                columns: new[] { "store_id", "date" });

            migrationBuilder.CreateIndex(
                name: "roster_audit_user_idx",
                table: "roster_audit",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "roster_periods_start_date_key",
                table: "roster_periods",
                column: "start_date",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "roster_review_checks_checked_by_idx",
                table: "roster_review_checks",
                column: "checked_by");

            migrationBuilder.CreateIndex(
                name: "roster_review_days_decided_by_idx",
                table: "roster_review_days",
                column: "decided_by");

            migrationBuilder.CreateIndex(
                name: "roster_review_events_actor_idx",
                table: "roster_review_events",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "roster_review_events_period_idx",
                table: "roster_review_events",
                columns: new[] { "store_id", "period_start", "created_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "roster_reviews_decided_by_idx",
                table: "roster_reviews",
                column: "decided_by");

            migrationBuilder.CreateIndex(
                name: "roster_reviews_submitted_by_idx",
                table: "roster_reviews",
                column: "submitted_by");

            migrationBuilder.CreateIndex(
                name: "shift_task_blocks_shift_idx",
                table: "shift_task_blocks",
                columns: new[] { "shift_id", "block_time" });

            migrationBuilder.CreateIndex(
                name: "special_trading_hours_store_idx",
                table: "special_trading_hours",
                columns: new[] { "store_id", "start_date" });

            migrationBuilder.CreateIndex(
                name: "store_attributes_pair_idx",
                table: "store_attributes",
                column: "pair_store_id");

            migrationBuilder.CreateIndex(
                name: "store_walk_notes_assigned_idx",
                table: "store_walk_notes",
                column: "assigned_employee_id");

            migrationBuilder.CreateIndex(
                name: "swn_open_idx",
                table: "store_walk_notes",
                columns: new[] { "store_id", "raised_date" },
                filter: "[completed_at] IS NULL");

            migrationBuilder.CreateIndex(
                name: "stores_area_idx",
                table: "stores",
                column: "area_id");

            migrationBuilder.CreateIndex(
                name: "stores_code_key",
                table: "stores",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "stores_kiosk_token_key",
                table: "stores",
                column: "kiosk_token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "stores_region_idx",
                table: "stores",
                column: "region_id");

            migrationBuilder.CreateIndex(
                name: "stores_slug_key",
                table: "stores",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "upload_batches_period_idx",
                table: "upload_batches",
                column: "roster_period_id");

            migrationBuilder.CreateIndex(
                name: "upload_batches_type_uploaded_idx",
                table: "upload_batches",
                columns: new[] { "type", "uploaded_at" });

            migrationBuilder.CreateIndex(
                name: "upload_batches_user_idx",
                table: "upload_batches",
                column: "uploaded_by");

            migrationBuilder.CreateIndex(
                name: "weekend_markers_weekend_idx",
                table: "weekend_markers",
                column: "weekend_start");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "actuals_days");

            migrationBuilder.DropTable(
                name: "admin_task_checks");

            migrationBuilder.DropTable(
                name: "admin_tasks");

            migrationBuilder.DropTable(
                name: "benchmark_days");

            migrationBuilder.DropTable(
                name: "break_rules");

            migrationBuilder.DropTable(
                name: "cron_runs");

            migrationBuilder.DropTable(
                name: "daily_loads");

            migrationBuilder.DropTable(
                name: "day_off_requests");

            migrationBuilder.DropTable(
                name: "day_plans");

            migrationBuilder.DropTable(
                name: "employee_availability");

            migrationBuilder.DropTable(
                name: "employee_leave");

            migrationBuilder.DropTable(
                name: "employee_restricted_tasks");

            migrationBuilder.DropTable(
                name: "feed_tokens");

            migrationBuilder.DropTable(
                name: "fixed_roster_cycles");

            migrationBuilder.DropTable(
                name: "fixed_rosters");

            migrationBuilder.DropTable(
                name: "labour_group_priority");

            migrationBuilder.DropTable(
                name: "labour_group_rules");

            migrationBuilder.DropTable(
                name: "leave_accrual_bands");

            migrationBuilder.DropTable(
                name: "leave_liability_snapshots");

            migrationBuilder.DropTable(
                name: "leave_requests_ukg");

            migrationBuilder.DropTable(
                name: "leave_settings");

            migrationBuilder.DropTable(
                name: "load_assignments");

            migrationBuilder.DropTable(
                name: "non_op_entries");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "pay_rates");

            migrationBuilder.DropTable(
                name: "pay_settings");

            migrationBuilder.DropTable(
                name: "payroll_tax_rates");

            migrationBuilder.DropTable(
                name: "plan_placement_rules");

            migrationBuilder.DropTable(
                name: "plan_placement_settings");

            migrationBuilder.DropTable(
                name: "plan_snapshots");

            migrationBuilder.DropTable(
                name: "planned_blocks");

            migrationBuilder.DropTable(
                name: "pos_interval_stats");

            migrationBuilder.DropTable(
                name: "probation_buffers");

            migrationBuilder.DropTable(
                name: "public_holidays_local");

            migrationBuilder.DropTable(
                name: "public_holidays_national");

            migrationBuilder.DropTable(
                name: "public_holidays_state");

            migrationBuilder.DropTable(
                name: "quarter_hour_allocation");

            migrationBuilder.DropTable(
                name: "quarter_hour_segments");

            migrationBuilder.DropTable(
                name: "reset_tasks");

            migrationBuilder.DropTable(
                name: "roster_audit");

            migrationBuilder.DropTable(
                name: "roster_order_steps");

            migrationBuilder.DropTable(
                name: "roster_review_checks");

            migrationBuilder.DropTable(
                name: "roster_review_days");

            migrationBuilder.DropTable(
                name: "roster_review_events");

            migrationBuilder.DropTable(
                name: "roster_reviews");

            migrationBuilder.DropTable(
                name: "rostering_rules");

            migrationBuilder.DropTable(
                name: "sales_overrides");

            migrationBuilder.DropTable(
                name: "school_term_exceptions");

            migrationBuilder.DropTable(
                name: "school_terms");

            migrationBuilder.DropTable(
                name: "shift_task_blocks");

            migrationBuilder.DropTable(
                name: "special_trading_hours");

            migrationBuilder.DropTable(
                name: "store_attributes");

            migrationBuilder.DropTable(
                name: "store_trading_hours");

            migrationBuilder.DropTable(
                name: "store_walk_notes");

            migrationBuilder.DropTable(
                name: "tab_visibility");

            migrationBuilder.DropTable(
                name: "variance_settings");

            migrationBuilder.DropTable(
                name: "weekend_markers");

            migrationBuilder.DropTable(
                name: "load_types");

            migrationBuilder.DropTable(
                name: "non_op_subcategories");

            migrationBuilder.DropTable(
                name: "upload_batches");

            migrationBuilder.DropTable(
                name: "generated_shifts");

            migrationBuilder.DropTable(
                name: "non_op_categories");

            migrationBuilder.DropTable(
                name: "roster_periods");

            migrationBuilder.DropTable(
                name: "employees");

            migrationBuilder.DropTable(
                name: "app_users");

            migrationBuilder.DropTable(
                name: "positions");

            migrationBuilder.DropTable(
                name: "stores");

            migrationBuilder.DropTable(
                name: "areas");

            migrationBuilder.DropTable(
                name: "regions");

            migrationBuilder.DropTable(
                name: "director_groups");
        }
    }
}
