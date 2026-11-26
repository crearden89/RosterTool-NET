using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Roster.Data.Entities;

namespace Roster.Data.Scope;

/// <summary>
/// The tables' SELECT policies as EF Core global query filters. Each filter reads the
/// context's access parameters on every query, so one compiled model serves every user:
///
///   VisibleStore            store_id in (select visible_store_ids())
///   VisibleStoreViaEmployee employee_id in (employees of visible stores)
///   VisibleStoreViaShift    shift_id in (shifts of visible stores)
///   AnySignedIn             auth.uid() is not null
///   SelfOrSuperUser         id = auth.uid() or is_super_user()
///   OwnOrSuperUser          user_id = auth.uid() or is_super_user()
///   SuperUserOnly           is_super_user()
///   ServerOnly              (no policy) — server code only
///
/// The predicates themselves live on <see cref="RosterDbContext"/> (see the note there);
/// this class only fits each table's key into the right one. Deliberate cross-store reads
/// (the peer-visible pages) check the store's access first (<see cref="IStoreScope.CanPeerReadAsync"/>,
/// <see cref="IStoreScope.AccessForStoreAsync"/>) and then read through a System context.
/// </summary>
internal static class ScopeFilters
{
    public static void Apply(ModelBuilder mb, RosterDbContext ctx)
    {
        // Driven by the policy catalogue; a test checks that the finished model has exactly
        // these tables, so a table without a policy cannot slip in.
        foreach (var (type, policy) in TablePolicies.ByEntity)
        {
            var filter = policy.Read switch
            {
                ReadRule.VisibleStore when type == typeof(Store) => ctx.StoreRowVisible,
                ReadRule.VisibleStore => Keyed(type, "StoreId", ctx.StoreVisible),
                ReadRule.VisibleStoreViaEmployee => Keyed(type, "EmployeeId", ctx.EmployeeVisible),
                ReadRule.VisibleStoreViaShift => Keyed(type, "ShiftId", ctx.ShiftVisible),
                ReadRule.SelfOrSuperUser => Keyed(type, "Id", ctx.SelfOrSuperUser),
                ReadRule.OwnOrSuperUser => Keyed(type, "UserId", ctx.SelfOrSuperUser),
                ReadRule.KioskStoreOrServer => KeyedNullable(type, "StoreId", ctx.KioskStoreOrServer),
                ReadRule.AnySignedIn => Constant(type, ctx.SignedIn),
                ReadRule.SuperUserOnly => Constant(type, ctx.SuperUserOrServer),
                ReadRule.ServerOnly => Constant(type, ctx.ServerOnly),
                _ => throw new InvalidOperationException($"Unhandled read rule {policy.Read} for {type.Name}."),
            };
            mb.Entity(type).HasQueryFilter(filter);
        }
    }

    /// <summary>row => predicate(row.Key).</summary>
    private static LambdaExpression Keyed(Type type, string key, Expression<Func<Guid, bool>> predicate)
    {
        var row = Expression.Parameter(type, "row");
        var body = new Replace(predicate.Parameters[0], Expression.Property(row, key)).Visit(predicate.Body);
        return Expression.Lambda(body, row);
    }

    private static LambdaExpression KeyedNullable(Type type, string key, Expression<Func<Guid?, bool>> predicate)
    {
        var row = Expression.Parameter(type, "row");
        Expression value = Expression.Property(row, key);
        if (value.Type == typeof(Guid)) value = Expression.Convert(value, typeof(Guid?));
        var body = new Replace(predicate.Parameters[0], value).Visit(predicate.Body);
        return Expression.Lambda(body, row);
    }

    private static LambdaExpression Constant(Type type, Expression<Func<bool>> predicate) =>
        Expression.Lambda(predicate.Body, Expression.Parameter(type, "row"));

    private sealed class Replace(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
