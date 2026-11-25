using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Roster.Data.Entities;

namespace Roster.Data.Configurations;

internal static partial class RosterModel
{
    private static void Ingest(ModelBuilder mb, SqlDialect d)
    {
        mb.Entity<UploadBatch>(e =>
        {
            e.ToTable("upload_batches", t =>
            {
                t.HasCheckConstraint("upload_batches_type_check", d.In("type", UploadTypes.All));
                t.HasCheckConstraint("upload_batches_error_report_check", d.IsJson("error_report"));
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.Type).IsRequired().Text(32);
            e.Property(x => x.Filename).IsRequired().Text(400);
            e.Property(x => x.UploadedAt).Now(d);
            e.Property(x => x.TotalRows).Default(0);
            e.Property(x => x.AcceptedRows).Default(0);
            e.Property(x => x.RejectedRows).Default(0);
            // jsonb NOT NULL DEFAULT '[]': a typed list stored as JSON text.
            e.Property(x => x.ErrorReport)
                .HasConversion(
                    v => JsonColumns.Serialize(v),
                    v => JsonColumns.Deserialize<List<UploadRowError>>(v),
                    new ValueComparer<List<UploadRowError>>(
                        (a, b) => a!.SequenceEqual(b!),
                        v => v.Aggregate(0, (h, x) => HashCode.Combine(h, x.GetHashCode())),
                        v => v.ToList()))
                .IsRequired()
                .Json(d)
                .HasDefaultValueSql(d.Text("[]"))
                .ValueGeneratedNever();
            // Postgres: both ON DELETE SET NULL (code cascade for the user; roster periods are never deleted).
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UploadedBy).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<RosterPeriod>().WithMany().HasForeignKey(x => x.RosterPeriodId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => x.UploadedBy).HasDatabaseName("upload_batches_user_idx");
            e.HasIndex(x => x.RosterPeriodId).HasDatabaseName("upload_batches_period_idx");
            e.HasIndex(x => new { x.Type, x.UploadedAt }).HasDatabaseName("upload_batches_type_uploaded_idx");
        });

        mb.Entity<BenchmarkDay>(e =>
        {
            e.ToTable("benchmark_days");
            e.HasKey(x => new { x.StoreId, x.Date });
            e.Property(x => x.IsSco).Default(false);
            e.Property(x => x.SalesForecast).Default(0.0);
            e.Property(x => x.Items).Default(0.0);
            e.Property(x => x.CustomerCount).Default(0.0);
            Hours100(e.Property(x => x.LrHours100), "lr_hours_100");
            Hours100(e.Property(x => x.CleaningHours100), "cleaning_hours_100");
            Hours100(e.Property(x => x.SpecialsHours100), "specials_hours_100");
            Hours100(e.Property(x => x.ResetHours100), "reset_hours_100");
            Hours100(e.Property(x => x.AdminHours100), "admin_hours_100");
            Hours100(e.Property(x => x.RegistersHours100), "registers_hours_100");
            Hours100(e.Property(x => x.ScoHours100), "sco_hours_100");
            Hours100(e.Property(x => x.CheckoutHours100), "checkout_hours_100");
            Hours90(e.Property(x => x.LrHours90), "lr_hours_90", "lr_hours_100");
            Hours90(e.Property(x => x.CleaningHours90), "cleaning_hours_90", "cleaning_hours_100");
            Hours90(e.Property(x => x.SpecialsHours90), "specials_hours_90", "specials_hours_100");
            Hours90(e.Property(x => x.ResetHours90), "reset_hours_90", "reset_hours_100");
            Hours90(e.Property(x => x.AdminHours90), "admin_hours_90", "admin_hours_100");
            Hours90(e.Property(x => x.RegistersHours90), "registers_hours_90", "registers_hours_100");
            Hours90(e.Property(x => x.ScoHours90), "sco_hours_90", "sco_hours_100");
            Hours90(e.Property(x => x.CheckoutHours90), "checkout_hours_90", "checkout_hours_100");
            e.Property(x => x.EmployeeCount).Default(0);
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<RosterPeriod>().WithMany().HasForeignKey(x => x.RosterPeriodId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<UploadBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.BatchId).HasDatabaseName("benchmark_days_batch_idx");
            e.HasIndex(x => x.RosterPeriodId).HasDatabaseName("benchmark_days_period_idx");
            e.HasIndex(x => x.Date).HasDatabaseName("benchmark_days_date_idx");

            void Hours100(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<double> p, string column) =>
                p.HasColumnName(column).Default(0.0);

            void Hours90(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<double> p, string column, string source) =>
                p.HasColumnName(column).HasComputedColumnSql(d.Over90(source), stored: true);
        });

        mb.Entity<QuarterHourSegment>(e =>
        {
            e.ToTable("quarter_hour_segments", t =>
            {
                t.HasCheckConstraint("quarter_hour_segments_block_time_check", d.QuarterHour("block_time"));
                t.HasCheckConstraint("quarter_hour_segments_segment_check", "[segment] >= 0");
            });
            e.HasKey(x => new { x.StoreId, x.Date, x.BlockTime });
            e.Property(x => x.BlockTime).Time(d);
            e.Property(x => x.Segment).Default(0.0);
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<RosterPeriod>().WithMany().HasForeignKey(x => x.RosterPeriodId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<UploadBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.RosterPeriodId).HasDatabaseName("quarter_hour_segments_period_idx");
            e.HasIndex(x => x.BatchId).HasDatabaseName("quarter_hour_segments_batch_idx");
        });

        mb.Entity<ActualsDay>(e =>
        {
            e.ToTable("actuals_days");
            e.HasKey(x => new { x.StoreId, x.Date });
            e.Property(x => x.ActualSales).Default(0.0);
            e.Property(x => x.ActualItems).Default(0.0);
            e.Property(x => x.ActualHours100).HasColumnName("actual_hours_100").Default(0.0);
            e.Property(x => x.UkgActualHours).Default(0.0);
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<UploadBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.BatchId).HasDatabaseName("actuals_days_batch_idx");
            e.HasIndex(x => x.Date).HasDatabaseName("actuals_days_date_idx");
        });

