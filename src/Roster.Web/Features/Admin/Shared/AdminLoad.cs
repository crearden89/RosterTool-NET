// New: a page's reads as the TypeScript pages took them: Supabase handed back null data on
// a failed read and the page drew itself from `data ?? []`. So a read that fails because
// the database cannot be reached leaves the page showing nothing for it, instead of
// failing the page. Refusals (a Super User page opened by someone else) still fail.

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Roster.Data.Scope;

namespace Roster.Web.Features.Admin.Shared;

public static class AdminLoad
{
    /// <summary>The page's data, or <paramref name="empty"/> when the database cannot be read.</summary>
    public static async Task<T> OrAsync<T>(Func<Task<T>> load, T empty)
    {
        try
        {
            return await load();
        }
        catch (Exception e) when (Unreachable(e))
        {
            return empty;
        }
    }

    /// <summary>A database that is not there or not configured (no connection string, no server).</summary>
    public static bool Unreachable(Exception e) =>
        e is DbException or DbUpdateException || (e is InvalidOperationException && e is not ScopeViolationException);
}
