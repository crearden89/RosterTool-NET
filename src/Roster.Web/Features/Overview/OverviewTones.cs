// New: the engine's tones and step states as the design system's own (the TypeScript
// shared one PillTone/Step type between lib/overview and components/ui).

using Roster.Engine.Overview;
using Roster.Web.Components.Ui;

namespace Roster.Web.Features.Overview;

internal static class OverviewTones
{
    public static PillTone Pill(ChipTone tone) => tone switch
    {
        ChipTone.Navy => PillTone.Navy,
        ChipTone.Soft => PillTone.Soft,
        ChipTone.Violet => PillTone.Violet,
        ChipTone.Gold => PillTone.Gold,
        ChipTone.Amber => PillTone.Amber,
        ChipTone.Green => PillTone.Green,
        ChipTone.Red => PillTone.Red,
        _ => PillTone.Grey,
    };

    public static Step Step(FortnightStep s) => new(s.Key, s.Title, s.Detail, s.State switch
    {
        StepStatus.Done => StepState.Done,
        StepStatus.Current => StepState.Current,
        StepStatus.Attention => StepState.Attention,
        _ => StepState.Todo,
    });
}
