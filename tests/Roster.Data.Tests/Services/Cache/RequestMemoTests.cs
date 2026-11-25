// Tests for the port of src/lib/requestCache.ts (no TypeScript tests cover it).

using Roster.Data.Services.Cache;

namespace Roster.Data.Tests.Services.Cache;

public class RequestMemoTests
{
    private static Func<Task<int>> Counter(Ref<int> calls) => () => Task.FromResult(++calls.Value);

    private sealed class Ref<T>(T value)
    {
        public T Value = value;
    }

    [Fact]
    public async Task Outside_a_scope_nothing_is_remembered()
    {
        var calls = new Ref<int>(0);
        Assert.Equal(1, await RequestMemo.Cached("t.outside", ["a"], Counter(calls)));
        Assert.Equal(2, await RequestMemo.Cached("t.outside", ["a"], Counter(calls)));
    }

    [Fact]
    public async Task Inside_a_scope_the_same_arguments_give_the_same_result()
    {
        var calls = new Ref<int>(0);
        await RequestMemo.Run(async () =>
        {
            Assert.Equal(1, await RequestMemo.Cached("t.same", ["a", 1.0], Counter(calls)));
            Assert.Equal(1, await RequestMemo.Cached("t.same", ["a", 1.0], Counter(calls)));
            // Strings and numbers key by value and by type: "1" is not 1.
            Assert.Equal(2, await RequestMemo.Cached("t.same", ["a", "1"], Counter(calls)));
            // Another cached function with the same arguments is its own entry.
            Assert.Equal(3, await RequestMemo.Cached("t.other", ["a", 1.0], Counter(calls)));
        });
        // The scope ended with Run.
        Assert.False(RequestMemo.Active);
        Assert.Equal(4, await RequestMemo.Cached("t.same", ["a", 1.0], Counter(calls)));
    }

    [Fact]
    public async Task Ids_and_dates_key_as_the_strings_they_were()
    {
        var calls = new Ref<int>(0);
        var id = Guid.NewGuid();
        await RequestMemo.Run(async () =>
        {
            Assert.Equal(1, await RequestMemo.Cached("t.ids", [id, new DateOnly(2026, 9, 3)], Counter(calls)));
            Assert.Equal(1, await RequestMemo.Cached("t.ids", [id.ToString("D"), "2026-09-03"], Counter(calls)));
        });
    }

    [Fact]
    public async Task Objects_key_by_identity()
    {
        var calls = new Ref<int>(0);
        var a = new List<int> { 1 };
        var b = new List<int> { 1 };
        await RequestMemo.Run(async () =>
        {
            Assert.Equal(1, await RequestMemo.Cached("t.obj", [a], Counter(calls)));
            Assert.Equal(1, await RequestMemo.Cached("t.obj", [a], Counter(calls)));
            Assert.Equal(2, await RequestMemo.Cached("t.obj", [b], Counter(calls)));
            Assert.Equal(3, await RequestMemo.Cached("t.obj", [null], Counter(calls)));
        });
    }

    [Fact]
    public async Task Nested_runs_share_the_outer_memo_and_parallel_reads_share_one_load()
    {
        var calls = new Ref<int>(0);
        await RequestMemo.Run(async () =>
        {
            Assert.Equal(1, await RequestMemo.Cached("t.nested", ["x"], Counter(calls)));
            Assert.Equal(1, await RequestMemo.Run(() => RequestMemo.Cached("t.nested", ["x"], Counter(calls))));
            var slow = 0;
            async Task<int> Load()
            {
                Interlocked.Increment(ref slow);
                await Task.Delay(20);
                return 7;
            }
            var all = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => RequestMemo.Cached("t.parallel", ["y"], Load))));
            Assert.All(all, v => Assert.Equal(7, v));
            Assert.Equal(1, slow);
        });
    }

    [Fact]
    public async Task A_failure_is_remembered_like_a_rejected_promise()
    {
        var calls = new Ref<int>(0);
        await RequestMemo.Run(async () =>
        {
            Task<int> Fail() { calls.Value++; return Task.FromException<int>(new InvalidOperationException("boom")); }
            await Assert.ThrowsAsync<InvalidOperationException>(() => RequestMemo.Cached("t.fail", ["z"], Fail));
            await Assert.ThrowsAsync<InvalidOperationException>(() => RequestMemo.Cached("t.fail", ["z"], Fail));
            Assert.Equal(1, calls.Value);
        });
    }

    [Fact]
    public async Task Begin_opens_a_scope_for_the_rest_of_the_method()
    {
        var calls = new Ref<int>(0);
        using (RequestMemo.Begin())
        {
            Assert.True(RequestMemo.Active);
            Assert.Equal(1, await RequestMemo.Cached("t.begin", ["a"], Counter(calls)));
            Assert.Equal(1, await RequestMemo.Cached("t.begin", ["a"], Counter(calls)));
        }
        Assert.False(RequestMemo.Active);
        Assert.Equal(2, await RequestMemo.Cached("t.begin", ["a"], Counter(calls)));
    }
}
