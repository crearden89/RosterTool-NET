// Port of src/lib/cron/heartbeat.ts.
using System.Text.Json;
using Roster.Data.Services.Functions;

namespace Roster.Data.Jobs;

// Record that a scheduled job completed. Every cron job calls this on its way
// out, so `select * from cron_runs` answers "is the schedule actually running?"
// without needing runtime logs that have long since expired.
//
// A heartbeat failure must never fail the job it is reporting on - the write is
// bookkeeping, not the work - so this swallows its own error and hands back
// whether it landed, for the caller to include in its response.

/// <summary>The cron_runs heartbeat (migration 0043), written through bump_cron_run.</summary>
public interface ICronHeartbeat
{
    /// <summary>
    /// recordCronRun: upsert the job's cron_runs row (last_run_at = now, run_count + 1,
    /// last_result = <paramref name="result"/> as JSON, or null). True when the write landed;
    /// never throws.
    /// </summary>
    Task<bool> RecordCronRunAsync(string job, object? result = null, CancellationToken cancellationToken = default);
}

/// <summary>How a job's result is written to cron_runs.last_result: the JSON the TypeScript route returned.</summary>
public static class CronResultJson
{
    /// <summary>camelCase property names, as the TypeScript objects were ({"created":0,"updated":0,"failed":0}).</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>The JSON text for a result, or null for no result.</summary>
    public static string? Serialize(object? result) =>
        result is null ? null : JsonSerializer.Serialize(result, result.GetType(), Options);
}

internal sealed class CronHeartbeat(IDatabaseFunctions functions) : ICronHeartbeat
{
    public async Task<bool> RecordCronRunAsync(string job, object? result = null, CancellationToken cancellationToken = default)
    {
        try
        {
            JsonElement? json = result is null ? null : JsonSerializer.SerializeToElement(result, result.GetType(), CronResultJson.Options);
            await functions.BumpCronRunAsync(job, json, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
