// Port of src/lib/plan/generateParts.ts.
// ─── Generate a fortnight in steps ───────────────────────────────────────────
// Generating a whole fortnight in one request ran into the server's time limit
// on the largest store (the page showed "Something went wrong" although the
// plan had been saved). The Plan page now asks for a few days at a time, one
// request after another, and shows its progress. Each step hands back its days
// as a sealed part: compressed, and signed with a key only the server holds,
// so the caller can carry it but not change it. The last step opens every
// part, checks they are this run's, this store's and this fortnight's and
// cover every day exactly once, and holds the fortnight in ONE write - the
// page never shows half a fortnight as finished.
//
// Generic over the day: the held plan's day (the snapshot's day, D's type) in the
// service, a small record in the tests. Each day must say its date.

using System.Buffers.Text;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Roster.Engine.Core;

namespace Roster.Web.Features.Plan;

/// <summary>Whose run a part belongs to: the store, fortnight, run and person it was planned for.</summary>
public sealed record PartStamp(Guid StoreId, DateOnly PeriodStart, string RunId, Guid UserId);

/// <summary>What opening a run's parts gives: the fortnight's days in date order, or why they were refused.</summary>
public sealed record OpenedPlanParts<TDay>(bool Ok, IReadOnlyList<TDay>? Days = null, string? Error = null);

public static class GenerateParts
{
    /// <summary>Days per request: the slowest store-days take about 4 seconds each.</summary>
    public const int DaysPerStep = 2;

    /// <summary>A part older than this (milliseconds) is refused: the inputs may have changed since.</summary>
    public const long PartMaxAgeMs = 15 * 60 * 1000;

    private const string Broken = "A step of the plan could not be checked. Press Generate again.";
    private const string TooOld = "Generating took too long and its first days may be out of date. Press Generate again.";
    private const string Missing = "Some days of the fortnight were not planned. Press Generate again.";

    /// <summary>
    /// A key of its own, derived from the server secret: the secret itself is never used for
    /// anything but this derivation here.
    /// </summary>
    public static byte[] SigningKey(string secret)
    {
        if (string.IsNullOrEmpty(secret)) throw new InvalidOperationException("The server has no key to seal plan steps with.");
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes("plan-generate-part-v1"));
    }

    private static byte[] Mac(byte[] key, byte[] data) => HMACSHA256.HashData(key, data);

    /// <summary>Seal one step's days for the caller to carry to the last step.</summary>
    public static string SealPlanPart<TDay>(byte[] key, PartStamp stamp, IReadOnlyList<TDay> days, long nowMs)
    {
        var body = new PartBody<TDay>
        {
            StoreId = stamp.StoreId,
            PeriodStart = stamp.PeriodStart,
            RunId = stamp.RunId,
            UserId = stamp.UserId,
            IssuedAt = nowMs,
            Days = [.. days],
        };
        var data = Gzip(JsonSerializer.SerializeToUtf8Bytes(body, RosterJson.Options));
        return $"{Base64Url.EncodeToString(data)}.{Base64Url.EncodeToString(Mac(key, data))}";
    }

    /// <summary>
    /// Open every part of a run and put the fortnight together. Refused (with a plain reason)
    /// unless every part is genuine, recent, of this run, store, fortnight and person, and the
    /// days are exactly <paramref name="dates"/>, each once.
    /// </summary>
    public static OpenedPlanParts<TDay> OpenPlanParts<TDay>(
        byte[] key,
        IReadOnlyList<string?> parts,
        PartStamp stamp,
        IReadOnlyList<DateOnly> dates,
        Func<TDay, DateOnly> dateOf,
        long nowMs)
    {
        var byDate = new Dictionary<DateOnly, TDay>();
        var broken = new OpenedPlanParts<TDay>(false, Error: Broken);
        foreach (var part in parts)
        {
            if (part is null) return broken;
            var pieces = part.Split('.');
            if (pieces.Length != 2 || pieces[0].Length == 0 || pieces[1].Length == 0) return broken;
            byte[] data, given;
            try
            {
                data = Base64Url.DecodeFromChars(pieces[0]);
                given = Base64Url.DecodeFromChars(pieces[1]);
            }
            catch (FormatException)
            {
                return broken;
            }
            var want = Mac(key, data);
            if (given.Length != want.Length || !CryptographicOperations.FixedTimeEquals(given, want)) return broken;
            PartBody<TDay>? body;
            try
            {
                body = JsonSerializer.Deserialize<PartBody<TDay>>(Gunzip(data), RosterJson.Options);
            }
            catch (Exception e) when (e is JsonException or InvalidDataException or NotSupportedException)
            {
                return broken;
            }
            if (body is null
                || body.StoreId != stamp.StoreId
                || body.PeriodStart != stamp.PeriodStart
                || body.RunId != stamp.RunId
                || body.UserId != stamp.UserId)
            {
                return broken;
            }
            if (!(nowMs - body.IssuedAt <= PartMaxAgeMs && body.IssuedAt <= nowMs + 60_000))
                return new OpenedPlanParts<TDay>(false, Error: TooOld);
            foreach (var d in body.Days)
            {
                if (!byDate.TryAdd(dateOf(d), d)) return broken;
            }
        }
        if (byDate.Count != dates.Count || dates.Any(d => !byDate.ContainsKey(d)))
            return new OpenedPlanParts<TDay>(false, Error: Missing);
        return new OpenedPlanParts<TDay>(true, dates.Select(d => byDate[d]).ToList());
    }

    /// <summary>The fortnight's days in steps of <see cref="DaysPerStep"/>, in order.</summary>
    public static List<IReadOnlyList<DateOnly>> PlanSteps(IReadOnlyList<DateOnly> dates, int size = DaysPerStep)
    {
        var out_ = new List<IReadOnlyList<DateOnly>>();
        for (var i = 0; i < dates.Count; i += size) out_.Add(dates.Skip(i).Take(size).ToList());
        return out_;
    }

    private static byte[] Gzip(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(raw);
        return ms.ToArray();
    }

    private static byte[] Gunzip(byte[] data)
    {
        using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }

    private sealed class PartBody<TDay>
    {
        public Guid StoreId { get; set; }
        public DateOnly PeriodStart { get; set; }
        public string RunId { get; set; } = "";
        public Guid UserId { get; set; }
        public long IssuedAt { get; set; }
        public List<TDay> Days { get; set; } = [];
    }
}

/// <summary>
/// The key plan parts are sealed with: derived from the configured <c>Plan:PartSecret</c>
/// (user-secrets or the <c>Plan__PartSecret</c> environment variable). Without one the
/// server makes a key of its own when it starts: the parts never leave the server process
/// in this edition (the steps run in the page's circuit), so a per-process key seals them
/// as well.
/// </summary>
public sealed class PlanPartKey
{
    public PlanPartKey(IConfiguration configuration)
    {
        var secret = configuration["Plan:PartSecret"];
        Key = string.IsNullOrEmpty(secret) ? RandomNumberGenerator.GetBytes(32) : GenerateParts.SigningKey(secret);
    }

    public PlanPartKey(byte[] key) => Key = key;

    public byte[] Key { get; }
}
