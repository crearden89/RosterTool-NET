// Port of the `await requireTier("super_user")` that opens every read and action of the
// rules and process pages (src/app/(app)/admin/rules/*, src/app/(app)/admin/process/*).

using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;
using Roster.Data.Scope;

namespace Roster.Web.Features.Rules;

public static class SuperUserGuard
{
    /// <summary>
    /// requireTier("super_user"). The pages are already behind the Super User policy, so
    /// this only stops a call that got past it; the TypeScript action redirected to the
    /// Dashboard instead of returning.
    /// </summary>
    public static void Require(IStoreScope scope)
    {
        if (!scope.HasTier(Tier.SuperUser))
            throw new UnauthorizedAccessException("The rules and process settings are for super users only.");
    }

    /// <summary>
    /// The message a refused write returns, as the TypeScript returned the database's
    /// <c>error.message</c>: the database's own message, or the write check's.
    /// </summary>
    public static string MessageOf(Exception ex) => ex switch
    {
        DbUpdateException { InnerException: { } inner } => inner.Message,
        _ => ex.Message,
    };

    /// <summary>True for the failures a save reports instead of throwing: the database refused the write.</summary>
    public static bool IsWriteFailure(Exception ex) => ex is DbUpdateException or ScopeViolationException;
}