        mb.Entity<QuarterHourAllocation>(e =>
        {
            e.ToTable("quarter_hour_allocation", t => t.HasCheckConstraint("quarter_hour_allocation_day_of_week_check", "[day_of_week] BETWEEN 0 AND 6"));
            e.HasKey(x => new { x.StoreId, x.DayOfWeek, x.BlockTime });
            e.Property(x => x.BlockTime).Time(d);
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<UploadBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.BatchId).HasDatabaseName("qha_batch_idx");
        });

        mb.Entity<PosIntervalStat>(e =>
        {
            e.ToTable("pos_interval_stats", t =>
            {
                t.HasCheckConstraint("pos_interval_stats_mco_customers_check", "[mco_customers] >= 0");
                t.HasCheckConstraint("pos_interval_stats_sco_customers_check", "[sco_customers] >= 0");
                t.HasCheckConstraint("pos_interval_stats_mco_items_check", "[mco_items] >= 0");
                t.HasCheckConstraint("pos_interval_stats_sco_items_check", "[sco_items] >= 0");
            });
            e.HasKey(x => new { x.StoreId, x.Date, x.BlockTime });
            e.Property(x => x.BlockTime).Time(d);
            e.Property(x => x.McoCustomers).Default(0.0);
            e.Property(x => x.ScoCustomers).Default(0.0);
            e.Property(x => x.McoItems).Default(0.0);
            e.Property(x => x.ScoItems).Default(0.0);
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<UploadBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.BatchId).HasDatabaseName("pis_batch_idx");
        });

        mb.Entity<FeedToken>(e =>
        {
            e.ToTable("feed_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).GuidKey(d);
            e.Property(x => x.Label).IsRequired().Text(200);
            e.Property(x => x.Token).IsRequired().Text(128).DefaultSql(d.RandomHex64);
            e.HasIndex(x => x.Token).IsUnique().HasDatabaseName("feed_tokens_token_key");
            e.Property(x => x.CreatedAt).Now(d);
        });

        mb.Entity<CronRun>(e =>
        {
            e.ToTable("cron_runs", t => t.HasCheckConstraint("cron_runs_last_result_check", d.IsJson("last_result")));
            e.HasKey(x => x.Job);
            e.Property(x => x.Job).Text(100);
            e.Property(x => x.LastRunAt).Now(d);
            e.Property(x => x.RunCount).Default(0L);
            e.Property(x => x.LastResult).Json(d);
        });
    }
}
