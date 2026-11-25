// Port of src/app/api/roster/held/route.test.ts: the held roster's time, for Generate
// Roster's safety net, through the real handler with the session and the write check
// stood in for.

using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Web.Features.RosterPage;

namespace Roster.Web.Tests.RosterPage;

public class HeldRosterEndpointTests
{
    private static readonly Guid St1 = Guid.Parse("00000000-0000-4000-8000-0000000000a1");

    private sealed class Held : IRosterService
    {
        public Task<RosterPageModel> LoadPageAsync(RosterPageQuery query, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<DateTimeOffset?> HeldRosterGeneratedAtAsync(Guid storeId, DateOnly periodStart, CancellationToken ct = default) =>
            Task.FromResult<DateTimeOffset?>(storeId == St1 && periodStart == new DateOnly(2026, 11, 23)
                ? DateTimeOffset.Parse("2026-11-14T20:22:05+00:00")
                : storeId == St1 && periodStart == new DateOnly(2026, 11, 9) ? DateTimeOffset.Parse("2026-11-01T01:00:00+00:00") : null);
    }

    private sealed class Scope(ScopedUser? user, string? denied) : IStoreScope
    {
        public AccessContext Access => throw new NotSupportedException();
        public ScopedUser? User => user;
        public Task<string?> DenyStoreWriteAsync(Guid? storeId, CancellationToken ct = default) => Task.FromResult(denied);
        public Task<IReadOnlyList<Guid>?> ReadableStoreIdsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>?> WritableStoreIdsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> CanReadStoreAsync(Guid storeId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> CanWriteStoreAsync(Guid storeId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<StoreAccess?> AccessForStoreAsync(Guid storeId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> CanPeerReadAsync(Guid storeId, CancellationToken ct = default) => throw new NotSupportedException();
        public string? DenyWrite() => throw new NotSupportedException();
        public Task<string?> DenyEmployeeWriteAsync(Guid employeeId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> DenyEmployeesWriteAsync(IEnumerable<Guid> employeeIds, CancellationToken ct = default) => throw new NotSupportedException();
        public bool HasTier(params Tier[] tiers) => throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>?> NetworkStoreIdsAsync(NetworkScope scope, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static readonly ScopedUser U1 = new(Guid.NewGuid(), "u1", Tier.SuperUser, null, null, null, null);

    private static async Task<(int Status, string? CacheControl, string Body)> Ask(string query, ScopedUser? user = null, string? denied = null, bool signedIn = true)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Request.QueryString = new QueryString("?" + query);
        http.Response.Body = new MemoryStream();
        var result = await HeldRosterEndpoint.HandleAsync(http.Request, new Scope(signedIn ? user ?? U1 : null, denied), new Held(), default);
        await result.ExecuteAsync(http);
        return (http.Response.StatusCode, http.Response.Headers.CacheControl.ToString(), Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray()));
    }

    [Fact]
    public async Task Answers_with_when_the_fortnights_roster_was_last_saved()
    {
        var res = await Ask($"store={St1}&period=2026-11-23");
        Assert.Equal(200, res.Status);
        Assert.Equal("no-store", res.CacheControl);
        Assert.Equal("{\"generatedAt\":\"2026-11-14T20:22:05+00:00\"}", res.Body);
    }

    [Fact]
    public async Task Answers_null_for_a_fortnight_with_no_roster_yet() =>
        Assert.Equal("{\"generatedAt\":null}", (await Ask($"store={St1}&period=2026-12-07")).Body);

    [Fact]
    public async Task Refuses_a_visitor_who_is_not_signed_in() =>
        Assert.Equal(401, (await Ask($"store={St1}&period=2026-11-23", signedIn: false)).Status);

    [Fact]
    public async Task Refuses_someone_who_may_not_build_that_stores_roster()
    {
        var res = await Ask($"store={St1}&period=2026-11-23", denied: ScopeMessages.DeniedScope);
        Assert.Equal(403, res.Status);
        Assert.Equal("You don't have access to that store.", res.Body);
    }

    [Fact]
    public async Task Needs_a_store_and_a_plain_date()
    {
        Assert.Equal(400, (await Ask("period=2026-11-23")).Status);
        Assert.Equal(400, (await Ask($"store={St1}&period=23/11/2026")).Status);
        // JavaScript's \d is ASCII and its $ is the very end.
        Assert.Equal(400, (await Ask($"store={St1}&period=٢٠٢٦-11-23")).Status);
        Assert.Equal(400, (await Ask($"store={St1}&period=2026-11-23%0A")).Status);
    }
}
