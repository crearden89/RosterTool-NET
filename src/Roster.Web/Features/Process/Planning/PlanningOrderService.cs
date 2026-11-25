// Port of src/app/(app)/admin/process/planning/actions.ts and the reads in its page.tsx.

using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Engine.Core;
using Roster.Engine.Planning.Plan;
using Roster.Web.Features.Rules;

namespace Roster.Web.Features.Process.Planning;

public sealed class PlanningOrderService(IRosterDbContextFactory contexts, IStoreScope scope, TimeProvider clock) : IPlanningOrderService
{
    public async Task<PlanningOrderData> LoadAsync(CancellationToken cancellationToken = default)
    {
        SuperUserGuard.Require(scope);
        await using var db = contexts.Create();
        var rows = await db.PlanPlacementRules.AsNoTracking().OrderBy(r => r.Position).ToListAsync(cancellationToken);
        var s = await db.PlanPlacementSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

        var saved = rows.Select(r => new PlacementRule
        {
            Group = r.LabourGroup,
            Strategy = r.Strategy,
            AnchorKind = r.AnchorKind,
            AnchorTime = r.AnchorTime is { } t ? Minutes(t) : null,
            AnchorOffsetMin = r.AnchorOffsetMin,
            PrePeakPct = r.PrePeakPct,
            PostPeakPct = r.PostPeakPct,
            AvoidStrength = r.AvoidStrength,
            NightTraderStrategy = r.NightTraderStrategy,
        }).ToList();

        var settings = s is null
            ? Placement.DefaultSettings
            : new PlacementSettings
            {
                Enabled = s.Enabled,
                PeakBasis = s.PeakBasis,
                PeakSmoothingBlocks = s.PeakSmoothingBlocks,
                PeakThresholdPct = s.PeakThresholdPct,
                NightTraderFromMin = Minutes(s.NightTraderFrom),
                NightTraderSharePct = s.NightTraderSharePct,
            };

        return new PlanningOrderData(saved, settings, PlanningOrderChecks.StepOrderFrom(s?.StepOrder));
    }

    public async Task<SaveResult> SavePlacementAsync(IReadOnlyList<PlacementRule> rules, PlacementSettings settings, CancellationToken cancellationToken = default)
    {
        SuperUserGuard.Require(scope);
        if (PlanningOrderChecks.CheckRules(rules) is { } error) return SaveResult.Fail(error);

        await using var db = contexts.Create();
        db.PlanPlacementRules.RemoveRange(await db.PlanPlacementRules.ToListAsync(cancellationToken));
        if (await SaveAsync(db, cancellationToken) is { Ok: false } deleted) return deleted;

        if (rules.Count > 0)
        {
            var now = clock.GetUtcNow();
            for (var i = 0; i < rules.Count; i++)
            {
                var r = rules[i];
                TimeOnly? anchorTime = null;
                if (r.AnchorKind == "clock" && r.AnchorTime is { } at)
                {
                    var (time, timeError) = PlanningOrderChecks.SqlTime(at);
                    if (timeError is not null) return SaveResult.Fail(timeError);
                    anchorTime = time;
                }
                db.PlanPlacementRules.Add(new PlanPlacementRule
                {
                    Position = i,
                    LabourGroup = r.Group,
                    Strategy = r.Strategy,
                    AnchorKind = r.AnchorKind,
                    AnchorTime = anchorTime,
                    AnchorOffsetMin = (int)Js.Round(double.IsNaN(r.AnchorOffsetMin) ? 0 : r.AnchorOffsetMin),
                    PrePeakPct = PlanningOrderChecks.Pct(r.PrePeakPct),
                    PostPeakPct = PlanningOrderChecks.Pct(r.PostPeakPct),
                    AvoidStrength = PlanningOrderChecks.Pct(r.AvoidStrength),
                    NightTraderStrategy = r.NightTraderStrategy,
                    UpdatedAt = now,
                });
            }
            if (await SaveAsync(db, cancellationToken) is { Ok: false } inserted) return inserted;
        }

        var row = await db.PlanPlacementSettings.FirstOrDefaultAsync(cancellationToken);
        if (row is not null)
        {
            // enabled is deliberately not part of this save - see SetPlacementEnabledAsync.
            var blocks = PlanningOrderChecks.SmoothingBlocks(settings.PeakSmoothingBlocks);
            if (!double.IsFinite(blocks)) return SaveResult.Fail($"invalid input syntax for type integer: \"{Js.NumberToString(blocks)}\"");
            var (from, fromError) = PlanningOrderChecks.SqlTime(settings.NightTraderFromMin);
            if (fromError is not null) return SaveResult.Fail(fromError);
            row.PeakBasis = settings.PeakBasis;
            row.PeakSmoothingBlocks = (int)blocks;
            row.PeakThresholdPct = PlanningOrderChecks.ThresholdPct(settings.PeakThresholdPct);
            row.NightTraderFrom = from!.Value;
            row.NightTraderSharePct = PlanningOrderChecks.Pct(settings.NightTraderSharePct) ?? 0;
            row.UpdatedAt = clock.GetUtcNow();
            if (await SaveAsync(db, cancellationToken) is { Ok: false } updated) return updated;
        }
        return SaveResult.Success;
    }

    public Task<SaveResult> ResetPlacementAsync(PlacementSettings settings, CancellationToken cancellationToken = default) =>
        SavePlacementAsync([], settings, cancellationToken);

    public async Task<SaveResult> SetPlacementEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        SuperUserGuard.Require(scope);
        await using var db = contexts.Create();
        var row = await db.PlanPlacementSettings.FirstOrDefaultAsync(cancellationToken);
        if (row is null) return SaveResult.Success;
        row.Enabled = enabled;
        row.UpdatedAt = clock.GetUtcNow();
        return await SaveAsync(db, cancellationToken);
    }

    public async Task<SaveResult> SaveStepOrderAsync(IReadOnlyList<string> order, CancellationToken cancellationToken = default)
    {
        SuperUserGuard.Require(scope);
        var (clean, error) = PlanningOrderChecks.CheckStepOrder(order);
        if (error is not null) return SaveResult.Fail(error);
        await using var db = contexts.Create();
        var row = await db.PlanPlacementSettings.FirstOrDefaultAsync(cancellationToken);
        if (row is null) return SaveResult.Success;
        row.StepOrder = [.. clean!];
        return await SaveAsync(db, cancellationToken);
    }

    // timeToMin of a stored time: whole minutes from midnight (the seconds are ignored).
    private static double Minutes(TimeOnly t) => t.Hour * 60 + t.Minute;

    internal static async Task<SaveResult> SaveAsync(RosterDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return SaveResult.Success;
        }
        catch (Exception ex) when (SuperUserGuard.IsWriteFailure(ex))
        {
            return SaveResult.Fail(SuperUserGuard.MessageOf(ex));
        }
    }
}
