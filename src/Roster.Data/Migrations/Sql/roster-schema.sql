IF OBJECT_ID(N'[__ef_migrations_history]') IS NULL
BEGIN
    CREATE TABLE [__ef_migrations_history] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___ef_migrations_history] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
CREATE TABLE [admin_tasks] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [name] nvarchar(200) NOT NULL,
    [schedule_type] nvarchar(16) NOT NULL,
    [day_of_week] int NULL,
    [nth_week] int NULL,
    [region_days] nvarchar(max) NULL,
    [duration_type] nvarchar(32) NOT NULL,
    [fixed_minutes] float NULL,
    [minutes_per_unit] float NULL,
    [min_minutes] float NULL,
    [window_start_kind] nvarchar(8) NOT NULL DEFAULT N'none',
    [window_start_time] time(0) NULL,
    [window_start_offset_min] int NOT NULL DEFAULT 0,
    [window_end_kind] nvarchar(8) NOT NULL DEFAULT N'none',
    [window_end_time] time(0) NULL,
    [window_end_offset_min] int NOT NULL DEFAULT 0,
    [sort_order] int NOT NULL DEFAULT 0,
    [active] bit NOT NULL DEFAULT CAST(1 AS bit),
    CONSTRAINT [admin_tasks_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [admin_tasks_day_of_week_check] CHECK ([day_of_week] BETWEEN 0 AND 6),
    CONSTRAINT [admin_tasks_duration_type_check] CHECK ([duration_type] IN (N'fixed', N'per_employee', N'per_register', N'till_calibration')),
    CONSTRAINT [admin_tasks_fixed_minutes_check] CHECK ([fixed_minutes] >= 0),
    CONSTRAINT [admin_tasks_min_minutes_check] CHECK ([min_minutes] >= 0),
    CONSTRAINT [admin_tasks_minutes_per_unit_check] CHECK ([minutes_per_unit] >= 0),
    CONSTRAINT [admin_tasks_nth_week_check] CHECK ([nth_week] BETWEEN 1 AND 5),
    CONSTRAINT [admin_tasks_region_days_check] CHECK ([region_days] IS NULL OR ISJSON([region_days]) = 1),
    CONSTRAINT [admin_tasks_schedule_type_check] CHECK ([schedule_type] IN (N'daily', N'weekly', N'monthly_nth', N'planning_week')),
    CONSTRAINT [admin_tasks_window_end_kind_check] CHECK ([window_end_kind] IN (N'none', N'clock', N'open', N'close')),
    CONSTRAINT [admin_tasks_window_start_kind_check] CHECK ([window_start_kind] IN (N'none', N'clock', N'open', N'close'))
);

CREATE TABLE [break_rules] (
    [role] nvarchar(8) NOT NULL,
    [min_shift_hours] float NOT NULL,
    [max_shift_hours] float NOT NULL,
    [gap_hours] float NOT NULL,
    [break_type] nvarchar(32) NOT NULL,
    [weekly_cap] float NULL,
    [consec_day_cap] int NULL,
    [ot_1_5_after_hours] float NULL,
    [ot_1_9_after_hours] float NULL,
    [ot_period_after_hours] float NULL,
    [double_time_under_gap_hours] float NULL,
    CONSTRAINT [break_rules_pkey] PRIMARY KEY ([role]),
    CONSTRAINT [break_rules_break_type_check] CHECK ([break_type] IN (N'flat_30_unpaid', N'tiered')),
    CONSTRAINT [break_rules_role_check] CHECK ([role] IN (N'SM', N'ASM', N'DSM', N'RSM', N'SA', N'JSA'))
);

