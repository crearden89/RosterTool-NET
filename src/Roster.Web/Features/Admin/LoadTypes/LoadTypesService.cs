// Port of src/app/(app)/admin/load-types/page.tsx (its read) and actions.ts.

using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Engine.Core;
using Roster.Engine.SubTasks;
using Roster.Web.Features.Admin.Shared;

namespace Roster.Web.Features.Admin.LoadTypes;

public sealed record LoadTypeRow(
    Guid Id,
    string Name,
    double MinutesPerPallet,
    double? MinutesPerPalletLt90,
    double? MinutesPerPalletLt60,
    double? MinutesPerPalletLt30,
    string Color,
    int Priority,
    string? SubTask);

/// <summary>Minutes per pallet for the probation bands. Null = same as fully trained.</summary>
public sealed record TenureMinutes(double? Lt90, double? Lt60, double? Lt30)
{
    public static readonly TenureMinutes Trained = new(null, null, null);
}

public interface ILoadTypesService
{
    /// <summary>Every load type, in sort order.</summary>
    Task<IReadOnlyList<LoadTypeRow>> LoadAsync(CancellationToken ct = default);

    Task<AdminResult> AddLoadTypeAsync(string name, double minutesPerPallet, string color, CancellationToken ct = default);

    /// <param name="subTask">
    /// Which Load Running sub-task the type counts under ("" = by its name).
    /// Null leaves the saved mapping untouched.
    /// </param>
    Task<AdminResult> UpdateLoadTypeAsync(
        Guid id, string name, double minutesPerPallet, string color, TenureMinutes? tenure = null, double priority = 100,
        string? subTask = null, CancellationToken ct = default);

    Task<AdminResult> DeleteLoadTypeAsync(Guid id, CancellationToken ct = default);
}

internal sealed partial class LoadTypesService(IRosterDbContextFactory contexts, IStoreScope scope) : ILoadTypesService
{
    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex Hex();

    public async Task<IReadOnlyList<LoadTypeRow>> LoadAsync(CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        await using var db = contexts.Create();
        return await db.LoadTypes.AsNoTracking()
            .OrderBy(t => t.SortOrder)
            .Select(t => new LoadTypeRow(
                t.Id, t.Name, t.MinutesPerPallet, t.MinutesPerPalletLt90, t.MinutesPerPalletLt60, t.MinutesPerPalletLt30,
                t.Color, t.Priority, t.SubTask))
            .ToListAsync(ct);
    }

    /// <summary>The checks addLoadType and updateLoadType share, in their order.</summary>
    public static string? ValidateBasics(string name, double minutesPerPallet, string color)
    {
        if (Js.Trim(name).Length == 0) return "Name is required";
        if (!(minutesPerPallet > 0)) return "Minutes must be positive";
        if (!Hex().IsMatch(color)) return "Colour must be a hex value like #2563eb";
        return null;
    }

    /// <summary>updateLoadType's checks after the shared ones; null when the row may be saved.</summary>
    public static string? ValidateUpdate(string name, double minutesPerPallet, string color, TenureMinutes tenure, double priority, string? subTask)
    {
        if (ValidateBasics(name, minutesPerPallet, color) is { } basic) return basic;
        if (!double.IsFinite(priority) || priority < 1 || priority > 999) return "Priority must be between 1 and 999";
        // Null is meaningful here (charge this band the trained rate); a zero or
        // negative is not - the DB check would reject it, so say so plainly instead.
        foreach (var (band, v) in new (string, double?)[] { ("lt90", tenure.Lt90), ("lt60", tenure.Lt60), ("lt30", tenure.Lt30) })
            if (v is { } m && !(m > 0)) return $"{band} minutes must be positive";
        if (!string.IsNullOrEmpty(subTask) && !Catalogue.IsLoadPart(subTask)) return "Unknown sub-task";
        return null;
    }

    public async Task<AdminResult> AddLoadTypeAsync(string name, double minutesPerPallet, string color, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        if (ValidateBasics(name, minutesPerPallet, color) is { } error) return AdminResult.Fail(error);
        await using var db = contexts.CreateSystem("Load Types: add a type");
        var max = await db.LoadTypes.OrderByDescending(t => t.SortOrder).Select(t => (int?)t.SortOrder).FirstOrDefaultAsync(ct);
        db.LoadTypes.Add(new LoadType
        {
            Name = Js.Trim(name),
            MinutesPerPallet = minutesPerPallet,
            Color = color,
            SortOrder = (max ?? 0) + 1,
        });
        return await AdminDb.WriteAsync(() => db.SaveChangesAsync(ct));
    }

    public async Task<AdminResult> UpdateLoadTypeAsync(
        Guid id, string name, double minutesPerPallet, string color, TenureMinutes? tenure = null, double priority = 100,
        string? subTask = null, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        tenure ??= TenureMinutes.Trained;
        if (ValidateUpdate(name, minutesPerPallet, color, tenure, priority, subTask) is { } error) return AdminResult.Fail(error);
        await using var db = contexts.CreateSystem("Load Types: save a type");
        var row = await db.LoadTypes.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (row is null) return AdminResult.Success; // update ... eq(id): no row, nothing to do
        if (subTask is not null) row.SubTask = subTask.Length == 0 ? null : subTask;
        row.Name = Js.Trim(name);
        row.MinutesPerPallet = minutesPerPallet;
        row.Color = color;
        row.Priority = (int)Js.Round(priority);
        row.MinutesPerPalletLt90 = tenure.Lt90;
        row.MinutesPerPalletLt60 = tenure.Lt60;
        row.MinutesPerPalletLt30 = tenure.Lt30;
        return await AdminDb.WriteAsync(() => db.SaveChangesAsync(ct));
    }

    public async Task<AdminResult> DeleteLoadTypeAsync(Guid id, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        await using var db = contexts.CreateSystem("Load Types: delete a type");
        try
        {
            await db.LoadTypes.Where(t => t.Id == id).ExecuteDeleteAsync(ct);
            return AdminResult.Success;
        }
        catch (Exception e) when (AdminDb.IsRefusal(e))
        {
            // The raw FK message ("violates foreign key constraint...") helps nobody.
            var message = AdminDb.MessageOf(e);
            if (AdminDb.IsForeignKeyRefusal(message))
                return AdminResult.Fail("This load type has recorded loads against it and can't be deleted.");
            return AdminResult.Fail(message);
        }
    }
}

public sealed class LoadTypesModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddScoped<ILoadTypesService, LoadTypesService>();
}
