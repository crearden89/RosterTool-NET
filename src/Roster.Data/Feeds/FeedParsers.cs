// New: one interface per incoming feed (ASSUMPTIONS.md #7). Every kind of data arrives
// today as a CSV file - an upload on Admin → Uploads, or the same CSV pushed to
// /api/ingest/{type} - and is validated into rows by the ports of lib/csv/*. A direct feed
// from an internal system can replace one of these registrations (or hand its own rows to
// IUploadIngest.CommitRowsAsync) without touching the engine: the engine reads only the
// tables the commit writes.

namespace Roster.Data.Feeds;

/// <summary>The upload types and the subsets each endpoint accepts.</summary>
public static class FeedTypes
{
    public const string Benchmark = "benchmark";
    public const string Actuals = "actuals";
    public const string LeaveLiability = "leave_liability";
    public const string LeaveRequests = "leave_requests";
    public const string Contacts = "contacts";
    public const string PosQuarterHour = "pos_quarter_hour";

    /// <summary>What Admin → Uploads and POST /api/uploads/{type} accept (VALID_TYPES).</summary>
    public static readonly IReadOnlyList<string> Uploads =
        [Benchmark, Actuals, LeaveLiability, LeaveRequests, Contacts, PosQuarterHour];

    /// <summary>What the automated data feed, POST /api/ingest/{type}, accepts.</summary>
    public static readonly IReadOnlyList<string> Ingest = [Benchmark, Actuals, PosQuarterHour];
}

/// <summary>Reads one delivery of a feed into validated rows.</summary>
public interface IFeedParser<TRow> where TRow : class, IFeedRow
{
    /// <summary>The upload type it serves (<see cref="FeedTypes"/>).</summary>
    string Type { get; }

    /// <summary>
    /// Validate a delivery. <paramref name="validStoreCodes"/> restricts store codes to known
    /// stores (the referential check, spec 4.4).
    /// </summary>
    ValidationResult<TRow> Validate(string text, IReadOnlySet<string> validStoreCodes);
}

/// <summary>The fortnightly Benchmark export (lib/csv/benchmark).</summary>
public sealed class BenchmarkCsvFeed : IFeedParser<BenchmarkRow>
{
    public string Type => FeedTypes.Benchmark;
    public ValidationResult<BenchmarkRow> Validate(string text, IReadOnlySet<string> validStoreCodes) =>
        Benchmark.ValidateBenchmark(text, validStoreCodes);
}

/// <summary>The daily Actuals export (lib/csv/actuals).</summary>
public sealed class ActualsCsvFeed : IFeedParser<ActualsRow>
{
    public string Type => FeedTypes.Actuals;
    public ValidationResult<ActualsRow> Validate(string text, IReadOnlySet<string> validStoreCodes) =>
        Actuals.ValidateActuals(text, validStoreCodes);
}

/// <summary>The four-weekly POS statistics export (lib/csv/posQuarterHour).</summary>
public sealed class PosQuarterHourCsvFeed : IFeedParser<PosQuarterHourRow>
{
    public string Type => FeedTypes.PosQuarterHour;
    public ValidationResult<PosQuarterHourRow> Validate(string text, IReadOnlySet<string> validStoreCodes) =>
        PosQuarterHour.ValidatePosQuarterHour(text, validStoreCodes);
}

/// <summary>The UKG Leave Liability export (lib/csv/leaveUploads).</summary>
public sealed class LeaveLiabilityCsvFeed : IFeedParser<ParsedLiabilityRow>
{
    public string Type => FeedTypes.LeaveLiability;
    public ValidationResult<ParsedLiabilityRow> Validate(string text, IReadOnlySet<string> validStoreCodes) =>
        LeaveUploads.ValidateLeaveLiability(text, validStoreCodes);
}

/// <summary>The UKG Leave Request dataview (lib/csv/leaveUploads).</summary>
public sealed class LeaveRequestsCsvFeed : IFeedParser<ParsedRequestRow>
{
    public string Type => FeedTypes.LeaveRequests;
    public ValidationResult<ParsedRequestRow> Validate(string text, IReadOnlySet<string> validStoreCodes) =>
        LeaveUploads.ValidateLeaveRequests(text, validStoreCodes);
}

/// <summary>The UKG Contact Details export (lib/csv/ukgContacts).</summary>
public sealed class ContactsCsvFeed : IFeedParser<ContactRow>
{
    public string Type => FeedTypes.Contacts;
    public ValidationResult<ContactRow> Validate(string text, IReadOnlySet<string> validStoreCodes) =>
        UkgContacts.ValidateContacts(text, validStoreCodes);
}
