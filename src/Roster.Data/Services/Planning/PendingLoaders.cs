// New: the loaders whose engine halves have not landed yet. They keep the
// contracts callable (and registered) until then; each says what it waits for.

using Roster.Engine.Planning.Plan;
using Roster.Engine.Planning.Stage1;

namespace Roster.Data.Services.Planning;

/// <summary>planSlots' dayCheckOptions: waits for E6's PlanSlots.DayCheckOptions.</summary>
public sealed class PendingDayCheckOptionsLoader : IDayCheckOptionsLoader
{
    public Task<DayCheckOptions> DayCheckOptionsAsync(Guid storeId, DateOnly date, CancellationToken ct = default) =>
        throw new NotImplementedException("dayCheckOptions needs E6's PlanSlots.DayCheckOptions (lib/stage2/planSlots).");
}

/// <summary>computePlanShifts: waits for E6's day slots, E5's painter and E3's till and labels.</summary>
public sealed class PendingPlanShiftsLoader : IPlanShiftsLoader
{
    public Task<PlanShiftComputation> ComputePlanShiftsAsync(Guid storeId, DateOnly date, CancellationToken ct = default) =>
        throw new NotImplementedException("computePlanShifts needs E6's PlanSlots, E5's painter and E3's till and sub-task labels.");
}
