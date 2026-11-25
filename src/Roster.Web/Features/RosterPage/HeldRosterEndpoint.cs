// Port of src/app/api/roster/held/route.ts.

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Roster.Data.Scope;

namespace Roster.Web.Features.RosterPage;

/// <summary>
/// GET /api/roster/held?store=&lt;id&gt;&amp;period=&lt;yyyy-mm-dd&gt;: when the fortnight's held
/// roster was last saved, as <c>{ "generatedAt": ... }</c> (null when there is none). Asked by
/// Generate Roster's safety net when the answer to a press has not arrived. A plain request,
/// so it never waits behind the request it is checking. Only someone who may build this
/// store's roster gets an answer.
/// </summary>
public static partial class HeldRosterEndpoint
{
    public const string Path = "/api/roster/held";

    public sealed record HeldRoster(DateTimeOffset? GeneratedAt);

    // JavaScript's /^\d{4}-\d{2}-\d{2}$/: ASCII digits, and nothing after the day.
    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}\z")]
    private static partial Regex PlainDate();

    public static async Task<IResult> HandleAsync(HttpRequest request, IStoreScope scope, IRosterService roster, CancellationToken ct)
    {
        if (scope.User is null) return Text("Unauthorised", StatusCodes.Status401Unauthorized);

        var store = request.Query["store"].ToString();
        var period = request.Query["period"].ToString();
        if (store.Length == 0 || !PlainDate().IsMatch(period))
            return Text("store and period required", StatusCodes.Status400BadRequest);

        var storeId = Guid.TryParse(store, out var id) ? id : (Guid?)null;
        var denied = await scope.DenyStoreWriteAsync(storeId, ct);
        if (denied is not null) return Text(denied, StatusCodes.Status403Forbidden);
        // What the database would have refused: a store id that is no id, a date that is no date.
        if (storeId is null) return Text($"invalid input syntax for type uuid: \"{store}\"", StatusCodes.Status500InternalServerError);
        if (!DateOnly.TryParseExact(period, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var periodStart))
            return Text($"date/time field value out of range: \"{period}\"", StatusCodes.Status500InternalServerError);

        var generatedAt = await roster.HeldRosterGeneratedAtAsync(storeId.Value, periodStart, ct);
        request.HttpContext.Response.Headers.CacheControl = "no-store";
        return TypedResults.Json(new HeldRoster(generatedAt));
    }

    private static ContentHttpResult Text(string body, int status) => TypedResults.Text(body, "text/plain; charset=utf-8", statusCode: status);
}
