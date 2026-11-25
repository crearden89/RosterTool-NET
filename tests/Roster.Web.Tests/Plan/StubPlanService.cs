// New: an IPlanService whose actions the tests set one by one (vi.fn stand-ins); anything
// not set fails the test.

using Roster.Web.Features.Plan;

namespace Roster.Web.Tests.Plan;

internal sealed class StubPlanService : IPlanService
{
    public Func<Guid, DateOnly, Task<PlanStartResult>>? Start { get; set; }
    public Func<Guid, DateOnly, string, IReadOnlyList<DateOnly>, Task<PlanStepResult>>? Step { get; set; }
    public Func<Guid, DateOnly, string, IReadOnlyList<string>, PlanChangeReason?, Task<PlanResult>>? Finish { get; set; }
    public Func<Guid, DateOnly, DateOnly, IReadOnlyList<PlanShiftEdit>, Task<PlanResult>>? Edit { get; set; }
    public Func<Guid, DateOnly, bool, Task<PlanResult>>? SetPh { get; set; }
    public Func<Guid, DateOnly, Task<PlanResult>>? ApprovePlan { get; set; }
    public Func<Guid, DateOnly, Task<PlanResult>>? WithdrawPlan { get; set; }
    public Func<Guid, IReadOnlyList<DateOnly>, string?, Task<PlanResult>>? ApproveRoster { get; set; }
    public Func<Guid, IReadOnlyList<DateOnly>, Task<PlanResult>>? WithdrawRoster { get; set; }
    public Func<Guid, DateOnly, Task<PlanResult>>? GenerateRoster { get; set; }
    public Func<Guid, DateOnly, Task<DateTimeOffset?>>? HeldRosterAt { get; set; }
    public Func<PlanPageQuery, Task<PlanPageModel>>? Load { get; set; }

    public List<string> Calls { get; } = [];

    private static T Need<T>(T? f, string name) where T : class => f ?? throw new InvalidOperationException($"{name} was not expected");

    public Task<PlanPageModel> LoadPageAsync(PlanPageQuery query, CancellationToken cancellationToken = default) =>
        Need(Load, nameof(LoadPageAsync))(query);

    public Task<PlanStartResult> StartPlanGenerationAsync(Guid storeId, DateOnly periodStart)
    {
        Calls.Add("start");
        return Need(Start, nameof(StartPlanGenerationAsync))(storeId, periodStart);
    }

    public Task<PlanStepResult> PlanDaysStepAsync(Guid storeId, DateOnly periodStart, string runId, IReadOnlyList<DateOnly> dates)
    {
        Calls.Add("step");
        return Need(Step, nameof(PlanDaysStepAsync))(storeId, periodStart, runId, dates);
    }

    public Task<PlanResult> FinishPlanGenerationAsync(Guid storeId, DateOnly periodStart, string runId, IReadOnlyList<string> parts, PlanChangeReason? reason = null)
    {
        Calls.Add("finish");
        return Need(Finish, nameof(FinishPlanGenerationAsync))(storeId, periodStart, runId, parts, reason);
    }

    public Task<PlanResult> GenerateRosterAsync(Guid storeId, DateOnly periodStart) => Need(GenerateRoster, nameof(GenerateRosterAsync))(storeId, periodStart);

    public Task<DateTimeOffset?> HeldRosterAtAsync(Guid storeId, DateOnly periodStart) => Need(HeldRosterAt, nameof(HeldRosterAtAsync))(storeId, periodStart);

    public Task<PlanResult> EditPlanDayAsync(Guid storeId, DateOnly periodStart, DateOnly date, IReadOnlyList<PlanShiftEdit> edits) =>
        Need(Edit, nameof(EditPlanDayAsync))(storeId, periodStart, date, edits);

    public Task<PlanResult> ApprovePlanAsync(Guid storeId, DateOnly date) => Need(ApprovePlan, nameof(ApprovePlanAsync))(storeId, date);

    public Task<PlanResult> WithdrawPlanAsync(Guid storeId, DateOnly date) => Need(WithdrawPlan, nameof(WithdrawPlanAsync))(storeId, date);

    public Task<PlanResult> SetPhReallocationAsync(Guid storeId, DateOnly date, bool on) => Need(SetPh, nameof(SetPhReallocationAsync))(storeId, date, on);

    public Task<PlanResult> SetShiftLockAsync(Guid storeId, DateOnly date, Guid employeeId, bool locked, double? startMin = null, double? endMin = null) =>
        throw new InvalidOperationException("not expected");

    public Task<PlanResult> UpdateShiftAsync(Guid storeId, DateOnly date, Guid employeeId, double startMin, double endMin) =>
        throw new InvalidOperationException("not expected");

    public Task<PlanResult> SaveShiftRowAsync(Guid storeId, DateOnly date, Guid originalEmployeeId, Guid employeeId, double startMin, double endMin) =>
        throw new InvalidOperationException("not expected");

    public Task<PlanResult> RemoveShiftAsync(Guid storeId, DateOnly date, Guid employeeId) => throw new InvalidOperationException("not expected");

    public Task<PlanResult> AddShiftAsync(Guid storeId, DateOnly date, Guid employeeId, double startMin, double endMin) =>
        throw new InvalidOperationException("not expected");

    public Task<PlanResult> ApproveRosterAsync(Guid storeId, IReadOnlyList<DateOnly> dates, string? comment = null) =>
        Need(ApproveRoster, nameof(ApproveRosterAsync))(storeId, dates, comment);

    public Task<PlanResult> WithdrawRosterAsync(Guid storeId, IReadOnlyList<DateOnly> dates) => Need(WithdrawRoster, nameof(WithdrawRosterAsync))(storeId, dates);
}
