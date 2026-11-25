// Port of src/lib/csv/leaveUploads.ts.
// ─── Leave exports as first-class uploads ────────────────────────────────────
// Adapters that put the two UKG leave exports through the SAME validate →
// review → commit pipeline as Benchmark, Actuals and Quarter-Hour, so there is
// one place a super user goes to load data and one batch history covering all
// of it.
//
// The parsing itself lives in UkgLeave (preamble stripping, accounting
// brackets, DD-MM-YYYY dates); this file only reshapes the result into the
// shared ValidationResult and adds the referential store-code check the other
// uploads do.

namespace Roster.Data.Feeds;

public static class LeaveUploads
{
    private static ValidationResult<T> Referential<T>(LeaveParseResult<T> parsed, IReadOnlySet<string> validStoreCodes)
        where T : IFeedRow
    {
        var errors = new List<RowError>(parsed.Errors);
        var validRows = new List<T>();
        foreach (var r in parsed.Rows)
        {
            if (!validStoreCodes.Contains(r.StoreCode!))
            {
                // The row's own line - this used to be its position among the rows
                // that parsed, which pointed at the wrong line of the file.
                errors.Add(new RowError(r.Row, r.StoreCode, "StoreID", $"Unknown store code '{r.StoreCode}'"));
                continue;
            }
            validRows.Add(r);
        }
        return new ValidationResult<T>(
            parsed.Rows.Count + parsed.Errors.Count + (parsed.Collapsed ?? 0),
            validRows,
            Duplicates.ByRow(errors),
            [],
            parsed.Collapsed ?? 0);
    }

    public static ValidationResult<ParsedLiabilityRow> ValidateLeaveLiability(string text, IReadOnlySet<string> validStoreCodes)
    {
        var parsed = UkgLeave.ParseLiability(text);
        if (parsed.Rows.Count == 0 && parsed.Errors.Count == 0)
        {
            return new ValidationResult<ParsedLiabilityRow>(0, [], [],
            [
                "No data rows found. Check this is the UKG Leave Liability export and that it carries a StoreID column.",
            ]);
        }
        return Referential(parsed, validStoreCodes);
    }

    public static ValidationResult<ParsedRequestRow> ValidateLeaveRequests(string text, IReadOnlySet<string> validStoreCodes)
    {
        var parsed = UkgLeave.ParseLeaveRequests(text);
        if (parsed.Rows.Count == 0 && parsed.Errors.Count == 0)
        {
            return new ValidationResult<ParsedRequestRow>(0, [], [],
            [
                "No data rows found. Check this is the UKG Leave Request Dataview and that it carries a StoreID column.",
            ]);
        }
        return Referential(parsed, validStoreCodes);
    }
}
