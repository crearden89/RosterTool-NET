// Port of src/lib/plan/generateParts.test.ts (generating a fortnight in steps). Its "one
// read per request" cases test lib/requestCache, which is D2's (Services/Cache).

using System.Buffers.Text;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Roster.Web.Features.Plan;

namespace Roster.Web.Tests.Plan;

public class GeneratePartsTests
{
    private sealed record Day(DateOnly Date, bool Ok, bool HasData);

    private static readonly byte[] Key = GenerateParts.SigningKey("test-only-secret");
    private static readonly PartStamp Stamp = new(Guid.Parse("00000000-0000-4000-8000-0000000000a1"), new(2026, 11, 23), "run-1", Guid.Parse("00000000-0000-4000-8000-0000000000b1"));
    private static readonly DateOnly[] Dates = [new(2026, 11, 23), new(2026, 11, 24), new(2026, 11, 25)];
    private static readonly long Now = DateTimeOffset.Parse("2026-11-20T09:00:00Z").ToUnixTimeMilliseconds();

    private static Day D(DateOnly date) => new(date, true, true);

    private static string Seal(PartStamp stamp, params Day[] days) => GenerateParts.SealPlanPart(Key, stamp, days, Now);

    private static OpenedPlanParts<Day> Open(IReadOnlyList<string?> parts, long? now = null) =>
        GenerateParts.OpenPlanParts<Day>(Key, parts, Stamp, Dates, d => d.Date, now ?? Now);

    [Fact]
    public void Splits_the_fortnight_into_steps_of_a_few_days_in_order()
    {
        var fortnight = Enumerable.Range(0, 14).Select(i => new DateOnly(2026, 11, 23).AddDays(i)).ToList();
        var steps = GenerateParts.PlanSteps(fortnight);
        Assert.Equal(fortnight, steps.SelectMany(s => s));
        Assert.Equal(GenerateParts.DaysPerStep, steps.Max(s => s.Count));
    }

    [Fact]
    public void Puts_the_sealed_parts_back_together_as_the_whole_fortnight_in_date_order()
    {
        var opened = Open([Seal(Stamp, D(Dates[2])), Seal(Stamp, D(Dates[0]), D(Dates[1]))]);
        Assert.True(opened.Ok);
        Assert.Equal(Dates, opened.Days!.Select(d => d.Date));
    }

    [Fact]
    public void Refuses_a_fortnight_with_a_day_missing_or_twice_never_half_a_plan()
    {
        var two = Seal(Stamp, D(Dates[0]), D(Dates[1]));
        Assert.False(Open([two]).Ok);
        Assert.False(Open([two, two, Seal(Stamp, D(Dates[2]))]).Ok);
    }

    [Fact]
    public void Refuses_a_part_that_was_changed_on_the_way()
    {
        var part = Seal(Stamp, D(Dates[0]), D(Dates[1]), D(Dates[2]));
        var pieces = part.Split('.');
        var (data, sig) = (pieces[0], pieces[1]);
        // The same days with one changed, under the original signature.
        using var gz = new GZipStream(new MemoryStream(Base64Url.DecodeFromChars(data)), CompressionMode.Decompress);
        var body = JsonNode.Parse(gz)!;
        body["days"]![0]!["ok"] = false;
        using var ms = new MemoryStream();
        using (var w = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) w.Write(System.Text.Encoding.UTF8.GetBytes(body.ToJsonString()));
        var changed = Base64Url.EncodeToString(ms.ToArray());
        Assert.False(Open([$"{changed}.{sig}"]).Ok);
        Assert.False(Open([$"{data}.{new string('x', sig.Length)}"]).Ok);
        Assert.False(Open(["nonsense"]).Ok);
        Assert.Equal("A step of the plan could not be checked. Press Generate again.", Open(["nonsense"]).Error);
    }

    [Fact]
    public void Refuses_a_part_of_another_store_fortnight_run_or_person()
    {
        var all = new[] { D(Dates[0]), D(Dates[1]), D(Dates[2]) };
        foreach (var other in new[]
        {
            Stamp with { StoreId = Guid.Parse("00000000-0000-4000-8000-0000000000a2") },
            Stamp with { PeriodStart = new DateOnly(2026, 11, 9) },
            Stamp with { RunId = "run-2" },
            Stamp with { UserId = Guid.Parse("00000000-0000-4000-8000-0000000000b2") },
        })
        {
            Assert.False(Open([Seal(other, all)]).Ok);
        }
    }

    [Fact]
    public void Refuses_parts_too_old_to_trust()
    {
        var part = GenerateParts.SealPlanPart(Key, Stamp, [D(Dates[0]), D(Dates[1]), D(Dates[2])], Now - 16 * 60 * 1000);
        var opened = Open([part], Now);
        Assert.False(opened.Ok);
        Assert.Equal("Generating took too long and its first days may be out of date. Press Generate again.", opened.Error);
    }

    [Fact]
    public void Says_which_way_a_fortnight_is_short()
    {
        Assert.Equal("Some days of the fortnight were not planned. Press Generate again.", Open([Seal(Stamp, D(Dates[0]))]).Error);
        Assert.Throws<InvalidOperationException>(() => GenerateParts.SigningKey(""));
    }
}
