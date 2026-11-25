// New: the ON DELETE actions SQL Server cannot express. SQL Server refuses a foreign key
// that opens a second cascade path to a table (error 1785), so where Postgres cascaded
// or set null along more than one path the key is NO ACTION here (see Configurations/)
// and these methods do the same work, in one transaction, before the delete.
using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;

namespace Roster.Data;

public sealed partial class RosterDbContext
{
    /// <summary>
    /// Delete a team member (Team Members → delete). Their availability, leave, restricted
    /// tasks, requests, weekend markers and fixed roster go with them (cascade); walk notes
    /// assigned to them keep the name and lose the link (Postgres: ON DELETE SET NULL).
    /// Refused while published shifts or non-op entries still name them, as the foreign keys
    /// refused it in the live database; deactivate them instead.
    /// Returns null when deleted (or already gone), else the reason.
    /// </summary>
    public async Task<string?> DeleteEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        var employee = await Employees.FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken);
        if (employee is null) return null;
        if (await GeneratedShifts.IgnoreQueryFilters().AnyAsync(s => s.EmployeeId == employeeId, cancellationToken))
            return "This team member is on a published roster and can't be deleted. Set them inactive instead.";
        if (await NonOpEntries.IgnoreQueryFilters().AnyAsync(n => n.EmployeeId == employeeId, cancellationToken))
            return "This team member has non-op entries and can't be deleted. Set them inactive instead.";

        await using var tx = await Database.BeginTransactionAsync(cancellationToken);
        Employees.Remove(employee);
        await SaveChangesAsync(cancellationToken); // the write check runs here, before anything is changed
        await StoreWalkNotes.IgnoreQueryFilters().Where(n => n.AssignedEmployeeId == employeeId)
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.AssignedEmployeeId, (Guid?)null), cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return null;
    }

    /// <summary>
    /// Delete a store and everything that belongs to it (super user only; the TypeScript app
    /// did this only to roll back a half-created store). Other stores' pairing to it is
    /// cleared; logins of PA/DSO users that named it lose the reference. A store with
    /// Store-tier logins is refused (tier_scope would leave them without a store).
    /// </summary>
    public async Task<string?> DeleteStoreAsync(Guid storeId, CancellationToken cancellationToken = default)
    {
        if (Access.Kind != Scope.AccessKind.System && !Access.IsSuperUser) return Scope.ScopeMessages.DeniedScope;
        var store = await Stores.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == storeId, cancellationToken);
        if (store is null) return null;
        if (await AppUsers.IgnoreQueryFilters().AnyAsync(u => u.StoreId == storeId && u.Tier == Tier.Store, cancellationToken))
            return "This store still has Store logins. Move or delete them first.";

        await using var tx = await Database.BeginTransactionAsync(cancellationToken);
        await StoreAttributes.IgnoreQueryFilters().Where(a => a.PairStoreId == storeId)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.PairStoreId, (Guid?)null), cancellationToken);
        await AppUsers.IgnoreQueryFilters().Where(u => u.StoreId == storeId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.StoreId, (Guid?)null), cancellationToken);
        Stores.Remove(store);
        await SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return null;
    }

    /// <summary>
    /// The references to a login that Postgres set to null on delete, set to null. Called by
    /// <see cref="Users.IUserAdmin.DeleteAsync"/> inside its transaction, after its checks.
    /// </summary>
    internal async Task DeleteUserReferencesAsync(Guid userId, CancellationToken ct)
    {
        Guid? none = null;
        await UploadBatches.IgnoreQueryFilters().Where(x => x.UploadedBy == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.UploadedBy, none), ct);
        await GeneratedShifts.IgnoreQueryFilters().Where(x => x.LockedBy == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.LockedBy, none), ct);
        await RosterAudit.IgnoreQueryFilters().Where(x => x.UserId == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.UserId, none), ct);
        await RosterReviews.IgnoreQueryFilters().Where(x => x.SubmittedBy == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.SubmittedBy, none), ct);
        await RosterReviews.IgnoreQueryFilters().Where(x => x.DecidedBy == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.DecidedBy, none), ct);
        await RosterReviewChecks.IgnoreQueryFilters().Where(x => x.CheckedBy == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.CheckedBy, none), ct);
        await RosterReviewDays.IgnoreQueryFilters().Where(x => x.DecidedBy == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.DecidedBy, none), ct);
        await RosterReviewEvents.IgnoreQueryFilters().Where(x => x.ActorId == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.ActorId, none), ct);
    }
}
