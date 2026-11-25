using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;

namespace Roster.Data.Configurations;

internal static partial class RosterModel
{
    private static void Planning(ModelBuilder mb, SqlDialect d)
    {
        mb.Entity<DayPlan>(e =>
        {
            e.ToTable("day_plans", t =>
            {
                t.HasCheckConstraint("day_plans_plan_status_check", d.In("plan_status", DayStatuses.All));
                t.HasCheckConstraint("day_plans_roster_status_check", d.In("roster_status", DayStatuses.All));
            });
            e.HasKey(x => new { x.StoreId, x.Date });
            e.Property(x => x.PlanStatus).IsRequired().Text(16).Default(DayStatuses.None);
            e.Property(x => x.RosterStatus).IsRequired().Text(16).Default(DayStatuses.None);
            e.Property(x => x.PhLoadReallocated).Default(false);
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.Date, x.RosterStatus }).HasDatabaseName("day_plans_date_idx");
        });

        mb.Entity<PlannedBlock>(e =>
        {
            e.ToTable("planned_blocks", t => t.HasCheckConstraint("planned_blocks_headcount_check", "[headcount] >= 0"));
            e.HasKey(x => new { x.StoreId, x.Date, x.LabourGroup, x.BlockTime });
            e.Property(x => x.LabourGroup).Text(32);
            e.Property(x => x.BlockTime).Time(d);
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<PlanSnapshot>(e =>
        {
            e.ToTable("plan_snapshots", t =>
            {
                t.HasCheckConstraint("plan_snapshots_kind_check", d.In("kind", SnapshotKinds.Plan, SnapshotKinds.Roster));
                t.HasCheckConstraint("plan_snapshots_payload_check", d.IsJson("payload"));
            });
            e.HasKey(x => new { x.StoreId, x.PeriodStart, x.Kind });
            e.Property(x => x.Kind).Text(16);
            e.Property(x => x.Payload).IsRequired().Json(d);
            e.Property(x => x.GeneratedAt).Now(d);
            e.Property(x => x.Version).Default(1).IsConcurrencyToken();
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
            // Postgres declared no ON DELETE action: deleting the user is refused.
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.GeneratedBy).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => x.GeneratedBy).HasDatabaseName("plan_snapshots_by_idx");
        });

        mb.Entity<SalesOverride>(e =>
        {
            e.ToTable("sales_overrides");
            e.HasKey(x => new { x.StoreId, x.Date });
            e.Property(x => x.Closed).Default(false);
            e.Property(x => x.Method).Text(32);
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<NonOpCategory>(e =>
        {
            e.ToTable("non_op_categories");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.Name).IsRequired().Text(200);
            e.Property(x => x.Sort).Default(0);
        });

        mb.Entity<NonOpSubcategory>(e =>
        {
            e.ToTable("non_op_subcategories", t => t.HasCheckConstraint("non_op_subcategories_days_allowed_check", d.IsJson("days_allowed")));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.Name).IsRequired().Text(200);
            // Postgres int[] '{0,1,2,3,4,5,6}': a JSON array here.
            e.Property(x => x.DaysAllowed).IsRequired().Json(d).HasDefaultValueSql(d.Text("[0,1,2,3,4,5,6]")).ValueGeneratedNever();
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => x.CategoryId).HasDatabaseName("non_op_subcategories_cat_idx");
        });

        mb.Entity<NonOpEntry>(e =>
        {
            e.ToTable("non_op_entries", t => t.HasCheckConstraint("non_op_entries_minutes_check", "[minutes] >= 0"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.CreatedAt).Now(d);
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<NonOpSubcategory>().WithMany().HasForeignKey(x => x.SubcategoryId).OnDelete(DeleteBehavior.NoAction);
            // A second path from stores (via employees): NO ACTION (code cascade sets it null).
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.StoreId, x.Date }).HasDatabaseName("non_op_entries_store_date_idx");
            e.HasIndex(x => x.SubcategoryId).HasDatabaseName("non_op_entries_sub_idx");
            e.HasIndex(x => x.EmployeeId).HasDatabaseName("non_op_entries_emp_idx");
        });

        mb.Entity<RosterAudit>(e =>
        {
            e.ToTable("roster_audit", t => t.HasCheckConstraint("roster_audit_detail_check", d.IsJson("detail")));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.Action).IsRequired().Text(64);
            e.Property(x => x.Detail).Json(d);
            e.Property(x => x.CreatedAt).Now(d);
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction); // code cascade: set null
            e.HasIndex(x => new { x.StoreId, x.Date }).HasDatabaseName("roster_audit_store_idx");
            e.HasIndex(x => x.UserId).HasDatabaseName("roster_audit_user_idx");
        });

        mb.Entity<GeneratedShift>(e =>
        {
            e.ToTable("generated_shifts", t =>
            {
                t.HasCheckConstraint("generated_shifts_role_check", d.In("role", Roles.All));
                t.HasCheckConstraint("generated_shifts_break_minutes_check", "[break_minutes] >= 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.EmployeeName).IsRequired().Text(200);
            e.Property(x => x.Role).IsRequired().Text(8);
            e.Property(x => x.StartTime).Time(d);
            e.Property(x => x.EndTime).Time(d);
            e.Property(x => x.BreakMinutes).Default(0);
            e.Property(x => x.Locked).Default(false);
            e.Property(x => x.CreatedAt).Now(d);
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
            // A second path from stores: NO ACTION; DeletionService removes the person's shifts first.
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.LockedBy).OnDelete(DeleteBehavior.NoAction); // code cascade: set null
            e.HasMany(x => x.Blocks).WithOne().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.StoreId, x.Date }).HasDatabaseName("generated_shifts_store_date_idx");
            e.HasIndex(x => x.EmployeeId).HasDatabaseName("generated_shifts_emp_idx");
            e.HasIndex(x => x.LockedBy).HasDatabaseName("generated_shifts_locked_by_idx");
        });

        mb.Entity<ShiftTaskBlock>(e =>
        {
            e.ToTable("shift_task_blocks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.BlockTime).Time(d);
            e.Property(x => x.LabourGroup).IsRequired().Text(32);
            e.Property(x => x.SubTask).Text(32);
            e.HasIndex(x => new { x.ShiftId, x.BlockTime }).HasDatabaseName("shift_task_blocks_shift_idx");
        });
    }

    private static void Reviews(ModelBuilder mb, SqlDialect d)
    {
        mb.Entity<RosterReview>(e =>
        {
            e.ToTable("roster_reviews", t =>
            {
                t.HasCheckConstraint("roster_reviews_status_check", d.In("status", ReviewStatuses.All));
                t.HasCheckConstraint("roster_reviews_return_message_check", $"[return_message] IS NULL OR {d.Len("return_message")} <= 2000");
            });
            e.HasKey(x => new { x.StoreId, x.PeriodStart });
            e.Property(x => x.Status).IsRequired().Text(16).Default(ReviewStatuses.InProgress);
            e.Property(x => x.SnapshotKey).Text(64);
            e.Property(x => x.ReturnMessage).Text(2000);
            e.Property(x => x.UpdatedAt).Now(d, alsoOnUpdate: true);
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
            // Two keys to app_users from one table would be two cascade paths: NO ACTION (code cascade).
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.SubmittedBy).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.DecidedBy).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => x.SubmittedBy).HasDatabaseName("roster_reviews_submitted_by_idx");
            e.HasIndex(x => x.DecidedBy).HasDatabaseName("roster_reviews_decided_by_idx");
        });

        mb.Entity<RosterReviewCheck>(e =>
        {
            e.ToTable("roster_review_checks", t =>
                t.HasCheckConstraint("roster_review_checks_note_check", $"[note] IS NULL OR {d.Len("note")} <= 1000"));
            e.HasKey(x => new { x.StoreId, x.PeriodStart, x.CheckKey });
            e.Property(x => x.CheckKey).Text(64);
            e.Property(x => x.Note).Text(1000);
            e.Property(x => x.CheckedAt).Now(d);
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.CheckedBy).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => x.CheckedBy).HasDatabaseName("roster_review_checks_checked_by_idx");
        });

        mb.Entity<RosterReviewDay>(e =>
        {
            e.ToTable("roster_review_days", t =>
            {
                t.HasCheckConstraint("roster_review_days_verdict_check", d.In("verdict", ReviewVerdicts.Ok, ReviewVerdicts.Query));
                t.HasCheckConstraint("roster_review_days_query_check", $"[query] IS NULL OR {d.Len("query")} <= 1000");
                t.HasCheckConstraint("roster_review_days_reply_check", $"[reply] IS NULL OR {d.Len("reply")} <= 1000");
            });
            e.HasKey(x => new { x.StoreId, x.PeriodStart, x.Date });
            e.Property(x => x.Verdict).Text(8);
            e.Property(x => x.Query).Text(1000);
            e.Property(x => x.Reply).Text(1000);
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.DecidedBy).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => x.DecidedBy).HasDatabaseName("roster_review_days_decided_by_idx");
        });

        mb.Entity<RosterReviewEvent>(e =>
        {
            e.ToTable("roster_review_events", t =>
                t.HasCheckConstraint("roster_review_events_body_check", $"{d.Len("body")} <= 3000"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.ActorName).Text(256);
            e.Property(x => x.ActorTier).Text(16);
            e.Property(x => x.Kind).IsRequired().Text(64);
            e.Property(x => x.Body).IsRequired().Text(3000);
            e.Property(x => x.CreatedAt).Now(d);
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.StoreId, x.PeriodStart, x.CreatedAt }).IsDescending(false, false, true)
                .HasDatabaseName("roster_review_events_period_idx");
            e.HasIndex(x => x.ActorId).HasDatabaseName("roster_review_events_actor_idx");
        });
    }
}
