// Port of src/app/api/uploads/[type]/route.ts, src/app/api/ingest/[type]/route.ts,
// src/app/api/export/ukg/route.ts and src/app/api/print/ukg-roster/route.ts: the HTTP
// side (who may ask, the form or query, the status codes). The work is in Roster.Data.Feeds.

using System.Text;
using System.Text.RegularExpressions;
using Roster.Data.Feeds;
using Roster.Engine.Core;
using Roster.Web.Auth;

namespace Roster.Web.Features.Feeds;

public static partial class FeedEndpoints
{
    public const string UploadsPath = "/api/uploads/{type}";
    public const string IngestPath = "/api/ingest/{type}";
    public const string ExportUkgPath = "/api/export/ukg";
    public const string PrintUkgRosterPath = "/api/print/ukg-roster";

    private static IResult Error(string message, int status) => Results.Json(new { error = message }, statusCode: status);

    private static IResult Text(string body, int status) => Results.Text(body, "text/plain; charset=utf-8", Encoding.UTF8, status);

    // POST /api/uploads/{type}  - Super User only.
    // Body: multipart form { file, mode: "validate"|"commit", acceptPartial?: "true",
    //                        periodEnd?: "YYYY-MM-DD" (Leave Liability only) }
    public static async Task<IResult> UploadAsync(string type, HttpContext http, IUploadIngest ingest, CancellationToken ct)
    {
        var user = SignedInUser.FromPrincipal(http.User);
        if (user is null || user.Tier != Tiers.SuperUser) return Error("Not authorised", StatusCodes.Status403Forbidden);
        if (!FeedTypes.Uploads.Contains(type)) return Error($"Unknown upload type '{type}'", StatusCodes.Status400BadRequest);

        var form = await http.Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        var mode = form.TryGetValue("mode", out var m) ? m[0] ?? "" : "validate";
        var acceptPartial = (form.TryGetValue("acceptPartial", out var a) ? a[0] ?? "" : "false") == "true";
        // Leave Liability is a point-in-time snapshot with no period of its own.
        var periodEnd = form.TryGetValue("periodEnd", out var p) && !string.IsNullOrEmpty(p[0]) ? p[0] : null;

        if (file is null) return Error("No file provided", StatusCodes.Status400BadRequest);
        string text;
        using (var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            text = await reader.ReadToEndAsync(ct);

        try
        {
            if (mode == "commit")
                return Results.Json<object>(await ingest.CommitUploadAsync(type, text, file.FileName, user.Id, acceptPartial, new UploadOptions(periodEnd), ct));
            return Results.Json<object>(await ingest.ValidateUploadAsync(type, text, ct));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Error(e.Message, StatusCodes.Status500InternalServerError);
        }
    }

    // POST /api/ingest/{type} - the automated data-feed endpoint for IT's sync
    // jobs (Super User → Data Feed has the tokens and the integration spec).
    //
    //   Authorization: Bearer <feed token>
    //   Body:         CSV text, the SAME layout the manual Uploads screen accepts
    //                 (one validator serves both paths)
    //   ?mode=validate  dry-run: full validation report, no writes
    //
    // Commits are always partial-accept: an automated feed shouldn't lose a whole
    // batch to one bad row - valid rows land, rejects come back (and are stored on
    // the batch record) for the sync job to alert on. Expected cadence: actuals
    // several times a day (dates in each store's LOCAL day), benchmark fortnightly
    // on the Friday before planning day, POS quarter-hour statistics every four
    // weeks (the trade curve and the Load Running customer figure derive from
    // them at read time - a push re-states both).
    public static async Task<IResult> IngestAsync(string type, HttpContext http, IUploadIngest ingest, IFeedTokens tokens, CancellationToken ct)
    {
        if (!FeedTypes.Ingest.Contains(type)) return Error($"Unknown feed type '{type}'", StatusCodes.Status400BadRequest);

        var auth = http.Request.Headers.Authorization.Count > 0 ? http.Request.Headers.Authorization[0] ?? "" : "";
        var token = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? Js.Trim(auth[7..]) : "";
        if (token.Length == 0) return Error("Missing bearer token", StatusCodes.Status401Unauthorized);
        var feed = await tokens.FindAsync(token, ct);
        if (feed is null) return Error("Invalid feed token", StatusCodes.Status401Unauthorized);

        string text;
        using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            text = await reader.ReadToEndAsync(ct);
        if (Js.Trim(text).Length == 0) return Error("Empty body - send CSV text", StatusCodes.Status400BadRequest);
        var dryRun = http.Request.Query.TryGetValue("mode", out var mode) && mode[0] == "validate";

        try
        {
            IngestValidation result = dryRun
                ? await ingest.ValidateUploadAsync(type, text, ct)
                : await ingest.CommitUploadAsync(type, text, $"api:{feed.Label}", null, true, null, ct);
            await tokens.TouchAsync(feed.Id, ct);
            // File-level failures (bad headers etc.) mean nothing was written - use a
            // 422 so a sync job's plain status check catches it.
            var failed = result.FileErrors.Count > 0 || (!dryRun && result is not IngestCommit { BatchId: not null });
            return Results.Json<object>(result, statusCode: failed && !dryRun ? StatusCodes.Status422UnprocessableEntity : StatusCodes.Status200OK);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Error(e.Message, StatusCodes.Status500InternalServerError);
        }
    }

    // JavaScript's /^\d{4}-\d{2}-\d{2}$/: ASCII digits, and nothing after the day.
    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}\z")]
    private static partial Regex PlainDate();

