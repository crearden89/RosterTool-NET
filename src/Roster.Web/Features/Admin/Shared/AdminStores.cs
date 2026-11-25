// New: the store list the Super User pages' store pickers show
// (stores.select("id, code, name").order("code")), and the random tokens the
// kiosk links and data feed issue (crypto.randomBytes(n).toString("hex")).

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Roster.Data;

namespace Roster.Web.Features.Admin.Shared;

/// <summary>A store as the admin pickers write it: "G101 - Northside".</summary>
public sealed record StoreChoice(Guid Id, string Code, string Name)
{
    public string Label => $"{Code} - {Name}";
}

public static class AdminStores
{
    /// <summary>Every store the context can read, by code.</summary>
    public static async Task<IReadOnlyList<StoreChoice>> ListAsync(RosterDbContext db, CancellationToken ct = default) =>
        await db.Stores.AsNoTracking()
            .OrderBy(s => s.Code)
            .Select(s => new StoreChoice(s.Id, s.Code, s.Name))
            .ToListAsync(ct);

    /// <summary>randomBytes(bytes).toString("hex"): 2 × bytes lower-case hex digits.</summary>
    public static string RandomHex(int bytes) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));
}