CREATE TABLE [cron_runs] (
    [job] nvarchar(100) NOT NULL,
    [last_run_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    [run_count] bigint NOT NULL DEFAULT CAST(0 AS bigint),
    [last_result] nvarchar(max) NULL,
    CONSTRAINT [cron_runs_pkey] PRIMARY KEY ([job]),
    CONSTRAINT [cron_runs_last_result_check] CHECK ([last_result] IS NULL OR ISJSON([last_result]) = 1)
);

CREATE TABLE [director_groups] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [name] nvarchar(200) NOT NULL,
    [created_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT [director_groups_pkey] PRIMARY KEY ([id])
);

CREATE TABLE [feed_tokens] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [label] nvarchar(200) NOT NULL,
    [token] nvarchar(128) NOT NULL DEFAULT (LOWER(CONVERT(varchar(32), HASHBYTES('MD5', CONVERT(varchar(36), NEWID())), 2) + CONVERT(varchar(32), HASHBYTES('MD5', CONVERT(varchar(36), NEWID())), 2))),
    [created_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    [last_used_at] datetimeoffset NULL,
    CONSTRAINT [feed_tokens_pkey] PRIMARY KEY ([id])
);

CREATE TABLE [labour_group_priority] (
    [labour_group] nvarchar(32) NOT NULL,
    [priority] int NOT NULL,
    CONSTRAINT [labour_group_priority_pkey] PRIMARY KEY ([labour_group])
);

CREATE TABLE [labour_group_rules] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [labour_group] nvarchar(32) NOT NULL,
    [day_of_week] int NOT NULL,
    [window_start] time(0) NOT NULL,
    [window_end] time(0) NOT NULL,
    [window_hours] float NULL,
    CONSTRAINT [labour_group_rules_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [labour_group_rules_day_of_week_check] CHECK ([day_of_week] BETWEEN 0 AND 6),
    CONSTRAINT [labour_group_rules_window_hours_check] CHECK ([window_hours] IS NULL OR [window_hours] > 0)
);

CREATE TABLE [leave_accrual_bands] (
    [contract_hours] float NOT NULL,
    [role_class] nvarchar(8) NOT NULL,
    [leave_notional_hours] float NOT NULL,
    [accrual_weeks] float NOT NULL,
    [annual_accrual_hours] float NOT NULL,
    CONSTRAINT [leave_accrual_bands_pkey] PRIMARY KEY ([contract_hours], [role_class]),
    CONSTRAINT [leave_accrual_bands_role_class_check] CHECK ([role_class] IN (N'SM', N'ASM', N'DSM', N'SA'))
);

CREATE TABLE [leave_settings] (
    [id] bit NOT NULL DEFAULT CAST(1 AS bit),
    [target_hours_per_fte] float NOT NULL DEFAULT 76.0E0,
    [fte_divisor] float NOT NULL DEFAULT 76.0E0,
    [updated_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT [leave_settings_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [leave_settings_id_check] CHECK ([id] = 1)
);

CREATE TABLE [load_types] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [name] nvarchar(100) NOT NULL,
    [minutes_per_pallet] float NOT NULL,
    [color] nvarchar(16) NOT NULL DEFAULT N'#2563eb',
    [sort_order] int NOT NULL DEFAULT 0,
    [minutes_per_pallet_lt90] float NULL,
    [minutes_per_pallet_lt60] float NULL,
    [minutes_per_pallet_lt30] float NULL,
    [priority] int NOT NULL DEFAULT 100,
    [sub_task] nvarchar(32) NULL,
    CONSTRAINT [load_types_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [load_types_minutes_per_pallet_check] CHECK ([minutes_per_pallet] > 0),
    CONSTRAINT [load_types_minutes_per_pallet_lt30_check] CHECK ([minutes_per_pallet_lt30] > 0),
    CONSTRAINT [load_types_minutes_per_pallet_lt60_check] CHECK ([minutes_per_pallet_lt60] > 0),
    CONSTRAINT [load_types_minutes_per_pallet_lt90_check] CHECK ([minutes_per_pallet_lt90] > 0),
    CONSTRAINT [load_types_sub_task_check] CHECK ([sub_task] IS NULL OR [sub_task] IN (N'produce', N'long_life', N'freezer', N'ambient', N'bread', N'short_life', N'freeze_to_thaw', N'ambient_last'))
);

CREATE TABLE [non_op_categories] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [name] nvarchar(200) NOT NULL,
    [sort] int NOT NULL DEFAULT 0,
    CONSTRAINT [non_op_categories_pkey] PRIMARY KEY ([id])
);

CREATE TABLE [pay_settings] (
    [id] bit NOT NULL DEFAULT CAST(1 AS bit),
    [rsm_allowance] float NOT NULL DEFAULT 9.0E0,
    [super_rate] float NOT NULL DEFAULT 0.12E0,
    [overtime_1_5_multiplier] float NOT NULL DEFAULT 1.5E0,
    [overtime_1_9_multiplier] float NOT NULL DEFAULT 1.8999999999999999E0,
    [double_time_multiplier] float NOT NULL DEFAULT 2.0E0,
    [sunday_ot_from_hour] float NOT NULL DEFAULT 0.0E0,
    [sunday_ot_to_hour] float NOT NULL DEFAULT 9.0E0,
    CONSTRAINT [pay_settings_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [pay_settings_id_check] CHECK ([id] = 1)
);

CREATE TABLE [payroll_tax_rates] (
    [state] nvarchar(3) NOT NULL,
    [rate] float NOT NULL DEFAULT 0.0E0,
    CONSTRAINT [payroll_tax_rates_pkey] PRIMARY KEY ([state]),
    CONSTRAINT [payroll_tax_rates_state_check] CHECK ([state] IN (N'NSW', N'VIC', N'QLD', N'SA', N'WA', N'TAS', N'NT', N'ACT'))
);

CREATE TABLE [plan_placement_rules] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [position] int NOT NULL,
    [labour_group] nvarchar(32) NOT NULL,
    [strategy] nvarchar(32) NOT NULL,
    [anchor_kind] nvarchar(8) NOT NULL DEFAULT N'none',
    [anchor_time] time(0) NULL,
    [anchor_offset_min] int NOT NULL DEFAULT 0,
    [pre_peak_pct] float NULL,
    [post_peak_pct] float NULL,
    [avoid_strength] float NULL,
    [night_trader_strategy] nvarchar(32) NULL,
    [updated_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT [plan_placement_rules_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [plan_placement_rules_anchor_kind_check] CHECK ([anchor_kind] IN (N'none', N'clock', N'open', N'close')),
    CONSTRAINT [plan_placement_rules_avoid_strength_check] CHECK ([avoid_strength] BETWEEN 0 AND 100),
    CONSTRAINT [plan_placement_rules_labour_group_check] CHECK ([labour_group] IN (N'checkout', N'load_running', N'reset', N'admin', N'specials', N'cleaning')),
    CONSTRAINT [plan_placement_rules_night_trader_strategy_check] CHECK ([night_trader_strategy] IN (N'spread', N'follow_demand', N'backfill', N'forward_fill', N'peak_split', N'avoid_peak')),
    CONSTRAINT [plan_placement_rules_post_peak_pct_check] CHECK ([post_peak_pct] BETWEEN 0 AND 100),
    CONSTRAINT [plan_placement_rules_pre_peak_pct_check] CHECK ([pre_peak_pct] BETWEEN 0 AND 100),
    CONSTRAINT [plan_placement_rules_strategy_check] CHECK ([strategy] IN (N'spread', N'follow_demand', N'backfill', N'forward_fill', N'peak_split', N'avoid_peak'))
);

CREATE TABLE [plan_placement_settings] (
    [id] bit NOT NULL DEFAULT CAST(1 AS bit),
    [enabled] bit NOT NULL DEFAULT CAST(0 AS bit),
    [peak_basis] nvarchar(16) NOT NULL DEFAULT N'hourly',
    [peak_smoothing_blocks] int NOT NULL DEFAULT 3,
    [peak_threshold_pct] float NOT NULL DEFAULT 85.0E0,
    [night_trader_from] time(0) NOT NULL DEFAULT '17:00:00',
    [night_trader_share_pct] float NOT NULL DEFAULT 25.0E0,
    [updated_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    [step_order] nvarchar(max) NOT NULL DEFAULT (N'["admin","load_running","reset","specials","cleaning"]'),
    CONSTRAINT [plan_placement_settings_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [plan_placement_settings_id_check] CHECK ([id] = 1),
    CONSTRAINT [plan_placement_settings_night_trader_share_pct_check] CHECK ([night_trader_share_pct] BETWEEN 0 AND 100),
    CONSTRAINT [plan_placement_settings_peak_basis_check] CHECK ([peak_basis] IN (N'hourly', N'rolling')),
    CONSTRAINT [plan_placement_settings_peak_smoothing_blocks_check] CHECK ([peak_smoothing_blocks] BETWEEN 1 AND 16),
    CONSTRAINT [plan_placement_settings_peak_threshold_pct_check] CHECK ([peak_threshold_pct] BETWEEN 1 AND 100),
    CONSTRAINT [plan_placement_settings_step_order_check] CHECK ([step_order] IS NULL OR ISJSON([step_order]) = 1)
);

CREATE TABLE [positions] (
    [code] nvarchar(32) NOT NULL,
    [label] nvarchar(200) NOT NULL,
    [base_role] nvarchar(8) NOT NULL,
    [weekly_hours] float NULL,
    [year_band] int NULL,
    [has_rate] bit NOT NULL DEFAULT CAST(1 AS bit),
    [sort_order] int NOT NULL DEFAULT 0,
    [active] bit NOT NULL DEFAULT CAST(1 AS bit),
    CONSTRAINT [positions_pkey] PRIMARY KEY ([code]),
    CONSTRAINT [positions_base_role_check] CHECK ([base_role] IN (N'SM', N'ASM', N'DSM', N'RSM', N'SA', N'JSA')),
    CONSTRAINT [positions_weekly_hours_check] CHECK ([weekly_hours] >= 0),
    CONSTRAINT [positions_year_band_check] CHECK ([year_band] >= 0)
);

CREATE TABLE [probation_buffers] (
    [labour_group] nvarchar(32) NOT NULL DEFAULT N'*',
    [max_days] int NOT NULL,
    [uplift_pct] float NOT NULL DEFAULT 0.0E0,
    CONSTRAINT [probation_buffers_pkey] PRIMARY KEY ([labour_group], [max_days]),
    CONSTRAINT [probation_buffers_max_days_check] CHECK ([max_days] > 0),
    CONSTRAINT [probation_buffers_uplift_pct_check] CHECK ([uplift_pct] >= 0 AND [uplift_pct] <= 300)
);

CREATE TABLE [public_holidays_national] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [date] date NOT NULL,
    [name] nvarchar(200) NOT NULL,
    CONSTRAINT [public_holidays_national_pkey] PRIMARY KEY ([id])
);

CREATE TABLE [public_holidays_state] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [state] nvarchar(3) NOT NULL,
    [date] date NOT NULL,
    [name] nvarchar(200) NOT NULL,
    CONSTRAINT [public_holidays_state_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [public_holidays_state_state_check] CHECK ([state] IN (N'NSW', N'VIC', N'QLD', N'SA', N'WA', N'TAS', N'NT', N'ACT'))
);

CREATE TABLE [reset_tasks] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [name] nvarchar(200) NOT NULL,
    [share_pct] float NULL,
    [fixed_minutes] float NULL,
    [window_start_kind] nvarchar(8) NOT NULL DEFAULT N'none',
    [window_start_time] time(0) NULL,
    [window_start_offset_min] int NOT NULL DEFAULT 0,
    [window_end_kind] nvarchar(8) NOT NULL DEFAULT N'none',
    [window_end_time] time(0) NULL,
    [window_end_offset_min] int NOT NULL DEFAULT 0,
    [sort_order] int NOT NULL DEFAULT 0,
    [active] bit NOT NULL DEFAULT CAST(1 AS bit),
    [placement] nvarchar(16) NOT NULL DEFAULT N'spread',
    [sub_task] nvarchar(32) NULL,
    CONSTRAINT [reset_tasks_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [reset_tasks_fixed_minutes_check] CHECK ([fixed_minutes] > 0),
    CONSTRAINT [reset_tasks_one_sizing] CHECK ([share_pct] IS NULL OR [fixed_minutes] IS NULL),
    CONSTRAINT [reset_tasks_placement_check] CHECK ([placement] IN (N'spread', N'avoid_peak', N'follow_demand', N'late', N'early')),
    CONSTRAINT [reset_tasks_share_pct_check] CHECK ([share_pct] > 0 AND [share_pct] <= 100),
    CONSTRAINT [reset_tasks_sub_task_check] CHECK ([sub_task] IS NULL OR [sub_task] IN (N're_runs', N'decarding')),
    CONSTRAINT [reset_tasks_window_end_kind_check] CHECK ([window_end_kind] IN (N'none', N'clock', N'open', N'close')),
    CONSTRAINT [reset_tasks_window_start_kind_check] CHECK ([window_start_kind] IN (N'none', N'clock', N'open', N'close'))
);

CREATE TABLE [roster_order_steps] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [position] int NOT NULL,
    [step_type] nvarchar(32) NOT NULL,
    [day_order] nvarchar(max) NULL,
    [updated_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT [roster_order_steps_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [roster_order_steps_day_order_check] CHECK ([day_order] IS NULL OR ISJSON([day_order]) = 1),
    CONSTRAINT [roster_order_steps_step_type_check] CHECK ([step_type] IN (N'ALLOCATE_SM', N'ALLOCATE_ASM', N'ALLOCATE_DSM', N'FILL_FLOOR', N'SHUFFLE_JSA', N'CONTRACT_CHECK'))
);

CREATE TABLE [roster_periods] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [start_date] date NOT NULL,
    [end_date] date NOT NULL,
    [created_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT [roster_periods_pkey] PRIMARY KEY ([id])
);

CREATE TABLE [rostering_rules] (
    [key] nvarchar(100) NOT NULL,
    [value] float NOT NULL,
    [note] nvarchar(max) NULL,
    [updated_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT [rostering_rules_pkey] PRIMARY KEY ([key])
);

CREATE TABLE [school_term_exceptions] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [state] nvarchar(3) NOT NULL,
    [date] date NOT NULL,
    [note] nvarchar(max) NULL,
    CONSTRAINT [school_term_exceptions_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [school_term_exceptions_state_check] CHECK ([state] IN (N'NSW', N'VIC', N'QLD', N'SA', N'WA', N'TAS', N'NT', N'ACT'))
);

CREATE TABLE [school_terms] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [state] nvarchar(3) NOT NULL,
    [term_label] nvarchar(100) NOT NULL,
    [start_date] date NOT NULL,
    [end_date] date NOT NULL,
    CONSTRAINT [school_terms_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [school_terms_state_check] CHECK ([state] IN (N'NSW', N'VIC', N'QLD', N'SA', N'WA', N'TAS', N'NT', N'ACT'))
);

CREATE TABLE [tab_visibility] (
    [level] nvarchar(16) NOT NULL,
    [tab_key] nvarchar(200) NOT NULL,
    [visible] bit NOT NULL DEFAULT CAST(1 AS bit),
    CONSTRAINT [tab_visibility_pkey] PRIMARY KEY ([level], [tab_key])
);

CREATE TABLE [variance_settings] (
    [id] bit NOT NULL DEFAULT CAST(1 AS bit),
    [data_light_pct] float NOT NULL DEFAULT 15.0E0,
    [metric_pct] float NOT NULL DEFAULT 70.0E0,
    [hours_pct] float NOT NULL DEFAULT 20.0E0,
    [updated_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT [variance_settings_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [variance_settings_data_light_pct_check] CHECK ([data_light_pct] BETWEEN 0 AND 100),
    CONSTRAINT [variance_settings_hours_pct_check] CHECK ([hours_pct] BETWEEN 0 AND 500),
    CONSTRAINT [variance_settings_id_check] CHECK ([id] = 1),
    CONSTRAINT [variance_settings_metric_pct_check] CHECK ([metric_pct] BETWEEN 0 AND 500)
);

CREATE TABLE [regions] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [name] nvarchar(200) NOT NULL,
    [director_group_id] uniqueidentifier NOT NULL,
    [created_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT [regions_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [regions_director_group_id_fkey] FOREIGN KEY ([director_group_id]) REFERENCES [director_groups] ([id]) ON DELETE CASCADE
);

CREATE TABLE [non_op_subcategories] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [category_id] uniqueidentifier NOT NULL,
    [name] nvarchar(200) NOT NULL,
    [min_minutes] int NULL,
    [max_minutes] int NULL,
    [days_allowed] nvarchar(max) NOT NULL DEFAULT (N'[0,1,2,3,4,5,6]'),
    [default_minutes] int NULL,
    [note] nvarchar(max) NULL,
    CONSTRAINT [non_op_subcategories_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [non_op_subcategories_days_allowed_check] CHECK ([days_allowed] IS NULL OR ISJSON([days_allowed]) = 1),
    CONSTRAINT [non_op_subcategories_category_id_fkey] FOREIGN KEY ([category_id]) REFERENCES [non_op_categories] ([id])
);

CREATE TABLE [pay_rates] (
    [day_type] nvarchar(16) NOT NULL,
    [hour] int NOT NULL,
    [role] nvarchar(32) NOT NULL,
    [rate] float NOT NULL,
    CONSTRAINT [pay_rates_pkey] PRIMARY KEY ([day_type], [hour], [role]),
    CONSTRAINT [pay_rates_day_type_check] CHECK ([day_type] IN (N'weekday', N'saturday', N'sunday', N'public_holiday')),
    CONSTRAINT [pay_rates_hour_check] CHECK ([hour] BETWEEN 0 AND 23),
    CONSTRAINT [pay_rates_role_fkey] FOREIGN KEY ([role]) REFERENCES [positions] ([code]) ON DELETE CASCADE
);

CREATE TABLE [areas] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [name] nvarchar(200) NOT NULL,
    [region_id] uniqueidentifier NULL,
    CONSTRAINT [areas_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [areas_region_id_fkey] FOREIGN KEY ([region_id]) REFERENCES [regions] ([id])
);

CREATE TABLE [stores] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [code] nvarchar(16) NOT NULL,
    [name] nvarchar(200) NOT NULL,
    [slug] nvarchar(64) NOT NULL,
    [region_id] uniqueidentifier NOT NULL,
    [state] nvarchar(3) NOT NULL,
    [created_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    [kiosk_token] nvarchar(64) NOT NULL DEFAULT (LOWER(CONVERT(varchar(32), HASHBYTES('MD5', CONVERT(varchar(36), NEWID())), 2))),
    [area_id] uniqueidentifier NULL,
    [dso_name] nvarchar(200) NULL,
    CONSTRAINT [stores_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [stores_state_check] CHECK ([state] IN (N'NSW', N'VIC', N'QLD', N'SA', N'WA', N'TAS', N'NT', N'ACT')),
    CONSTRAINT [stores_area_id_fkey] FOREIGN KEY ([area_id]) REFERENCES [areas] ([id]),
    CONSTRAINT [stores_region_id_fkey] FOREIGN KEY ([region_id]) REFERENCES [regions] ([id]) ON DELETE NO ACTION
);

CREATE TABLE [admin_task_checks] (
    [store_id] uniqueidentifier NOT NULL,
    [date] date NOT NULL,
    [task_name] nvarchar(200) NOT NULL,
    [checked_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT [admin_task_checks_pkey] PRIMARY KEY ([store_id], [date], [task_name]),
    CONSTRAINT [admin_task_checks_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE
);

CREATE TABLE [app_users] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [username] nvarchar(256) NOT NULL,
    [tier] nvarchar(16) NOT NULL,
    [store_id] uniqueidentifier NULL,
    [region_id] uniqueidentifier NULL,
    [director_group_id] uniqueidentifier NULL,
    [area_id] uniqueidentifier NULL,
    [created_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    [entra_object_id] nvarchar(64) NULL,
    [user_principal_name] nvarchar(256) NULL,
    [windows_account] nvarchar(256) NULL,
    [display_name] nvarchar(256) NULL,
    [last_sign_in_at] datetimeoffset NULL,
    CONSTRAINT [app_users_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [app_users_tier_check] CHECK ([tier] IN (N'super_user', N'store', N'pa', N'director', N'emso')),
    CONSTRAINT [tier_scope] CHECK (([tier] = 'super_user' AND [store_id] IS NULL AND [region_id] IS NULL AND [director_group_id] IS NULL) OR ([tier] = 'store' AND [store_id] IS NOT NULL) OR ([tier] = 'pa') OR ([tier] = 'director') OR ([tier] = 'emso' AND [area_id] IS NOT NULL AND [store_id] IS NULL AND [director_group_id] IS NULL)),
    CONSTRAINT [app_users_area_id_fkey] FOREIGN KEY ([area_id]) REFERENCES [areas] ([id]),
    CONSTRAINT [app_users_director_group_id_fkey] FOREIGN KEY ([director_group_id]) REFERENCES [director_groups] ([id]),
    CONSTRAINT [app_users_region_id_fkey] FOREIGN KEY ([region_id]) REFERENCES [regions] ([id]) ON DELETE SET NULL,
    CONSTRAINT [app_users_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id])
);

CREATE TABLE [daily_loads] (
    [store_id] uniqueidentifier NOT NULL,
    [date] date NOT NULL,
    [load_type_id] uniqueidentifier NOT NULL,
    [qty] int NOT NULL,
    [updated_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT [daily_loads_pkey] PRIMARY KEY ([store_id], [date], [load_type_id]),
    CONSTRAINT [daily_loads_qty_check] CHECK ([qty] >= 0 AND [qty] <= 99),
    CONSTRAINT [daily_loads_load_type_id_fkey] FOREIGN KEY ([load_type_id]) REFERENCES [load_types] ([id]) ON DELETE CASCADE,
    CONSTRAINT [daily_loads_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE
);

CREATE TABLE [day_plans] (
    [store_id] uniqueidentifier NOT NULL,
    [date] date NOT NULL,
    [plan_status] nvarchar(16) NOT NULL DEFAULT N'none',
    [roster_status] nvarchar(16) NOT NULL DEFAULT N'none',
    [plan_generated_at] datetimeoffset NULL,
    [roster_generated_at] datetimeoffset NULL,
    [ph_load_reallocated] bit NOT NULL DEFAULT CAST(0 AS bit),
    CONSTRAINT [day_plans_pkey] PRIMARY KEY ([store_id], [date]),
    CONSTRAINT [day_plans_plan_status_check] CHECK ([plan_status] IN (N'none', N'generated', N'approved')),
    CONSTRAINT [day_plans_roster_status_check] CHECK ([roster_status] IN (N'none', N'generated', N'approved')),
    CONSTRAINT [day_plans_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE
);

CREATE TABLE [employees] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [store_id] uniqueidentifier NOT NULL,
    [name] nvarchar(200) NOT NULL,
    [role] nvarchar(8) NOT NULL,
    [contract_hours] float NOT NULL DEFAULT 0.0E0,
    [sm_year_band] int NULL,
    [notes] nvarchar(max) NULL,
    [active] bit NOT NULL DEFAULT CAST(1 AS bit),
    [created_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    [notional_hours] float NULL,
    [position_code] nvarchar(32) NULL,
    [start_date] date NULL,
    [ukg_id] nvarchar(64) NULL,
    CONSTRAINT [employees_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [employees_notional_hours_check] CHECK ([notional_hours] IS NULL OR ([notional_hours] >= 0 AND [notional_hours] <= 24)),
    CONSTRAINT [employees_role_check] CHECK ([role] IN (N'SM', N'ASM', N'DSM', N'RSM', N'SA', N'JSA')),
    CONSTRAINT [employees_position_code_fkey] FOREIGN KEY ([position_code]) REFERENCES [positions] ([code]) ON DELETE SET NULL,
    CONSTRAINT [employees_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE
);

CREATE TABLE [load_assignments] (
    [store_id] uniqueidentifier NOT NULL,
    [date] date NOT NULL,
    [employee_id] uniqueidentifier NOT NULL,
    [employee_name] nvarchar(200) NOT NULL,
    [load_type] nvarchar(100) NOT NULL,
    [manual] bit NOT NULL DEFAULT CAST(0 AS bit),
    CONSTRAINT [load_assignments_pkey] PRIMARY KEY ([store_id], [date], [employee_id]),
    CONSTRAINT [load_assignments_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE
);

CREATE TABLE [planned_blocks] (
    [store_id] uniqueidentifier NOT NULL,
    [date] date NOT NULL,
    [labour_group] nvarchar(32) NOT NULL,
    [block_time] time(0) NOT NULL,
    [headcount] int NOT NULL,
    CONSTRAINT [planned_blocks_pkey] PRIMARY KEY ([store_id], [date], [labour_group], [block_time]),
    CONSTRAINT [planned_blocks_headcount_check] CHECK ([headcount] >= 0),
    CONSTRAINT [planned_blocks_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE
);

CREATE TABLE [public_holidays_local] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [store_id] uniqueidentifier NOT NULL,
    [date] date NOT NULL,
    [name] nvarchar(200) NOT NULL,
    CONSTRAINT [public_holidays_local_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [public_holidays_local_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE
);

CREATE TABLE [sales_overrides] (
    [store_id] uniqueidentifier NOT NULL,
    [date] date NOT NULL,
    [closed] bit NOT NULL DEFAULT CAST(0 AS bit),
    [method] nvarchar(32) NULL,
    [pct] float NULL,
    [value] float NULL,
    [reason] nvarchar(max) NULL,
    [notes] nvarchar(max) NULL,
    CONSTRAINT [sales_overrides_pkey] PRIMARY KEY ([store_id], [date]),
    CONSTRAINT [sales_overrides_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE
);

CREATE TABLE [special_trading_hours] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [store_id] uniqueidentifier NOT NULL,
    [start_date] date NOT NULL,
    [end_date] date NOT NULL,
    [closed] bit NOT NULL DEFAULT CAST(0 AS bit),
    [open_time] time(0) NULL,
    [close_time] time(0) NULL,
    [note] nvarchar(max) NULL,
    CONSTRAINT [special_trading_hours_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [special_trading_hours_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE
);

CREATE TABLE [store_attributes] (
    [store_id] uniqueidentifier NOT NULL,
    [has_sco] bit NOT NULL DEFAULT CAST(0 AS bit),
    [is_new_sco_model] bit NOT NULL DEFAULT CAST(0 AS bit),
    [mco_registers] int NOT NULL DEFAULT 0,
    [sco_lanes] int NOT NULL DEFAULT 0,
    [sco_efficiency] float NOT NULL DEFAULT 0.45000000000000001E0,
    [first_step] float NOT NULL DEFAULT 1.2E0,
    [sco_share] float NOT NULL DEFAULT 0.66000000000000003E0,
    [oe_target_pct] float NOT NULL DEFAULT 90.0E0,
    [updated_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    [max_staff_on_leave] int NOT NULL DEFAULT 3,
    [night_fill] bit NOT NULL DEFAULT CAST(0 AS bit),
    [opening_date] date NULL,
    [pair_store_id] uniqueidentifier NULL,
    CONSTRAINT [store_attributes_pkey] PRIMARY KEY ([store_id]),
    CONSTRAINT [store_attributes_max_staff_on_leave_check] CHECK ([max_staff_on_leave] >= 0),
    CONSTRAINT [store_attributes_sco_share_check] CHECK ([sco_share] BETWEEN 0 AND 0.90),
    CONSTRAINT [store_attributes_pair_store_id_fkey] FOREIGN KEY ([pair_store_id]) REFERENCES [stores] ([id]),
    CONSTRAINT [store_attributes_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE
);

CREATE TABLE [store_trading_hours] (
    [store_id] uniqueidentifier NOT NULL,
    [day_of_week] int NOT NULL,
    [open_time] time(0) NULL,
    [close_time] time(0) NULL,
    [closed] bit NOT NULL DEFAULT CAST(0 AS bit),
    CONSTRAINT [store_trading_hours_pkey] PRIMARY KEY ([store_id], [day_of_week]),
    CONSTRAINT [store_trading_hours_day_of_week_check] CHECK ([day_of_week] BETWEEN 0 AND 6),
    CONSTRAINT [store_trading_hours_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE
);

CREATE TABLE [notifications] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [user_id] uniqueidentifier NOT NULL,
    [kind] nvarchar(64) NOT NULL,
    [body] nvarchar(max) NOT NULL,
    [link] nvarchar(500) NULL,
    [read_at] datetimeoffset NULL,
    [created_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT [notifications_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [notifications_user_id_fkey] FOREIGN KEY ([user_id]) REFERENCES [app_users] ([id]) ON DELETE CASCADE
);

CREATE TABLE [plan_snapshots] (
    [store_id] uniqueidentifier NOT NULL,
    [period_start] date NOT NULL,
    [kind] nvarchar(16) NOT NULL,
    [payload] nvarchar(max) NOT NULL,
    [generated_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    [generated_by] uniqueidentifier NULL,
    [version] int NOT NULL DEFAULT 1,
    CONSTRAINT [plan_snapshots_pkey] PRIMARY KEY ([store_id], [period_start], [kind]),
    CONSTRAINT [plan_snapshots_kind_check] CHECK ([kind] IN (N'plan', N'roster')),
    CONSTRAINT [plan_snapshots_payload_check] CHECK ([payload] IS NULL OR ISJSON([payload]) = 1),
    CONSTRAINT [plan_snapshots_generated_by_fkey] FOREIGN KEY ([generated_by]) REFERENCES [app_users] ([id]),
    CONSTRAINT [plan_snapshots_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE
);

CREATE TABLE [roster_audit] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [store_id] uniqueidentifier NOT NULL,
    [date] date NULL,
    [user_id] uniqueidentifier NULL,
    [action] nvarchar(64) NOT NULL,
    [detail] nvarchar(max) NULL,
    [created_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT [roster_audit_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [roster_audit_detail_check] CHECK ([detail] IS NULL OR ISJSON([detail]) = 1),
    CONSTRAINT [roster_audit_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE,
    CONSTRAINT [roster_audit_user_id_fkey] FOREIGN KEY ([user_id]) REFERENCES [app_users] ([id])
);

CREATE TABLE [roster_review_checks] (
    [store_id] uniqueidentifier NOT NULL,
    [period_start] date NOT NULL,
    [check_key] nvarchar(64) NOT NULL,
    [note] nvarchar(1000) NULL,
    [checked_by] uniqueidentifier NULL,
    [checked_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT [roster_review_checks_pkey] PRIMARY KEY ([store_id], [period_start], [check_key]),
    CONSTRAINT [roster_review_checks_note_check] CHECK ([note] IS NULL OR LEN([note]) <= 1000),
    CONSTRAINT [roster_review_checks_checked_by_fkey] FOREIGN KEY ([checked_by]) REFERENCES [app_users] ([id]),
    CONSTRAINT [roster_review_checks_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE
);

CREATE TABLE [roster_review_days] (
    [store_id] uniqueidentifier NOT NULL,
    [period_start] date NOT NULL,
    [date] date NOT NULL,
    [verdict] nvarchar(8) NULL,
    [query] nvarchar(1000) NULL,
    [reply] nvarchar(1000) NULL,
    [replied_at] datetimeoffset NULL,
    [prev_query] nvarchar(max) NULL,
    [prev_reply] nvarchar(max) NULL,
    [decided_by] uniqueidentifier NULL,
    [decided_at] datetimeoffset NULL,
    CONSTRAINT [roster_review_days_pkey] PRIMARY KEY ([store_id], [period_start], [date]),
    CONSTRAINT [roster_review_days_query_check] CHECK ([query] IS NULL OR LEN([query]) <= 1000),
    CONSTRAINT [roster_review_days_reply_check] CHECK ([reply] IS NULL OR LEN([reply]) <= 1000),
    CONSTRAINT [roster_review_days_verdict_check] CHECK ([verdict] IN (N'ok', N'query')),
    CONSTRAINT [roster_review_days_decided_by_fkey] FOREIGN KEY ([decided_by]) REFERENCES [app_users] ([id]),
    CONSTRAINT [roster_review_days_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE
);

CREATE TABLE [roster_review_events] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [store_id] uniqueidentifier NOT NULL,
    [period_start] date NOT NULL,
    [actor_id] uniqueidentifier NULL,
    [actor_name] nvarchar(256) NULL,
    [actor_tier] nvarchar(16) NULL,
    [kind] nvarchar(64) NOT NULL,
    [body] nvarchar(3000) NOT NULL,
    [created_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT [roster_review_events_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [roster_review_events_body_check] CHECK (LEN([body]) <= 3000),
    CONSTRAINT [roster_review_events_actor_id_fkey] FOREIGN KEY ([actor_id]) REFERENCES [app_users] ([id]),
    CONSTRAINT [roster_review_events_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE
);

CREATE TABLE [roster_reviews] (
    [store_id] uniqueidentifier NOT NULL,
    [period_start] date NOT NULL,
    [status] nvarchar(16) NOT NULL DEFAULT N'in_progress',
    [snapshot_key] nvarchar(64) NULL,
    [submitted_at] datetimeoffset NULL,
    [submitted_by] uniqueidentifier NULL,
    [decided_at] datetimeoffset NULL,
    [decided_by] uniqueidentifier NULL,
    [return_message] nvarchar(2000) NULL,
    [updated_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT [roster_reviews_pkey] PRIMARY KEY ([store_id], [period_start]),
    CONSTRAINT [roster_reviews_return_message_check] CHECK ([return_message] IS NULL OR LEN([return_message]) <= 2000),
    CONSTRAINT [roster_reviews_status_check] CHECK ([status] IN (N'in_progress', N'submitted', N'returned', N'approved')),
    CONSTRAINT [roster_reviews_decided_by_fkey] FOREIGN KEY ([decided_by]) REFERENCES [app_users] ([id]),
    CONSTRAINT [roster_reviews_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE,
    CONSTRAINT [roster_reviews_submitted_by_fkey] FOREIGN KEY ([submitted_by]) REFERENCES [app_users] ([id])
);

CREATE TABLE [upload_batches] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [type] nvarchar(32) NOT NULL,
    [filename] nvarchar(400) NOT NULL,
    [uploaded_by] uniqueidentifier NULL,
    [uploaded_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    [roster_period_id] uniqueidentifier NULL,
    [total_rows] int NOT NULL DEFAULT 0,
    [accepted_rows] int NOT NULL DEFAULT 0,
    [rejected_rows] int NOT NULL DEFAULT 0,
    [error_report] nvarchar(max) NOT NULL DEFAULT (N'[]'),
    CONSTRAINT [upload_batches_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [upload_batches_error_report_check] CHECK ([error_report] IS NULL OR ISJSON([error_report]) = 1),
    CONSTRAINT [upload_batches_type_check] CHECK ([type] IN (N'benchmark', N'actuals', N'quarter_hour', N'leave_liability', N'leave_requests', N'contacts', N'pos_quarter_hour')),
    CONSTRAINT [upload_batches_roster_period_id_fkey] FOREIGN KEY ([roster_period_id]) REFERENCES [roster_periods] ([id]),
    CONSTRAINT [upload_batches_uploaded_by_fkey] FOREIGN KEY ([uploaded_by]) REFERENCES [app_users] ([id])
);

CREATE TABLE [day_off_requests] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [employee_id] uniqueidentifier NOT NULL,
    [date] date NOT NULL,
    [submitted_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    [notes] nvarchar(max) NULL,
    [status] nvarchar(16) NOT NULL DEFAULT N'pending',
    CONSTRAINT [day_off_requests_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [day_off_requests_status_check] CHECK ([status] IN (N'pending', N'granted', N'declined')),
    CONSTRAINT [day_off_requests_employee_id_fkey] FOREIGN KEY ([employee_id]) REFERENCES [employees] ([id]) ON DELETE CASCADE
);

CREATE TABLE [employee_availability] (
    [employee_id] uniqueidentifier NOT NULL,
    [day_of_week] int NOT NULL,
    [available] bit NOT NULL DEFAULT CAST(1 AS bit),
    [from_time] time(0) NULL,
    [to_time] time(0) NULL,
    CONSTRAINT [employee_availability_pkey] PRIMARY KEY ([employee_id], [day_of_week]),
    CONSTRAINT [employee_availability_day_of_week_check] CHECK ([day_of_week] BETWEEN 0 AND 6),
    CONSTRAINT [employee_availability_employee_id_fkey] FOREIGN KEY ([employee_id]) REFERENCES [employees] ([id]) ON DELETE CASCADE
);

CREATE TABLE [employee_leave] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [employee_id] uniqueidentifier NOT NULL,
    [start_date] date NOT NULL,
    [end_date] date NOT NULL,
    [leave_type] nvarchar(8) NOT NULL DEFAULT N'ANN',
    [day_hours] float NULL,
    [created_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT [employee_leave_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [employee_leave_day_hours_check] CHECK ([day_hours] IS NULL OR ([day_hours] > 0 AND [day_hours] <= 24)),
    CONSTRAINT [employee_leave_type_check] CHECK ([leave_type] IN (N'ANN', N'LS', N'BRV', N'SCK', N'CAR', N'UNP')),
    CONSTRAINT [employee_leave_employee_id_fkey] FOREIGN KEY ([employee_id]) REFERENCES [employees] ([id]) ON DELETE CASCADE
);

CREATE TABLE [employee_restricted_tasks] (
    [employee_id] uniqueidentifier NOT NULL,
    [labour_group] nvarchar(32) NOT NULL,
    CONSTRAINT [employee_restricted_tasks_pkey] PRIMARY KEY ([employee_id], [labour_group]),
    CONSTRAINT [employee_restricted_tasks_employee_id_fkey] FOREIGN KEY ([employee_id]) REFERENCES [employees] ([id]) ON DELETE CASCADE
);

CREATE TABLE [fixed_roster_cycles] (
    [employee_id] uniqueidentifier NOT NULL,
    [cycle_weeks] int NOT NULL DEFAULT 1,
    [start_date] date NOT NULL,
    [updated_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT [fixed_roster_cycles_pkey] PRIMARY KEY ([employee_id]),
    CONSTRAINT [fixed_roster_cycles_cycle_weeks_check] CHECK ([cycle_weeks] IN (1, 2, 4)),
    CONSTRAINT [fixed_roster_cycles_start_date_check] CHECK (DATEDIFF(DAY, CONVERT(date, '19000101', 112), [start_date]) % 7 = 0),
    CONSTRAINT [fixed_roster_cycles_employee_id_fkey] FOREIGN KEY ([employee_id]) REFERENCES [employees] ([id]) ON DELETE CASCADE
);

CREATE TABLE [fixed_rosters] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [employee_id] uniqueidentifier NOT NULL,
    [day_of_week] int NOT NULL,
    [start_time] time(0) NOT NULL,
    [end_time] time(0) NOT NULL,
    [week_index] int NOT NULL DEFAULT 0,
    CONSTRAINT [fixed_rosters_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [fixed_rosters_day_of_week_check] CHECK ([day_of_week] BETWEEN 0 AND 6),
    CONSTRAINT [fixed_rosters_week_index_check] CHECK ([week_index] >= 0 AND [week_index] <= 3),
    CONSTRAINT [fixed_rosters_employee_id_fkey] FOREIGN KEY ([employee_id]) REFERENCES [employees] ([id]) ON DELETE CASCADE
);

CREATE TABLE [generated_shifts] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [store_id] uniqueidentifier NOT NULL,
    [date] date NOT NULL,
    [employee_id] uniqueidentifier NOT NULL,
    [employee_name] nvarchar(200) NOT NULL,
    [role] nvarchar(8) NOT NULL,
    [start_time] time(0) NOT NULL,
    [end_time] time(0) NOT NULL,
    [break_minutes] int NOT NULL DEFAULT 0,
    [locked] bit NOT NULL DEFAULT CAST(0 AS bit),
    [locked_by] uniqueidentifier NULL,
    [locked_at] datetimeoffset NULL,
    [created_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT [generated_shifts_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [generated_shifts_break_minutes_check] CHECK ([break_minutes] >= 0),
    CONSTRAINT [generated_shifts_role_check] CHECK ([role] IN (N'SM', N'ASM', N'DSM', N'RSM', N'SA', N'JSA')),
    CONSTRAINT [generated_shifts_employee_id_fkey] FOREIGN KEY ([employee_id]) REFERENCES [employees] ([id]),
    CONSTRAINT [generated_shifts_locked_by_fkey] FOREIGN KEY ([locked_by]) REFERENCES [app_users] ([id]),
    CONSTRAINT [generated_shifts_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE
);

CREATE TABLE [non_op_entries] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [store_id] uniqueidentifier NOT NULL,
    [date] date NOT NULL,
    [subcategory_id] uniqueidentifier NOT NULL,
    [employee_id] uniqueidentifier NULL,
    [minutes] int NOT NULL,
    [description] nvarchar(max) NULL,
    [created_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT [non_op_entries_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [non_op_entries_minutes_check] CHECK ([minutes] >= 0),
    CONSTRAINT [non_op_entries_employee_id_fkey] FOREIGN KEY ([employee_id]) REFERENCES [employees] ([id]),
    CONSTRAINT [non_op_entries_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE,
    CONSTRAINT [non_op_entries_subcategory_id_fkey] FOREIGN KEY ([subcategory_id]) REFERENCES [non_op_subcategories] ([id])
);

CREATE TABLE [store_walk_notes] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [store_id] uniqueidentifier NOT NULL,
    [note] nvarchar(500) NOT NULL,
    [assigned_employee_id] uniqueidentifier NULL,
    [assigned_name] nvarchar(200) NULL,
    [raised_date] date NOT NULL,
    [created_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    [completed_at] datetimeoffset NULL,
    CONSTRAINT [store_walk_notes_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [store_walk_notes_note_check] CHECK (LEN(LTRIM(RTRIM([note]))) BETWEEN 1 AND 500),
    CONSTRAINT [store_walk_notes_assigned_employee_id_fkey] FOREIGN KEY ([assigned_employee_id]) REFERENCES [employees] ([id]),
    CONSTRAINT [store_walk_notes_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE
);

CREATE TABLE [weekend_markers] (
    [employee_id] uniqueidentifier NOT NULL,
    [weekend_start] date NOT NULL,
    [sat_off] bit NOT NULL DEFAULT CAST(0 AS bit),
    [sun_off] bit NOT NULL DEFAULT CAST(0 AS bit),
    CONSTRAINT [weekend_markers_pkey] PRIMARY KEY ([employee_id], [weekend_start]),
    CONSTRAINT [weekend_markers_employee_id_fkey] FOREIGN KEY ([employee_id]) REFERENCES [employees] ([id]) ON DELETE CASCADE
);

CREATE TABLE [actuals_days] (
    [store_id] uniqueidentifier NOT NULL,
    [date] date NOT NULL,
    [actual_sales] float NOT NULL DEFAULT 0.0E0,
    [actual_items] float NOT NULL DEFAULT 0.0E0,
    [actual_hours_100] float NOT NULL DEFAULT 0.0E0,
    [ukg_actual_hours] float NOT NULL DEFAULT 0.0E0,
    [batch_id] uniqueidentifier NULL,
    [lr_hours] float NULL,
    [cleaning_hours] float NULL,
    [specials_hours] float NULL,
    [reset_hours] float NULL,
    [admin_hours] float NULL,
    [checkout_hours] float NULL,
    [is_mhs] bit NULL,
    [has_esl] bit NULL,
    [has_baler] bit NULL,
    [actual_customers] float NULL,
    CONSTRAINT [actuals_days_pkey] PRIMARY KEY ([store_id], [date]),
    CONSTRAINT [actuals_days_batch_id_fkey] FOREIGN KEY ([batch_id]) REFERENCES [upload_batches] ([id]) ON DELETE SET NULL,
    CONSTRAINT [actuals_days_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE
);

CREATE TABLE [benchmark_days] (
    [store_id] uniqueidentifier NOT NULL,
    [date] date NOT NULL,
    [is_sco] bit NOT NULL DEFAULT CAST(0 AS bit),
    [sales_forecast] float NOT NULL DEFAULT 0.0E0,
    [items] float NOT NULL DEFAULT 0.0E0,
    [customer_count] float NOT NULL DEFAULT 0.0E0,
    [lr_hours_100] float NOT NULL DEFAULT 0.0E0,
    [cleaning_hours_100] float NOT NULL DEFAULT 0.0E0,
    [specials_hours_100] float NOT NULL DEFAULT 0.0E0,
    [reset_hours_100] float NOT NULL DEFAULT 0.0E0,
    [admin_hours_100] float NOT NULL DEFAULT 0.0E0,
    [registers_hours_100] float NOT NULL DEFAULT 0.0E0,
    [sco_hours_100] float NOT NULL DEFAULT 0.0E0,
    [checkout_hours_100] float NOT NULL DEFAULT 0.0E0,
    [lr_hours_90] AS [lr_hours_100] / 0.9 PERSISTED,
    [cleaning_hours_90] AS [cleaning_hours_100] / 0.9 PERSISTED,
    [specials_hours_90] AS [specials_hours_100] / 0.9 PERSISTED,
    [reset_hours_90] AS [reset_hours_100] / 0.9 PERSISTED,
    [admin_hours_90] AS [admin_hours_100] / 0.9 PERSISTED,
    [registers_hours_90] AS [registers_hours_100] / 0.9 PERSISTED,
    [sco_hours_90] AS [sco_hours_100] / 0.9 PERSISTED,
    [checkout_hours_90] AS [checkout_hours_100] / 0.9 PERSISTED,
    [employee_count] int NOT NULL DEFAULT 0,
    [is_mhs] bit NULL,
    [has_esl] bit NULL,
    [has_baler] bit NULL,
    [roster_period_id] uniqueidentifier NULL,
    [batch_id] uniqueidentifier NULL,
    CONSTRAINT [benchmark_days_pkey] PRIMARY KEY ([store_id], [date]),
    CONSTRAINT [benchmark_days_batch_id_fkey] FOREIGN KEY ([batch_id]) REFERENCES [upload_batches] ([id]) ON DELETE SET NULL,
    CONSTRAINT [benchmark_days_roster_period_id_fkey] FOREIGN KEY ([roster_period_id]) REFERENCES [roster_periods] ([id]) ON DELETE SET NULL,
    CONSTRAINT [benchmark_days_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE
);

CREATE TABLE [leave_liability_snapshots] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [store_id] uniqueidentifier NOT NULL,
    [period_end] date NOT NULL,
    [ukg_employee_id] nvarchar(64) NOT NULL,
    [ukg_location] nvarchar(64) NULL,
    [employee_name] nvarchar(200) NOT NULL,
    [job] nvarchar(200) NULL,
    [role_class] nvarchar(8) NOT NULL,
    [contract_hours] float NOT NULL DEFAULT 0.0E0,
    [notional_hours] float NOT NULL DEFAULT 0.0E0,
    [sick_hours] float NOT NULL DEFAULT 0.0E0,
    [annual_hours] float NOT NULL DEFAULT 0.0E0,
    [lieu_hours] float NOT NULL DEFAULT 0.0E0,
    [banked_hours] float NOT NULL DEFAULT 0.0E0,
    [banked2_hours] float NOT NULL DEFAULT 0.0E0,
    [batch_id] uniqueidentifier NULL,
    [created_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT [leave_liability_snapshots_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [leave_liability_snapshots_role_class_check] CHECK ([role_class] IN (N'SM', N'ASM', N'DSM', N'SA', N'CASUAL')),
    CONSTRAINT [leave_liability_snapshots_batch_id_fkey] FOREIGN KEY ([batch_id]) REFERENCES [upload_batches] ([id]) ON DELETE SET NULL,
    CONSTRAINT [leave_liability_snapshots_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE
);

CREATE TABLE [leave_requests_ukg] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [store_id] uniqueidentifier NOT NULL,
    [ukg_employee_id] nvarchar(64) NOT NULL,
    [employee_name] nvarchar(200) NOT NULL,
    [leave_type] nvarchar(200) NOT NULL,
    [start_date] date NOT NULL,
    [end_date] date NOT NULL,
    [status] nvarchar(64) NOT NULL,
    [approver] nvarchar(200) NULL,
    [batch_id] uniqueidentifier NULL,
    [created_at] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT [leave_requests_ukg_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [leave_requests_ukg_batch_id_fkey] FOREIGN KEY ([batch_id]) REFERENCES [upload_batches] ([id]) ON DELETE SET NULL,
    CONSTRAINT [leave_requests_ukg_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE
);

CREATE TABLE [pos_interval_stats] (
    [store_id] uniqueidentifier NOT NULL,
    [date] date NOT NULL,
    [block_time] time(0) NOT NULL,
    [mco_customers] float NOT NULL DEFAULT 0.0E0,
    [sco_customers] float NOT NULL DEFAULT 0.0E0,
    [mco_items] float NOT NULL DEFAULT 0.0E0,
    [sco_items] float NOT NULL DEFAULT 0.0E0,
    [batch_id] uniqueidentifier NULL,
    CONSTRAINT [pos_interval_stats_pkey] PRIMARY KEY ([store_id], [date], [block_time]),
    CONSTRAINT [pos_interval_stats_mco_customers_check] CHECK ([mco_customers] >= 0),
    CONSTRAINT [pos_interval_stats_mco_items_check] CHECK ([mco_items] >= 0),
    CONSTRAINT [pos_interval_stats_sco_customers_check] CHECK ([sco_customers] >= 0),
    CONSTRAINT [pos_interval_stats_sco_items_check] CHECK ([sco_items] >= 0),
    CONSTRAINT [pos_interval_stats_batch_id_fkey] FOREIGN KEY ([batch_id]) REFERENCES [upload_batches] ([id]) ON DELETE SET NULL,
    CONSTRAINT [pos_interval_stats_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE
);

CREATE TABLE [quarter_hour_allocation] (
    [store_id] uniqueidentifier NOT NULL,
    [day_of_week] int NOT NULL,
    [block_time] time(0) NOT NULL,
    [allocation] float NOT NULL,
    [batch_id] uniqueidentifier NULL,
    CONSTRAINT [quarter_hour_allocation_pkey] PRIMARY KEY ([store_id], [day_of_week], [block_time]),
    CONSTRAINT [quarter_hour_allocation_day_of_week_check] CHECK ([day_of_week] BETWEEN 0 AND 6),
    CONSTRAINT [quarter_hour_allocation_batch_id_fkey] FOREIGN KEY ([batch_id]) REFERENCES [upload_batches] ([id]) ON DELETE SET NULL,
    CONSTRAINT [quarter_hour_allocation_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE
);

CREATE TABLE [quarter_hour_segments] (
    [store_id] uniqueidentifier NOT NULL,
    [date] date NOT NULL,
    [block_time] time(0) NOT NULL,
    [segment] float NOT NULL DEFAULT 0.0E0,
    [roster_period_id] uniqueidentifier NULL,
    [batch_id] uniqueidentifier NULL,
    CONSTRAINT [quarter_hour_segments_pkey] PRIMARY KEY ([store_id], [date], [block_time]),
    CONSTRAINT [quarter_hour_segments_block_time_check] CHECK (DATEPART(MINUTE, [block_time]) % 15 = 0 AND DATEPART(SECOND, [block_time]) = 0),
    CONSTRAINT [quarter_hour_segments_segment_check] CHECK ([segment] >= 0),
    CONSTRAINT [quarter_hour_segments_batch_id_fkey] FOREIGN KEY ([batch_id]) REFERENCES [upload_batches] ([id]) ON DELETE SET NULL,
    CONSTRAINT [quarter_hour_segments_roster_period_id_fkey] FOREIGN KEY ([roster_period_id]) REFERENCES [roster_periods] ([id]) ON DELETE SET NULL,
    CONSTRAINT [quarter_hour_segments_store_id_fkey] FOREIGN KEY ([store_id]) REFERENCES [stores] ([id]) ON DELETE CASCADE
);

CREATE TABLE [shift_task_blocks] (
    [id] uniqueidentifier NOT NULL DEFAULT (NEWSEQUENTIALID()),
    [shift_id] uniqueidentifier NOT NULL,
    [block_time] time(0) NOT NULL,
    [labour_group] nvarchar(32) NOT NULL,
    [sub_task] nvarchar(32) NULL,
    CONSTRAINT [shift_task_blocks_pkey] PRIMARY KEY ([id]),
    CONSTRAINT [shift_task_blocks_shift_id_fkey] FOREIGN KEY ([shift_id]) REFERENCES [generated_shifts] ([id]) ON DELETE CASCADE
);

CREATE INDEX [actuals_days_batch_idx] ON [actuals_days] ([batch_id]);

CREATE INDEX [actuals_days_date_idx] ON [actuals_days] ([date]);

CREATE UNIQUE INDEX [admin_tasks_name_key] ON [admin_tasks] ([name]);

CREATE INDEX [app_users_area_idx] ON [app_users] ([area_id]);

CREATE INDEX [app_users_dg_idx] ON [app_users] ([director_group_id]);

CREATE UNIQUE INDEX [app_users_entra_object_id_key] ON [app_users] ([entra_object_id]) WHERE [entra_object_id] IS NOT NULL;

CREATE INDEX [app_users_region_idx] ON [app_users] ([region_id]);

CREATE INDEX [app_users_store_idx] ON [app_users] ([store_id]);

CREATE UNIQUE INDEX [app_users_user_principal_name_key] ON [app_users] ([user_principal_name]) WHERE [user_principal_name] IS NOT NULL;

CREATE UNIQUE INDEX [app_users_username_key] ON [app_users] ([username]);

CREATE UNIQUE INDEX [app_users_windows_account_key] ON [app_users] ([windows_account]) WHERE [windows_account] IS NOT NULL;

CREATE UNIQUE INDEX [areas_name_key] ON [areas] ([name]);

CREATE INDEX [areas_region_idx] ON [areas] ([region_id]);

CREATE INDEX [benchmark_days_batch_idx] ON [benchmark_days] ([batch_id]);

CREATE INDEX [benchmark_days_date_idx] ON [benchmark_days] ([date]);

CREATE INDEX [benchmark_days_period_idx] ON [benchmark_days] ([roster_period_id]);

CREATE INDEX [daily_loads_type_idx] ON [daily_loads] ([load_type_id]);

CREATE INDEX [day_off_requests_employee_idx] ON [day_off_requests] ([employee_id], [date]);

CREATE INDEX [day_plans_date_idx] ON [day_plans] ([date], [roster_status]);

CREATE INDEX [employee_leave_employee_idx] ON [employee_leave] ([employee_id], [start_date]);

CREATE INDEX [employees_position_idx] ON [employees] ([position_code]);

CREATE INDEX [employees_store_idx] ON [employees] ([store_id], [active]);

CREATE UNIQUE INDEX [employees_store_ukg_idx] ON [employees] ([store_id], [ukg_id]) WHERE [ukg_id] IS NOT NULL;

CREATE UNIQUE INDEX [feed_tokens_token_key] ON [feed_tokens] ([token]);

CREATE UNIQUE INDEX [fixed_rosters_employee_week_day_key] ON [fixed_rosters] ([employee_id], [week_index], [day_of_week]);

CREATE INDEX [generated_shifts_emp_idx] ON [generated_shifts] ([employee_id]);

CREATE INDEX [generated_shifts_locked_by_idx] ON [generated_shifts] ([locked_by]);

CREATE INDEX [generated_shifts_store_date_idx] ON [generated_shifts] ([store_id], [date]);

CREATE INDEX [labour_group_rules_day_idx] ON [labour_group_rules] ([day_of_week], [labour_group]);

CREATE UNIQUE INDEX [leave_liability_snapshots_store_id_period_end_ukg_employee_id_key] ON [leave_liability_snapshots] ([store_id], [period_end], [ukg_employee_id]);

CREATE INDEX [lls_batch_idx] ON [leave_liability_snapshots] ([batch_id]);

CREATE INDEX [lls_store_period_idx] ON [leave_liability_snapshots] ([store_id], [period_end]);

CREATE UNIQUE INDEX [leave_requests_ukg_store_id_ukg_employee_id_leave_type_start_date_end_date_key] ON [leave_requests_ukg] ([store_id], [ukg_employee_id], [leave_type], [start_date], [end_date]);

CREATE INDEX [lru_batch_idx] ON [leave_requests_ukg] ([batch_id]);

CREATE INDEX [lru_store_dates_idx] ON [leave_requests_ukg] ([store_id], [start_date], [end_date]);

CREATE UNIQUE INDEX [load_types_name_key] ON [load_types] ([name]);

CREATE INDEX [non_op_entries_emp_idx] ON [non_op_entries] ([employee_id]);

CREATE INDEX [non_op_entries_store_date_idx] ON [non_op_entries] ([store_id], [date]);

CREATE INDEX [non_op_entries_sub_idx] ON [non_op_entries] ([subcategory_id]);

CREATE INDEX [non_op_subcategories_cat_idx] ON [non_op_subcategories] ([category_id]);

CREATE INDEX [notifications_user_idx] ON [notifications] ([user_id], [read_at]);

CREATE INDEX [pay_rates_role_idx] ON [pay_rates] ([role]);

CREATE UNIQUE INDEX [plan_placement_rules_labour_group_key] ON [plan_placement_rules] ([labour_group]);

CREATE INDEX [plan_snapshots_by_idx] ON [plan_snapshots] ([generated_by]);

CREATE INDEX [pis_batch_idx] ON [pos_interval_stats] ([batch_id]);

CREATE UNIQUE INDEX [public_holidays_local_store_id_date_name_key] ON [public_holidays_local] ([store_id], [date], [name]);

CREATE UNIQUE INDEX [public_holidays_national_date_name_key] ON [public_holidays_national] ([date], [name]);

CREATE UNIQUE INDEX [public_holidays_state_state_date_name_key] ON [public_holidays_state] ([state], [date], [name]);

CREATE INDEX [qha_batch_idx] ON [quarter_hour_allocation] ([batch_id]);

CREATE INDEX [quarter_hour_segments_batch_idx] ON [quarter_hour_segments] ([batch_id]);

CREATE INDEX [quarter_hour_segments_period_idx] ON [quarter_hour_segments] ([roster_period_id]);

CREATE INDEX [regions_dg_idx] ON [regions] ([director_group_id]);

CREATE UNIQUE INDEX [reset_tasks_name_key] ON [reset_tasks] ([name]);

CREATE INDEX [roster_audit_store_idx] ON [roster_audit] ([store_id], [date]);

CREATE INDEX [roster_audit_user_idx] ON [roster_audit] ([user_id]);

CREATE UNIQUE INDEX [roster_periods_start_date_key] ON [roster_periods] ([start_date]);

CREATE INDEX [roster_review_checks_checked_by_idx] ON [roster_review_checks] ([checked_by]);

CREATE INDEX [roster_review_days_decided_by_idx] ON [roster_review_days] ([decided_by]);

CREATE INDEX [roster_review_events_actor_idx] ON [roster_review_events] ([actor_id]);

CREATE INDEX [roster_review_events_period_idx] ON [roster_review_events] ([store_id], [period_start], [created_at] DESC);

CREATE INDEX [roster_reviews_decided_by_idx] ON [roster_reviews] ([decided_by]);

CREATE INDEX [roster_reviews_submitted_by_idx] ON [roster_reviews] ([submitted_by]);

CREATE INDEX [shift_task_blocks_shift_idx] ON [shift_task_blocks] ([shift_id], [block_time]);

CREATE INDEX [special_trading_hours_store_idx] ON [special_trading_hours] ([store_id], [start_date]);

CREATE INDEX [store_attributes_pair_idx] ON [store_attributes] ([pair_store_id]);

CREATE INDEX [store_walk_notes_assigned_idx] ON [store_walk_notes] ([assigned_employee_id]);

CREATE INDEX [swn_open_idx] ON [store_walk_notes] ([store_id], [raised_date]) WHERE [completed_at] IS NULL;

CREATE INDEX [stores_area_idx] ON [stores] ([area_id]);

CREATE UNIQUE INDEX [stores_code_key] ON [stores] ([code]);

CREATE UNIQUE INDEX [stores_kiosk_token_key] ON [stores] ([kiosk_token]);

CREATE INDEX [stores_region_idx] ON [stores] ([region_id]);

CREATE UNIQUE INDEX [stores_slug_key] ON [stores] ([slug]);

CREATE INDEX [upload_batches_period_idx] ON [upload_batches] ([roster_period_id]);

CREATE INDEX [upload_batches_type_uploaded_idx] ON [upload_batches] ([type], [uploaded_at]);

CREATE INDEX [upload_batches_user_idx] ON [upload_batches] ([uploaded_by]);

CREATE INDEX [weekend_markers_weekend_idx] ON [weekend_markers] ([weekend_start]);

INSERT INTO [__ef_migrations_history] ([MigrationId], [ProductVersion])
VALUES (N'20261125062411_InitialCreate', N'10.0.15');

COMMIT;
GO

BEGIN TRANSACTION;
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'code', N'label', N'base_role', N'weekly_hours', N'year_band', N'has_rate', N'sort_order') AND [object_id] = OBJECT_ID(N'[positions]'))
    SET IDENTITY_INSERT [positions] ON;
INSERT INTO [positions] ([code], [label], [base_role], [weekly_hours], [year_band], [has_rate], [sort_order])
VALUES (N'JSA-16', N'JSA 16 year old', N'JSA', NULL, NULL, CAST(1 AS bit), 10),
(N'JSA-17', N'JSA 17 year old', N'JSA', NULL, NULL, CAST(1 AS bit), 20),
(N'SA-FF', N'Store Assistant Fully Flexible', N'SA', NULL, NULL, CAST(1 AS bit), 30),
(N'SA-LR', N'Store Assistant Limited Roster', N'SA', NULL, NULL, CAST(1 AS bit), 40),
(N'DSM', N'DSM', N'DSM', NULL, NULL, CAST(1 AS bit), 50),
(N'RSM', N'Relieving Shift Manager', N'RSM', NULL, NULL, CAST(0 AS bit), 60),
(N'ASM-30-1', N'ASM 30 hour year 1', N'ASM', 30.0E0, 1, CAST(1 AS bit), 111),
(N'ASM-30-2', N'ASM 30 hour year 2', N'ASM', 30.0E0, 2, CAST(1 AS bit), 112),
(N'ASM-32-1', N'ASM 32 hour year 1', N'ASM', 32.0E0, 1, CAST(1 AS bit), 121),
(N'ASM-32-2', N'ASM 32 hour year 2', N'ASM', 32.0E0, 2, CAST(1 AS bit), 122),
(N'ASM-35-1', N'ASM 35 hour year 1', N'ASM', 35.0E0, 1, CAST(1 AS bit), 131),
(N'ASM-35-2', N'ASM 35 hour year 2', N'ASM', 35.0E0, 2, CAST(1 AS bit), 132),
(N'ASM-40-1', N'ASM 40 hour year 1', N'ASM', 40.0E0, 1, CAST(1 AS bit), 141),
(N'ASM-40-2', N'ASM 40 hour year 2', N'ASM', 40.0E0, 2, CAST(1 AS bit), 142),
(N'ASM-45-1', N'ASM 45 hour year 1', N'ASM', 45.0E0, 1, CAST(1 AS bit), 151),
(N'ASM-45-2', N'ASM 45 hour year 2', N'ASM', 45.0E0, 2, CAST(1 AS bit), 152),
(N'SM-40-1', N'SM 40 hour year 1', N'SM', 40.0E0, 1, CAST(1 AS bit), 211),
(N'SM-40-2', N'SM 40 hour year 2', N'SM', 40.0E0, 2, CAST(1 AS bit), 212),
(N'SM-40-3', N'SM 40 hour year 3', N'SM', 40.0E0, 3, CAST(1 AS bit), 213),
(N'SM-40-4', N'SM 40 hour year 4', N'SM', 40.0E0, 4, CAST(1 AS bit), 214),
(N'SM-40-7', N'SM 40 hour year 7', N'SM', 40.0E0, 7, CAST(1 AS bit), 217),
(N'SM-45-1', N'SM 45 hour year 1', N'SM', 45.0E0, 1, CAST(1 AS bit), 221),
(N'SM-45-2', N'SM 45 hour year 2', N'SM', 45.0E0, 2, CAST(1 AS bit), 222),
(N'SM-45-3', N'SM 45 hour year 3', N'SM', 45.0E0, 3, CAST(1 AS bit), 223),
(N'SM-45-4', N'SM 45 hour year 4', N'SM', 45.0E0, 4, CAST(1 AS bit), 224),
(N'SM-45-7', N'SM 45 hour year 7', N'SM', 45.0E0, 7, CAST(1 AS bit), 227),
(N'SM-50-1', N'SM 50 hour year 1', N'SM', 50.0E0, 1, CAST(1 AS bit), 231),
(N'SM-50-2', N'SM 50 hour year 2', N'SM', 50.0E0, 2, CAST(1 AS bit), 232),
(N'SM-50-3', N'SM 50 hour year 3', N'SM', 50.0E0, 3, CAST(1 AS bit), 233),
(N'SM-50-4', N'SM 50 hour year 4', N'SM', 50.0E0, 4, CAST(1 AS bit), 234),
(N'SM-50-7', N'SM 50 hour year 7', N'SM', 50.0E0, 7, CAST(1 AS bit), 237);
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'code', N'label', N'base_role', N'weekly_hours', N'year_band', N'has_rate', N'sort_order') AND [object_id] = OBJECT_ID(N'[positions]'))
    SET IDENTITY_INSERT [positions] OFF;

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'name', N'minutes_per_pallet', N'color', N'sort_order', N'minutes_per_pallet_lt90', N'minutes_per_pallet_lt60', N'minutes_per_pallet_lt30') AND [object_id] = OBJECT_ID(N'[load_types]'))
    SET IDENTITY_INSERT [load_types] ON;
INSERT INTO [load_types] ([name], [minutes_per_pallet], [color], [sort_order], [minutes_per_pallet_lt90], [minutes_per_pallet_lt60], [minutes_per_pallet_lt30])
VALUES (N'Ambient', 19.0E0, N'#2563eb', 1, 20.899999999999999E0, 23.800000000000001E0, 26.600000000000001E0),
(N'Long Life', 28.0E0, N'#7c3aed', 2, 30.800000000000001E0, 35.0E0, 39.200000000000003E0),
(N'Short Life', 20.0E0, N'#059669', 3, 22.0E0, 25.0E0, 28.0E0),
(N'Freezer', 28.0E0, N'#0891b2', 4, 30.800000000000001E0, 35.0E0, 39.200000000000003E0),
(N'Bread', 17.0E0, N'#d97706', 5, 18.699999999999999E0, 21.300000000000001E0, 23.800000000000001E0),
(N'Produce', 10.0E0, N'#16a34a', 6, 11.0E0, 12.5E0, 14.0E0),
(N'Bulky', 5.0E0, N'#dc2626', 7, 5.5E0, 6.2999999999999998E0, 7.0E0),
(N'Freeze to Thaw', 11.0E0, N'#0d9488', 8, 12.1E0, 13.800000000000001E0, 15.4E0);
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'name', N'minutes_per_pallet', N'color', N'sort_order', N'minutes_per_pallet_lt90', N'minutes_per_pallet_lt60', N'minutes_per_pallet_lt30') AND [object_id] = OBJECT_ID(N'[load_types]'))
    SET IDENTITY_INSERT [load_types] OFF;

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'name', N'sort') AND [object_id] = OBJECT_ID(N'[non_op_categories]'))
    SET IDENTITY_INSERT [non_op_categories] ON;
INSERT INTO [non_op_categories] ([name], [sort])
VALUES (N'Meetings', 1),
(N'Merchandising', 2),
(N'Miscellaneous', 3),
(N'Cleaning', 4),
(N'Training', 5);
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'name', N'sort') AND [object_id] = OBJECT_ID(N'[non_op_categories]'))
    SET IDENTITY_INSERT [non_op_categories] OFF;

INSERT INTO [non_op_subcategories] ([category_id], [name], [min_minutes], [max_minutes], [days_allowed], [default_minutes], [note])
SELECT c.[id], v.[name], NULL, v.[max_minutes], N'[0,1,2,3,4,5,6]', v.[default_minutes], v.[note]
FROM (VALUES
    (N'Meetings', N'Store Manager Meetings', 120, 120, NULL),
    (N'Meetings', N'Performance Appraisal Preparation (JSA, SA, RSM)', 5, 5, NULL),
    (N'Meetings', N'Performance Appraisal Preparation (DSM, ASM, SM)', 30, 30, NULL),
    (N'Meetings', N'Performance Appraisal Completion (JSA, SA, RSM)', 15, 15, NULL),
    (N'Meetings', N'Performance Appraisal Completion (DSM, ASM, SM)', 60, 60, NULL),
    (N'Meetings', N'Grievance Investigations', NULL, NULL, N'As approved by EMSO'),
    (N'Meetings', N'Career Conversations (Employee)', 30, 30, NULL),
    (N'Meetings', N'Career Conversations (Leader)', 30, 30, NULL),
    (N'Meetings', N'Performance Development Plan (Employee)', 30, 30, NULL),
    (N'Meetings', N'Performance Development Plan (Leader)', 30, 30, NULL),
    (N'Meetings', N'Liquor Accord', NULL, 60, NULL),
    (N'Meetings', N'SM Interview Completion', 60, 60, NULL),
    (N'Meetings', N'SM 1:1 Interview Completion', 30, 30, NULL),
    (N'Meetings', N'30/60/90 Review (Employee)', 30, 30, NULL),
    (N'Meetings', N'30/60/90 Review (Leader)', 30, 30, NULL),
    (N'Meetings', N'End of Probation: Personnel Leader Catch Up', 15, 15, NULL),
    (N'Meetings', N'30/60/90 Position Change Review (Employee)', 30, 30, NULL),
    (N'Meetings', N'30/60/90 Position Change Review (Leader)', 30, 30, NULL),
    (N'Meetings', N'Position Change: Personnel Leader Catch Up', 15, 15, NULL),
    (N'Merchandising', N'Monthly Product Launch', NULL, NULL, N'As determined by Merchandising'),
    (N'Merchandising', N'Quarterly Remerches', NULL, NULL, N'As determined by Merchandising'),
    (N'Merchandising', N'Yearly Retorfit Program (MSR)', NULL, NULL, N'As approved by National Merchandising Working Group'),
    (N'Merchandising', N'Store Refresh or Extension', NULL, NULL, N'As determined by Refresh Guide'),
    (N'Merchandising', N'Merchndising Trials', NULL, NULL, N'As determined by Merchandising'),
    (N'Merchandising', N'Seasonal POS Install', NULL, NULL, N'As determined by Merchandising'),
    (N'Merchandising', N'Grand Openings', NULL, NULL, N'As determined by Grand Opening Guide'),
    (N'Merchandising', N'Seasonal Remerch', NULL, NULL, N'As determined by Merchandising'),
    (N'Miscellaneous', N'Travel Time', NULL, NULL, N'As per travel policy'),
    (N'Miscellaneous', N'Alarm Callouts', 180, 540, NULL),
    (N'Miscellaneous', N'Power Outages', NULL, NULL, N'As approved by EMSO'),
    (N'Miscellaneous', N'Store Evacuations', NULL, NULL, N'As approved by EMSO'),
    (N'Miscellaneous', N'Fire Drills (per person)', 15, 15, NULL),
    (N'Miscellaneous', N'Quarterly H&S Audits and Regional Safety Day', 120, 120, NULL),
    (N'Miscellaneous', N'Seasonal Process', NULL, NULL, N'As approved by DSO'),
    (N'Miscellaneous', N'EA Update', NULL, NULL, N'As approved by DSO'),
    (N'Miscellaneous', N'Broken Compactor', 15, 60, NULL),
    (N'Miscellaneous', N'Capacity Constraints', NULL, NULL, N'As approved by DSO'),
    (N'Cleaning', N'Monthly', NULL, NULL, N'Per approved cleaning time note'),
    (N'Cleaning', N'Bi-monthly', NULL, NULL, N'Per approved cleaning time note'),
    (N'Cleaning', N'Quarterly', NULL, NULL, N'Per approved cleaning time note'),
    (N'Cleaning', N'Biannual or Annual', NULL, NULL, N'Per approved cleaning time note'),
    (N'Training', N'JSA Pathway (Day 1)', 330, 330, NULL),
    (N'Training', N'JSA Pathway (Day 2)', 315, 315, NULL),
    (N'Training', N'JSA Pathway (Day 3)', 495, 495, NULL),
    (N'Training', N'Store Assistant Pathway (Day 1)', 360, 360, NULL),
    (N'Training', N'Store Assistant Pathway (Day 2)', 270, 270, NULL),
    (N'Training', N'Store Assistant Pathway (Day 3)', 540, 540, NULL),
    (N'Training', N'Store Assistant Pathway (Day 4)', 420, 420, NULL),
    (N'Training', N'RSM Pathway (Module 1)', 180, 180, NULL),
    (N'Training', N'RSM Pathway (Module 2)', 180, 180, NULL),
    (N'Training', N'DSM Pathway', 72, 72, NULL),
    (N'Training', N'ASM Pathway', 103, 103, NULL),
    (N'Training', N'Future Leader Program', NULL, NULL, N'As per national agenda'),
    (N'Training', N'Store Manager Pathway', NULL, NULL, NULL),
    (N'Training', N'Quarterly Training', NULL, NULL, N'As per Academy NWCP Guide'),
    (N'Training', N'Head Office Training', NULL, NULL, N'As per agenda'),
    (N'Training', N'EMSO Store Training', NULL, NULL, N'As approved by DSO')
) AS v ([category], [name], [default_minutes], [max_minutes], [note])
JOIN [non_op_categories] c ON c.[name] = v.[category];

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'name', N'schedule_type', N'day_of_week', N'nth_week', N'region_days', N'duration_type', N'fixed_minutes', N'minutes_per_unit', N'min_minutes', N'window_start_kind', N'window_start_time', N'window_start_offset_min', N'window_end_kind', N'window_end_time', N'window_end_offset_min', N'sort_order') AND [object_id] = OBJECT_ID(N'[admin_tasks]'))
    SET IDENTITY_INSERT [admin_tasks] ON;
INSERT INTO [admin_tasks] ([name], [schedule_type], [day_of_week], [nth_week], [region_days], [duration_type], [fixed_minutes], [minutes_per_unit], [min_minutes], [window_start_kind], [window_start_time], [window_start_offset_min], [window_end_kind], [window_end_time], [window_end_offset_min], [sort_order])
VALUES (N'APM', N'weekly', 0, NULL, NULL, N'fixed', 15.0E0, NULL, NULL, N'clock', '09:00:00', 0, N'clock', '12:00:00', 0, 10),
(N'End of Day', N'daily', NULL, NULL, NULL, N'fixed', 17.0E0, NULL, NULL, N'close', NULL, 0, N'none', NULL, 0, 20),
(N'Comms Overhaul', N'weekly', 0, NULL, NULL, N'fixed', 30.0E0, NULL, NULL, N'clock', '12:00:00', 0, N'clock', '15:00:00', 0, 30),
(N'Fresh Ordering', N'daily', NULL, NULL, NULL, N'fixed', 150.0E0, NULL, NULL, N'close', NULL, -210, N'close', NULL, 0, 40),
(N'Longlife Date Checks', N'weekly', 0, NULL, NULL, N'fixed', 120.0E0, NULL, NULL, N'none', NULL, 0, N'clock', '08:30:00', 0, 50),
(N'Non BAU STM Tasks', N'weekly', 0, NULL, NULL, N'fixed', 60.0E0, NULL, NULL, N'none', NULL, 0, N'none', NULL, 0, 60),
(N'Parameter Deviation & Custom Bays', N'weekly', 0, NULL, NULL, N'fixed', 15.0E0, NULL, NULL, N'none', NULL, 0, N'clock', '13:00:00', 0, 70),
(N'STM Date Check', N'daily', NULL, NULL, NULL, N'fixed', 5.0E0, NULL, NULL, N'clock', '17:00:00', 0, N'none', NULL, 0, 80),
(N'Safe Handover', N'weekly', 4, NULL, NULL, N'fixed', 14.0E0, NULL, NULL, N'clock', '09:00:00', 0, N'none', NULL, 0, 90),
(N'Till Calibration', N'weekly', NULL, NULL, N'{"501":0,"505":0,"506":0,"504":1,"507":1,"509":1,"503":3,"502":3}', N'till_calibration', NULL, NULL, NULL, N'none', NULL, 0, N'none', NULL, 0, 100),
(N'UKG End of Day', N'daily', NULL, NULL, NULL, N'fixed', 6.0E0, NULL, NULL, N'close', NULL, 0, N'none', NULL, 0, 110),
(N'Use By Date Check', N'daily', NULL, NULL, NULL, N'fixed', 18.0E0, NULL, NULL, N'clock', '17:00:00', 0, N'none', NULL, 0, 120),
(N'CGOS Ordering', N'daily', NULL, NULL, NULL, N'fixed', 58.5E0, NULL, NULL, N'clock', '13:00:00', 0, N'clock', '16:00:00', 0, 130),
(N'EMSO Meeting', N'monthly_nth', 1, 3, NULL, N'fixed', 60.0E0, NULL, NULL, N'none', NULL, 0, N'none', NULL, 0, 140),
(N'UKG End of PayPeriod', N'planning_week', 0, NULL, NULL, N'per_employee', NULL, 1.3200000000000001E0, 20.0E0, N'none', NULL, 0, N'clock', '10:00:00', 0, 150),
(N'UKG Rostering', N'planning_week', 1, NULL, NULL, N'per_employee', NULL, 3.1600000000000001E0, 48.0E0, N'none', NULL, 0, N'none', NULL, 0, 160),
(N'Specials Inventory Management', N'monthly_nth', 1, 2, NULL, N'fixed', 210.0E0, NULL, NULL, N'none', NULL, 0, N'none', NULL, 0, 170),
(N'MyWalk', N'monthly_nth', 1, 2, NULL, N'fixed', 45.0E0, NULL, NULL, N'none', NULL, 0, N'none', NULL, 0, 180),
(N'Register Assignment', N'daily', NULL, NULL, NULL, N'per_register', NULL, 3.0E0, NULL, N'none', NULL, 0, N'open', NULL, 0, 190),
(N'Register Cash-Up', N'daily', NULL, NULL, NULL, N'per_register', NULL, 5.0E0, NULL, N'close', NULL, -30, N'none', NULL, 0, 200),
(N'Register Date Checks', N'monthly_nth', 1, 3, NULL, N'fixed', 45.0E0, NULL, NULL, N'none', NULL, 0, N'none', NULL, 0, 210),
(N'EMSO Meeting Prep', N'monthly_nth', 0, 3, NULL, N'fixed', 60.0E0, NULL, NULL, N'none', NULL, 0, N'none', NULL, 0, 220),
(N'SPi Planning', N'planning_week', 1, NULL, NULL, N'per_employee', NULL, 1.79E0, 27.0E0, N'none', NULL, 0, N'none', NULL, 0, 230),
(N'Specials Price Card Audit', N'weekly', 1, NULL, NULL, N'fixed', 4.0E0, NULL, NULL, N'none', NULL, 0, N'none', NULL, 0, 240);
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'name', N'schedule_type', N'day_of_week', N'nth_week', N'region_days', N'duration_type', N'fixed_minutes', N'minutes_per_unit', N'min_minutes', N'window_start_kind', N'window_start_time', N'window_start_offset_min', N'window_end_kind', N'window_end_time', N'window_end_offset_min', N'sort_order') AND [object_id] = OBJECT_ID(N'[admin_tasks]'))
    SET IDENTITY_INSERT [admin_tasks] OFF;

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'name', N'share_pct', N'window_start_kind', N'window_start_time', N'window_start_offset_min', N'window_end_kind', N'window_end_time', N'window_end_offset_min', N'sort_order') AND [object_id] = OBJECT_ID(N'[reset_tasks]'))
    SET IDENTITY_INSERT [reset_tasks] ON;
INSERT INTO [reset_tasks] ([name], [share_pct], [window_start_kind], [window_start_time], [window_start_offset_min], [window_end_kind], [window_end_time], [window_end_offset_min], [sort_order])
VALUES (N'Re-run 1', 20.0E0, N'clock', '10:00:00', 0, N'clock', '14:00:00', 0, 10),
(N'Re-run 2', 20.0E0, N'clock', '14:00:00', 0, N'clock', '18:00:00', 0, 20),
(N'First declare (light)', 15.0E0, N'clock', '10:00:00', 0, N'clock', '14:00:00', 0, 30),
(N'Full decard', NULL, N'close', NULL, -150, N'close', NULL, 0, 40);
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'name', N'share_pct', N'window_start_kind', N'window_start_time', N'window_start_offset_min', N'window_end_kind', N'window_end_time', N'window_end_offset_min', N'sort_order') AND [object_id] = OBJECT_ID(N'[reset_tasks]'))
    SET IDENTITY_INSERT [reset_tasks] OFF;

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'id') AND [object_id] = OBJECT_ID(N'[plan_placement_settings]'))
    SET IDENTITY_INSERT [plan_placement_settings] ON;
INSERT INTO [plan_placement_settings] ([id])
VALUES (CAST(1 AS bit));
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'id') AND [object_id] = OBJECT_ID(N'[plan_placement_settings]'))
    SET IDENTITY_INSERT [plan_placement_settings] OFF;

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'id') AND [object_id] = OBJECT_ID(N'[variance_settings]'))
    SET IDENTITY_INSERT [variance_settings] ON;
INSERT INTO [variance_settings] ([id])
VALUES (CAST(1 AS bit));
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'id') AND [object_id] = OBJECT_ID(N'[variance_settings]'))
    SET IDENTITY_INSERT [variance_settings] OFF;

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'id') AND [object_id] = OBJECT_ID(N'[leave_settings]'))
    SET IDENTITY_INSERT [leave_settings] ON;
INSERT INTO [leave_settings] ([id])
VALUES (CAST(1 AS bit));
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'id') AND [object_id] = OBJECT_ID(N'[leave_settings]'))
    SET IDENTITY_INSERT [leave_settings] OFF;

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'contract_hours', N'role_class', N'leave_notional_hours', N'accrual_weeks', N'annual_accrual_hours') AND [object_id] = OBJECT_ID(N'[leave_accrual_bands]'))
    SET IDENTITY_INSERT [leave_accrual_bands] ON;
INSERT INTO [leave_accrual_bands] ([contract_hours], [role_class], [leave_notional_hours], [accrual_weeks], [annual_accrual_hours])
VALUES (100.0E0, N'SM', 10.0E0, 5.0E0, 250.0E0),
(90.0E0, N'SM', 9.0E0, 5.0E0, 225.0E0),
(80.0E0, N'SM', 8.75E0, 5.0E0, 218.75E0),
(90.0E0, N'ASM', 9.0E0, 5.0E0, 225.0E0),
(80.0E0, N'ASM', 8.75E0, 5.0E0, 218.75E0),
(60.0E0, N'ASM', 6.0E0, 5.0E0, 150.0E0),
(70.0E0, N'DSM', 7.5E0, 5.0E0, 187.5E0),
(60.0E0, N'DSM', 7.5E0, 5.0E0, 187.5E0),
(70.0E0, N'SA', 7.25E0, 4.0E0, 145.0E0),
(60.0E0, N'SA', 6.5E0, 4.0E0, 130.0E0),
(55.0E0, N'SA', 6.0E0, 4.0E0, 120.0E0),
(50.0E0, N'SA', 5.5E0, 4.0E0, 110.0E0),
(40.0E0, N'SA', 5.25E0, 4.0E0, 80.0E0),
(30.0E0, N'SA', 4.75E0, 4.0E0, 60.0E0),
(21.0E0, N'SA', 4.0E0, 4.0E0, 42.0E0);
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'contract_hours', N'role_class', N'leave_notional_hours', N'accrual_weeks', N'annual_accrual_hours') AND [object_id] = OBJECT_ID(N'[leave_accrual_bands]'))
    SET IDENTITY_INSERT [leave_accrual_bands] OFF;

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'labour_group', N'max_days', N'uplift_pct') AND [object_id] = OBJECT_ID(N'[probation_buffers]'))
    SET IDENTITY_INSERT [probation_buffers] ON;
INSERT INTO [probation_buffers] ([labour_group], [max_days], [uplift_pct])
VALUES (N'*', 30, 40.0E0),
(N'*', 60, 25.0E0),
(N'*', 90, 10.0E0);
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'labour_group', N'max_days', N'uplift_pct') AND [object_id] = OBJECT_ID(N'[probation_buffers]'))
    SET IDENTITY_INSERT [probation_buffers] OFF;

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'key', N'value', N'note') AND [object_id] = OBJECT_ID(N'[rostering_rules]'))
    SET IDENTITY_INSERT [rostering_rules] ON;
INSERT INTO [rostering_rules] ([key], [value], [note])
VALUES (N'paidBreakFactorPct', 4.5E0, N'Paid break allowance added to actual hours @100% before OE is measured (Labour Group Rules -> Breaks).');
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'key', N'value', N'note') AND [object_id] = OBJECT_ID(N'[rostering_rules]'))
    SET IDENTITY_INSERT [rostering_rules] OFF;

INSERT INTO [__ef_migrations_history] ([MigrationId], [ProductVersion])
VALUES (N'20261125210404_ReferenceData', N'10.0.15');

COMMIT;
GO