    /// <summary>The checks both roster files make first; a response when they fail.</summary>
    private static IResult? Check(HttpContext http, out string storeId, out string start, out DateOnly first)
    {
        storeId = start = "";
        first = default;
        var user = SignedInUser.FromPrincipal(http.User);
        if (user is null) return Text("Unauthorised", StatusCodes.Status401Unauthorized);

        var query = http.Request.Query;
        storeId = query.TryGetValue("store", out var s) ? s[0] ?? "" : user.StoreId?.ToString() ?? "";
        start = query.TryGetValue("start", out var st) ? st[0] ?? "" : "";
        if (storeId.Length == 0 || start.Length == 0) return Text("store and start required", StatusCodes.Status400BadRequest);
        // The date is echoed into headers/markup and fed to new Date(); reject
        // anything that isn't a plain ISO day before it goes anywhere.
        if (!PlainDate().IsMatch(start)) return Text("bad start date", StatusCodes.Status400BadRequest);
        // A month or day out of range was an Invalid Date, and the route threw.
        if (UkgRosterFiles.FortnightStart(start) is not { } d) return Text("Invalid time value", StatusCodes.Status500InternalServerError);
        first = d;
        return null;
    }

    // GET /api/export/ukg?store=<id>&start=<yyyy-mm-dd>
    // Returns the approved roster for the fortnight as a UKG-style import CSV
    // (spec 11.8 - manual import into UKG, no live sync in Phase 1).
    public static async Task<IResult> ExportUkgAsync(HttpContext http, IUkgRosterFiles files, CancellationToken ct)
    {
        if (Check(http, out var storeId, out var start, out var first) is { } refused) return refused;
        var file = await files.ExportCsvAsync(storeId, start, first, ct);
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{file.FileName}\"";
        return Results.Text(file.Csv, "text/csv; charset=utf-8", Encoding.UTF8);
    }

    // GET /api/print/ukg-roster?store=<id>&start=<yyyy-mm-dd>[&subtasks=1]
    // A print-optimised HTML fortnight roster (see UkgRosterFiles.PrintHtmlAsync).
    public static async Task<IResult> PrintUkgRosterAsync(HttpContext http, IUkgRosterFiles files, CancellationToken ct)
    {
        if (Check(http, out var storeId, out _, out var first) is { } refused) return refused;
        var showSubtasks = http.Request.Query.TryGetValue("subtasks", out var sub) && sub[0] == "1";
        var html = await files.PrintHtmlAsync(storeId, first, showSubtasks, ct);
        return Results.Text(html, "text/html; charset=utf-8", Encoding.UTF8);
    }
}
