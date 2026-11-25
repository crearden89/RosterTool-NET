// Port of the TradeCurveWeek type in src/components/plan/TradeCurveCard.tsx.

using Roster.Engine.Planning.Plan;

namespace Roster.Web.Features.Plan;

/// <summary>
/// Seven hourly curves, index 0 = Monday. Built server-side from the store's derived trade
/// curve so the card stays pure presentation.
/// </summary>
public sealed record TradeCurveWeek(IReadOnlyList<IReadOnlyList<CurvePoint>> ByDow);
