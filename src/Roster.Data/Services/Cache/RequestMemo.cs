// Port of src/lib/requestCache.ts.

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Roster.Engine.Core;

namespace Roster.Data.Services.Cache;

// ─── One read per request, in server actions too ─────────────────────────────
// React's cache() only remembers inside a page render. A server action runs
// before any render, so there every cached read (the store's benchmarks,
// trading hours, rules, team ...) went to the database again for each day and
// each step that asked: about 1,400 reads to generate one fortnight instead of
// about 125. That is most of the time Generate took in the deployed app.
//
// Cached() below remembers for the length of one memo scope: Run() - the plan
// and roster builds - or Begin(), which a page opens around its load (a page
// load is what a React render was). Inside a scope the same arguments
// (strings, numbers, dates and ids by value, other objects by identity, as
// React compares them) give the same result. Outside one nothing is
// remembered: every read goes to the database, as a server action's did.
//
// The memo flows with the async context (AsyncLocal, the AsyncLocalStorage of
// the TypeScript), so the reads a scope starts - and the reads those start, in
// parallel or not - share it, while another request or circuit never sees it.

/// <summary>Per-request memoisation of reads (lib/requestCache).</summary>
public static class RequestMemo
{
    private sealed class Memo
    {
        // fn → (args key → the remembered result, a Lazy<Task<R>>).
        public readonly ConcurrentDictionary<string, ConcurrentDictionary<string, object>> ByFn = new(StringComparer.Ordinal);
    }

    private static readonly AsyncLocal<Memo?> Scope = new();

    /// <summary>True while a memo scope is open on this async flow.</summary>
    public static bool Active => Scope.Value is not null;

    /// <summary>
    /// <c>withRequestMemo</c>: run <paramref name="fn"/> with every cached read
    /// remembered until it finishes. Nested calls share the outer memo.
    /// </summary>
    public static Task<T> Run<T>(Func<Task<T>> fn)
    {
        ArgumentNullException.ThrowIfNull(fn);
        if (Scope.Value is not null) return fn();
        return InNewScope(fn);
    }

    /// <inheritdoc cref="Run{T}(Func{Task{T}})"/>
    public static Task Run(Func<Task> fn)
    {
        ArgumentNullException.ThrowIfNull(fn);
        return Run(async () =>
        {
            await fn();
            return true;
        });
    }

    // An async method's change to an AsyncLocal is seen by everything it calls
    // and awaits, and undone for its caller when it returns.
    private static async Task<T> InNewScope<T>(Func<Task<T>> fn)
    {
        Scope.Value = new Memo();
        return await fn();
    }

    /// <summary>
    /// Opens a memo scope for the rest of the calling method (a page's load, the
    /// React render): <c>using var _ = RequestMemo.Begin();</c>. Nested: shares
    /// the scope already open. Disposing closes only a scope this call opened.
    /// </summary>
    public static IDisposable Begin()
    {
        if (Scope.Value is not null) return NoScope.Instance;
        var memo = new Memo();
        Scope.Value = memo;
        return new OpenedScope(memo);
    }

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();
        public void Dispose() { }
    }

    private sealed class OpenedScope(Memo memo) : IDisposable
    {
        public void Dispose()
        {
            if (ReferenceEquals(Scope.Value, memo)) Scope.Value = null;
        }
    }

    /// <summary>
    /// <c>cache(fn)(...args)</c>: inside a memo scope, the first call with these
    /// arguments runs <paramref name="load"/> and every later one gets the same
    /// task (a failure included, as a rejected promise was); outside a scope it
    /// just runs <paramref name="load"/>. <paramref name="fn"/> names the cached
    /// function - one name per function, as the function's identity was.
    /// </summary>
    public static Task<TResult> Cached<TResult>(string fn, IReadOnlyList<object?> args, Func<Task<TResult>> load)
    {
        ArgumentNullException.ThrowIfNull(fn);
        ArgumentNullException.ThrowIfNull(load);
        var memo = Scope.Value;
        if (memo is null) return load();
        var byArgs = memo.ByFn.GetOrAdd(fn, _ => new ConcurrentDictionary<string, object>(StringComparer.Ordinal));
        var entry = byArgs.GetOrAdd(KeyOf(args), _ => new Lazy<Task<TResult>>(load, LazyThreadSafetyMode.ExecutionAndPublication));
        if (entry is not Lazy<Task<TResult>> lazy)
            throw new InvalidOperationException($"RequestMemo: '{fn}' was cached with another result type ({entry.GetType()}).");
        return lazy.Value;
    }

    /// <inheritdoc cref="Cached{TResult}(string, IReadOnlyList{object?}, Func{Task{TResult}})"/>
    public static Task<TResult> Cached<TResult>(string fn, Func<Task<TResult>> load) => Cached(fn, [], load);

    // ── The arguments' key ───────────────────────────────────────────────────
    // Primitives by value ("string:2026-09-03", "number:15"), every other
    // object by identity - the same rule as keyOf in the TypeScript. The ids,
    // dates and times the C# passes where the TypeScript passed strings key as
    // the strings they were.

    private static readonly ConditionalWeakTable<object, object> ObjectIds = new();
    private static long _nextObjectId;

    internal static string KeyOf(IReadOnlyList<object?> args) => string.Join("|", args.Select(KeyOfOne));

    private static string KeyOfOne(object? a) => a switch
    {
        null => "object:null",
        string s => "string:" + s,
        Guid g => "string:" + g.ToString("D"),
        DateOnly d => "string:" + d.ToIso(),
        TimeOnly t => "string:" + t.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        bool b => b ? "boolean:true" : "boolean:false",
        double x => "number:" + Js.NumberToString(x),
        float x => "number:" + Js.NumberToString(x),
        int x => "number:" + Js.NumberToString(x),
        long x => "number:" + Js.NumberToString(x),
        decimal x => "number:" + Js.NumberToString((double)x),
        Enum e => "string:" + e,
        _ => "o" + ObjectIds.GetValue(a, _ => Interlocked.Increment(ref _nextObjectId)),
    };
}
