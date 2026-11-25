// Port of the `{ ok: boolean; error?: string }` result that every action in
// src/app/(app)/admin/rules/*/actions.ts and src/app/(app)/admin/process/*/actions.ts returns.

namespace Roster.Web.Features.Rules;

/// <summary>What an action did: saved, or the reason it was refused (shown under the form).</summary>
public sealed record SaveResult(bool Ok, string? Error = null)
{
    public static readonly SaveResult Success = new(true);

    public static SaveResult Fail(string error) => new(false, error);
}
