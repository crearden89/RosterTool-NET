// New: the { ok, error } every Super User page action returns in the TypeScript app
// (src/app/(app)/admin/*/actions.ts), and its requireTier("super_user") guard.

using Roster.Data.Entities;
using Roster.Data.Scope;

namespace Roster.Web.Features.Admin.Shared;

/// <summary>An action's outcome: ok, or the reason the page shows ("Error: …").</summary>
public sealed record AdminResult(bool Ok, string? Error = null)
{
    public static readonly AdminResult Success = new(true);

    public static AdminResult Fail(string error) => new(false, error);
}

/// <summary>A Super User action was called by someone who is not one.</summary>
public sealed class SuperUserRequiredException() : UnauthorizedAccessException("This needs a Super User.");

public static class AdminAccess
{
    /// <summary>
    /// requireTier("super_user"), as every admin action starts. The TypeScript action
    /// redirected to /overview; the pages themselves already require a Super User
    /// (and send anyone else to the Dashboard), so here the call is simply refused.
    /// </summary>
    public static ScopedUser RequireSuperUser(this IStoreScope scope) =>
        scope.HasTier(Tier.SuperUser) && scope.User is { } user ? user : throw new SuperUserRequiredException();
}

public static class AdminDb
{
    /// <summary>
    /// A database refusal, as the action reports it: the TypeScript actions return
    /// Supabase's error.message; here it is the database's own message.
    /// </summary>
    public static bool IsRefusal(Exception e) =>
        e is Microsoft.EntityFrameworkCore.DbUpdateException or System.Data.Common.DbException or ScopeViolationException;

    public static string MessageOf(Exception e) => e.GetBaseException().Message;

    /// <summary>
    /// A refusal because rows elsewhere still reference the row: Postgres says
    /// "violates foreign key constraint", SQL Server "conflicted with the REFERENCE
    /// constraint", SQLite "FOREIGN KEY constraint failed".
    /// </summary>
    public static bool IsForeignKeyRefusal(string message) =>
        message.Contains("foreign key", StringComparison.OrdinalIgnoreCase)
        || message.Contains("REFERENCE constraint", StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs a write; a database refusal comes back as the action's error.</summary>
    public static async Task<AdminResult> WriteAsync(Func<Task> write)
    {
        try
        {
            await write();
            return AdminResult.Success;
        }
        catch (Exception e) when (IsRefusal(e))
        {
            return AdminResult.Fail(MessageOf(e));
        }
    }
}
