// Port of the reference rows the Postgres migrations insert, exactly as they leave them:
// 0002 load_types (+ 0032's tenure-band minutes), 0018 the non-op catalogue, 0019
// admin_tasks (+ 0046's windows), 0020 positions, 0021 plan_placement_settings, 0022
// variance_settings, 0028 leave_accrual_bands and leave_settings, 0030 probation_buffers,
// 0031 rostering_rules paidBreakFactorPct and 0038 reset_tasks. Columns the Postgres
// inserts left out take their column defaults here too. Seed/ReferenceData.cs holds the
// same rows for databases built without the migrations (the SQLite unit tests); a test
// keeps the two in step.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Roster.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReferenceData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 0020: the positions catalogue (also the pay_rates key set).
            migrationBuilder.InsertData(
                table: "positions",
                columns: new[] { "code", "label", "base_role", "weekly_hours", "year_band", "has_rate", "sort_order" },
                values: new object[,]
                {
                    { "JSA-16", "JSA 16 year old", "JSA", null, null, true, 10 },
                    { "JSA-17", "JSA 17 year old", "JSA", null, null, true, 20 },
                    { "SA-FF", "Store Assistant Fully Flexible", "SA", null, null, true, 30 },
                    { "SA-LR", "Store Assistant Limited Roster", "SA", null, null, true, 40 },
                    { "DSM", "DSM", "DSM", null, null, true, 50 },
                    { "RSM", "Relieving Shift Manager", "RSM", null, null, false, 60 },
                    { "ASM-30-1", "ASM 30 hour year 1", "ASM", 30.0, 1, true, 111 },
                    { "ASM-30-2", "ASM 30 hour year 2", "ASM", 30.0, 2, true, 112 },
                    { "ASM-32-1", "ASM 32 hour year 1", "ASM", 32.0, 1, true, 121 },
                    { "ASM-32-2", "ASM 32 hour year 2", "ASM", 32.0, 2, true, 122 },
                    { "ASM-35-1", "ASM 35 hour year 1", "ASM", 35.0, 1, true, 131 },
                    { "ASM-35-2", "ASM 35 hour year 2", "ASM", 35.0, 2, true, 132 },
                    { "ASM-40-1", "ASM 40 hour year 1", "ASM", 40.0, 1, true, 141 },
                    { "ASM-40-2", "ASM 40 hour year 2", "ASM", 40.0, 2, true, 142 },
                    { "ASM-45-1", "ASM 45 hour year 1", "ASM", 45.0, 1, true, 151 },
                    { "ASM-45-2", "ASM 45 hour year 2", "ASM", 45.0, 2, true, 152 },
                    { "SM-40-1", "SM 40 hour year 1", "SM", 40.0, 1, true, 211 },
                    { "SM-40-2", "SM 40 hour year 2", "SM", 40.0, 2, true, 212 },
                    { "SM-40-3", "SM 40 hour year 3", "SM", 40.0, 3, true, 213 },
                    { "SM-40-4", "SM 40 hour year 4", "SM", 40.0, 4, true, 214 },
                    { "SM-40-7", "SM 40 hour year 7", "SM", 40.0, 7, true, 217 },
                    { "SM-45-1", "SM 45 hour year 1", "SM", 45.0, 1, true, 221 },
                    { "SM-45-2", "SM 45 hour year 2", "SM", 45.0, 2, true, 222 },
                    { "SM-45-3", "SM 45 hour year 3", "SM", 45.0, 3, true, 223 },
                    { "SM-45-4", "SM 45 hour year 4", "SM", 45.0, 4, true, 224 },
                    { "SM-45-7", "SM 45 hour year 7", "SM", 45.0, 7, true, 227 },
                    { "SM-50-1", "SM 50 hour year 1", "SM", 50.0, 1, true, 231 },
                    { "SM-50-2", "SM 50 hour year 2", "SM", 50.0, 2, true, 232 },
                    { "SM-50-3", "SM 50 hour year 3", "SM", 50.0, 3, true, 233 },
                    { "SM-50-4", "SM 50 hour year 4", "SM", 50.0, 4, true, 234 },
                    { "SM-50-7", "SM 50 hour year 7", "SM", 50.0, 7, true, 237 },
                });

            // 0002: the load types, with 0032's tenure-band minutes (round(minutes x 1.10 / 1.25 / 1.40, 1)).
            migrationBuilder.InsertData(
                table: "load_types",
                columns: new[] { "name", "minutes_per_pallet", "color", "sort_order", "minutes_per_pallet_lt90", "minutes_per_pallet_lt60", "minutes_per_pallet_lt30" },
                values: new object[,]
                {
                    { "Ambient", 19.0, "#2563eb", 1, 20.9, 23.8, 26.6 },
                    { "Long Life", 28.0, "#7c3aed", 2, 30.8, 35.0, 39.2 },
                    { "Short Life", 20.0, "#059669", 3, 22.0, 25.0, 28.0 },
                    { "Freezer", 28.0, "#0891b2", 4, 30.8, 35.0, 39.2 },
                    { "Bread", 17.0, "#d97706", 5, 18.7, 21.3, 23.8 },
                    { "Produce", 10.0, "#16a34a", 6, 11.0, 12.5, 14.0 },
                    { "Bulky", 5.0, "#dc2626", 7, 5.5, 6.3, 7.0 },
                    { "Freeze to Thaw", 11.0, "#0d9488", 8, 12.1, 13.8, 15.4 },
                });

            // 0018: the non-op catalogue - five categories and the sheet's sub-categories, every day allowed.
            migrationBuilder.InsertData(
                table: "non_op_categories",
                columns: new[] { "name", "sort" },
                values: new object[,]
                {
                    { "Meetings", 1 },
                    { "Merchandising", 2 },
                    { "Miscellaneous", 3 },
                    { "Cleaning", 4 },
                    { "Training", 5 },
                });
            migrationBuilder.Sql("""
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
                """);

            // 0019: the administration task catalogue, with 0046's windows (End of Day, Fresh Ordering, Register Cash-Up).
            migrationBuilder.InsertData(
                table: "admin_tasks",
                columns: new[] { "name", "schedule_type", "day_of_week", "nth_week", "region_days", "duration_type", "fixed_minutes", "minutes_per_unit", "min_minutes", "window_start_kind", "window_start_time", "window_start_offset_min", "window_end_kind", "window_end_time", "window_end_offset_min", "sort_order" },
                values: new object[,]
                {
                    { "APM", "weekly", 0, null, null, "fixed", 15.0, null, null, "clock", new TimeOnly(9, 0), 0, "clock", new TimeOnly(12, 0), 0, 10 },
                    { "End of Day", "daily", null, null, null, "fixed", 17.0, null, null, "close", null, 0, "none", null, 0, 20 },
                    { "Comms Overhaul", "weekly", 0, null, null, "fixed", 30.0, null, null, "clock", new TimeOnly(12, 0), 0, "clock", new TimeOnly(15, 0), 0, 30 },
                    { "Fresh Ordering", "daily", null, null, null, "fixed", 150.0, null, null, "close", null, -210, "close", null, 0, 40 },
                    { "Longlife Date Checks", "weekly", 0, null, null, "fixed", 120.0, null, null, "none", null, 0, "clock", new TimeOnly(8, 30), 0, 50 },
                    { "Non BAU STM Tasks", "weekly", 0, null, null, "fixed", 60.0, null, null, "none", null, 0, "none", null, 0, 60 },
                    { "Parameter Deviation & Custom Bays", "weekly", 0, null, null, "fixed", 15.0, null, null, "none", null, 0, "clock", new TimeOnly(13, 0), 0, 70 },
                    { "STM Date Check", "daily", null, null, null, "fixed", 5.0, null, null, "clock", new TimeOnly(17, 0), 0, "none", null, 0, 80 },
                    { "Safe Handover", "weekly", 4, null, null, "fixed", 14.0, null, null, "clock", new TimeOnly(9, 0), 0, "none", null, 0, 90 },
                    { "Till Calibration", "weekly", null, null, "{\"501\":0,\"505\":0,\"506\":0,\"504\":1,\"507\":1,\"509\":1,\"503\":3,\"502\":3}", "till_calibration", null, null, null, "none", null, 0, "none", null, 0, 100 },
                    { "UKG End of Day", "daily", null, null, null, "fixed", 6.0, null, null, "close", null, 0, "none", null, 0, 110 },
                    { "Use By Date Check", "daily", null, null, null, "fixed", 18.0, null, null, "clock", new TimeOnly(17, 0), 0, "none", null, 0, 120 },
                    { "CGOS Ordering", "daily", null, null, null, "fixed", 58.5, null, null, "clock", new TimeOnly(13, 0), 0, "clock", new TimeOnly(16, 0), 0, 130 },
                    { "EMSO Meeting", "monthly_nth", 1, 3, null, "fixed", 60.0, null, null, "none", null, 0, "none", null, 0, 140 },
                    { "UKG End of PayPeriod", "planning_week", 0, null, null, "per_employee", null, 1.32, 20.0, "none", null, 0, "clock", new TimeOnly(10, 0), 0, 150 },
                    { "UKG Rostering", "planning_week", 1, null, null, "per_employee", null, 3.16, 48.0, "none", null, 0, "none", null, 0, 160 },
                    { "Specials Inventory Management", "monthly_nth", 1, 2, null, "fixed", 210.0, null, null, "none", null, 0, "none", null, 0, 170 },
                    { "MyWalk", "monthly_nth", 1, 2, null, "fixed", 45.0, null, null, "none", null, 0, "none", null, 0, 180 },
                    { "Register Assignment", "daily", null, null, null, "per_register", null, 3.0, null, "none", null, 0, "open", null, 0, 190 },
                    { "Register Cash-Up", "daily", null, null, null, "per_register", null, 5.0, null, "close", null, -30, "none", null, 0, 200 },
                    { "Register Date Checks", "monthly_nth", 1, 3, null, "fixed", 45.0, null, null, "none", null, 0, "none", null, 0, 210 },
                    { "EMSO Meeting Prep", "monthly_nth", 0, 3, null, "fixed", 60.0, null, null, "none", null, 0, "none", null, 0, 220 },
                    { "SPi Planning", "planning_week", 1, null, null, "per_employee", null, 1.79, 27.0, "none", null, 0, "none", null, 0, 230 },
                    { "Specials Price Card Audit", "weekly", 1, null, null, "fixed", 4.0, null, null, "none", null, 0, "none", null, 0, 240 },
                });

            // 0038: the reset task catalogue (Full decard from 2.5 hours before close).
            migrationBuilder.InsertData(
                table: "reset_tasks",
                columns: new[] { "name", "share_pct", "window_start_kind", "window_start_time", "window_start_offset_min", "window_end_kind", "window_end_time", "window_end_offset_min", "sort_order" },
                values: new object[,]
                {
                    { "Re-run 1", 20.0, "clock", new TimeOnly(10, 0), 0, "clock", new TimeOnly(14, 0), 0, 10 },
                    { "Re-run 2", 20.0, "clock", new TimeOnly(14, 0), 0, "clock", new TimeOnly(18, 0), 0, 20 },
                    { "First declare (light)", 15.0, "clock", new TimeOnly(10, 0), 0, "clock", new TimeOnly(14, 0), 0, 30 },
                    { "Full decard", null, "close", null, -150, "close", null, 0, 40 },
                });

            // 0021, 0022, 0028: the single settings rows, every column at its default.
            migrationBuilder.InsertData(table: "plan_placement_settings", column: "id", value: true);
            migrationBuilder.InsertData(table: "variance_settings", column: "id", value: true);
            migrationBuilder.InsertData(table: "leave_settings", column: "id", value: true);

            // 0028: leave accrual bands (the workbook's Setup sheet).
            migrationBuilder.InsertData(
                table: "leave_accrual_bands",
                columns: new[] { "contract_hours", "role_class", "leave_notional_hours", "accrual_weeks", "annual_accrual_hours" },
                values: new object[,]
                {
                    { 100.0, "SM", 10.0, 5.0, 250.0 },
                    { 90.0, "SM", 9.0, 5.0, 225.0 },
                    { 80.0, "SM", 8.75, 5.0, 218.75 },
                    { 90.0, "ASM", 9.0, 5.0, 225.0 },
                    { 80.0, "ASM", 8.75, 5.0, 218.75 },
                    { 60.0, "ASM", 6.0, 5.0, 150.0 },
                    { 70.0, "DSM", 7.5, 5.0, 187.5 },
                    { 60.0, "DSM", 7.5, 5.0, 187.5 },
                    { 70.0, "SA", 7.25, 4.0, 145.0 },
                    { 60.0, "SA", 6.5, 4.0, 130.0 },
                    { 55.0, "SA", 6.0, 4.0, 120.0 },
                    { 50.0, "SA", 5.5, 4.0, 110.0 },
                    { 40.0, "SA", 5.25, 4.0, 80.0 },
                    { 30.0, "SA", 4.75, 4.0, 60.0 },
                    { 21.0, "SA", 4.0, 4.0, 42.0 },
                });

            // 0030: probation buffers (superseded by the load types' tenure minutes; kept for reference).
            migrationBuilder.InsertData(
                table: "probation_buffers",
                columns: new[] { "labour_group", "max_days", "uplift_pct" },
                values: new object[,]
                {
                    { "*", 30, 40.0 },
                    { "*", 60, 25.0 },
                    { "*", 90, 10.0 },
                });

            // 0031: the paid break allowance, seeded so it is visible in the rules catalogue.
            migrationBuilder.InsertData(
                table: "rostering_rules",
                columns: new[] { "key", "value", "note" },
                values: new object[,]
                {
                    { "paidBreakFactorPct", 4.5, "Paid break allowance added to actual hours @100% before OE is measured (Labour Group Rules -> Breaks)." },
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove the reference rows again, keeping any that live data still refers to.
            migrationBuilder.Sql("DELETE FROM [rostering_rules] WHERE [key] = N'paidBreakFactorPct';");
            migrationBuilder.Sql("DELETE FROM [probation_buffers] WHERE [labour_group] = N'*' AND [max_days] IN (30, 60, 90);");
            migrationBuilder.Sql("DELETE FROM [leave_accrual_bands];");
            migrationBuilder.Sql("DELETE FROM [leave_settings]; DELETE FROM [variance_settings]; DELETE FROM [plan_placement_settings];");
            migrationBuilder.Sql("DELETE FROM [reset_tasks] WHERE [name] IN (N'Re-run 1', N'Re-run 2', N'First declare (light)', N'Full decard');");
            migrationBuilder.Sql("DELETE FROM [admin_tasks] WHERE [name] IN (N'APM', N'End of Day', N'Comms Overhaul', N'Fresh Ordering', N'Longlife Date Checks', N'Non BAU STM Tasks', N'Parameter Deviation & Custom Bays', N'STM Date Check', N'Safe Handover', N'Till Calibration', N'UKG End of Day', N'Use By Date Check', N'CGOS Ordering', N'EMSO Meeting', N'UKG End of PayPeriod', N'UKG Rostering', N'Specials Inventory Management', N'MyWalk', N'Register Assignment', N'Register Cash-Up', N'Register Date Checks', N'EMSO Meeting Prep', N'SPi Planning', N'Specials Price Card Audit');");
            migrationBuilder.Sql("DELETE s FROM [non_op_subcategories] s JOIN [non_op_categories] c ON c.[id] = s.[category_id] WHERE c.[name] IN (N'Meetings', N'Merchandising', N'Miscellaneous', N'Cleaning', N'Training') AND NOT EXISTS (SELECT 1 FROM [non_op_entries] e WHERE e.[subcategory_id] = s.[id]);");
            migrationBuilder.Sql("DELETE c FROM [non_op_categories] c WHERE c.[name] IN (N'Meetings', N'Merchandising', N'Miscellaneous', N'Cleaning', N'Training') AND NOT EXISTS (SELECT 1 FROM [non_op_subcategories] s WHERE s.[category_id] = c.[id]);");
            migrationBuilder.Sql("DELETE FROM [load_types] WHERE [name] IN (N'Ambient', N'Long Life', N'Short Life', N'Freezer', N'Bread', N'Produce', N'Bulky', N'Freeze to Thaw') AND NOT EXISTS (SELECT 1 FROM [daily_loads] d WHERE d.[load_type_id] = [load_types].[id]);");
            migrationBuilder.Sql("DELETE FROM [positions] WHERE [code] IN (N'JSA-16', N'JSA-17', N'SA-FF', N'SA-LR', N'DSM', N'RSM', N'ASM-30-1', N'ASM-30-2', N'ASM-32-1', N'ASM-32-2', N'ASM-35-1', N'ASM-35-2', N'ASM-40-1', N'ASM-40-2', N'ASM-45-1', N'ASM-45-2', N'SM-40-1', N'SM-40-2', N'SM-40-3', N'SM-40-4', N'SM-40-7', N'SM-45-1', N'SM-45-2', N'SM-45-3', N'SM-45-4', N'SM-45-7', N'SM-50-1', N'SM-50-2', N'SM-50-3', N'SM-50-4', N'SM-50-7') AND NOT EXISTS (SELECT 1 FROM [pay_rates] r WHERE r.[role] = [positions].[code]) AND NOT EXISTS (SELECT 1 FROM [employees] e WHERE e.[position_code] = [positions].[code]);");
        }
    }
}
