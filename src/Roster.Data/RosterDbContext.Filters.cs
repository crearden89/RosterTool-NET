using System.Linq.Expressions;
using Roster.Data.Entities;

namespace Roster.Data;

public sealed partial class RosterDbContext
{
    // The filter predicates are written here, as instance members, on purpose: EF Core
    // recognises a reference to the context instance ("this") inside a query filter and
    // swaps in the context running the query. A predicate built elsewhere would capture
    // one particular instance and filter every later query by that instance's user.

    /// <summary>visible_store_ids() for a store id.</summary>
    internal Expression<Func<Guid, bool>> StoreVisible =>
        id => QfUnrestricted || QfAllStores || id == QfStoreId
              || (QfAreaId != null && Set<Store>().Any(st => st.Id == id && st.AreaId == QfAreaId));

    /// <summary>An employee of a visible store (frc_all, 0025).</summary>
    internal Expression<Func<Guid, bool>> EmployeeVisible =>
        id => QfUnrestricted || QfAllStores
              || Set<Employee>().Any(e => e.Id == id
                  && (e.StoreId == QfStoreId
                      || (QfAreaId != null && Set<Store>().Any(st => st.Id == e.StoreId && st.AreaId == QfAreaId))));

    /// <summary>A published shift of a visible store.</summary>
    internal Expression<Func<Guid, bool>> ShiftVisible =>
        id => QfUnrestricted || QfAllStores
              || Set<GeneratedShift>().Any(s => s.Id == id
                  && (s.StoreId == QfStoreId
                      || (QfAreaId != null && Set<Store>().Any(st => st.Id == s.StoreId && st.AreaId == QfAreaId))));

    /// <summary>The stores table itself (keyed on id, so no sub-query).</summary>
    internal Expression<Func<Store, bool>> StoreRowVisible =>
        s => QfUnrestricted || QfAllStores || s.Id == QfStoreId || (QfAreaId != null && s.AreaId == QfAreaId);

    internal Expression<Func<Guid, bool>> SelfOrSuperUser => id => QfUnrestricted || QfSuperUser || id == QfUserId;

    internal Expression<Func<Guid?, bool>> KioskStoreOrServer => id => QfUnrestricted || id == QfKioskStoreId;

    internal Expression<Func<bool>> SignedIn => () => QfSignedIn;

    internal Expression<Func<bool>> SuperUserOrServer => () => QfUnrestricted || QfSuperUser;

    internal Expression<Func<bool>> ServerOnly => () => QfUnrestricted;
}
