// Port of the shared pieces of src/app/(app)/admin/leave-liability/LeaveSettings.tsx.

using Roster.Engine.Core;

namespace Roster.Web.Features.LeaveLiability;

public static class LeaveSettingsShared
{
    // A number field's draft counts as a change when it no longer reads as the
    // saved number (so "76" and "76.0" are the same, and an emptied box is not).
    public static bool Differs(string value, string saved) => Js.Trim(value) == "" || Js.Number(value) != Js.Number(saved);
}

/// <summary>The Targets form's draft: the target and the FTE divisor, as typed.</summary>
public sealed record TargetsDraft(string T, string F);
