// New: test vectors for the Planning port, produced by the TypeScript app itself:
// random Stage 1 days through runStage1, the day checker over random shifts on
// them, and the benchmark split's hundredths. PlanningVectorTests replays each
// case through the C# port and compares the answers field by field.
// Run from this folder (Node 22, which strips the TypeScript types itself):
//   node planning-vectors.mjs planning-vectors.json [path to the TS app]
import { register } from "node:module";
import { writeFileSync } from "node:fs";

const APP = process.argv[3] ?? "/home/user/rostertool";
const ROOT = new URL("src/", "file://" + APP.replace(/\/?$/, "/")).href;
// Resolve the app's "@/..." imports and its extensionless specifiers to .ts files.
const hooks = `const ROOT = ${JSON.stringify(ROOT)};
export async function resolve(s, c, next) {
  if (s.startsWith("@/")) s = ROOT + s.slice(2);
  if ((s.startsWith("file://") || s.startsWith("./") || s.startsWith("../")) && !/\\.[mc]?[jt]sx?$/.test(s))
    for (const ext of [".ts", ".tsx", "/index.ts"]) { try { return await next(s + ext, c); } catch {} }
  return next(s, c);
}`;
register("data:text/javascript," + encodeURIComponent(hooks));
const { runStage1 } = await import(ROOT + "lib/stage1/engine.ts");
const { checkPlanDay } = await import(ROOT + "lib/plan/dayCheck.ts");
const { toHundredths } = await import(ROOT + "lib/plan/benchmarkSplit.ts");

let seed = 20261125;
const rnd = () => {
  seed = (seed * 1103515245 + 12345) % 2147483648;
  return seed / 2147483648;
};
const pick = (xs) => xs[Math.floor(rnd() * xs.length)];
const q = (m) => Math.round(m / 15) * 15;
const GROUPS = ["load_running", "cleaning", "specials", "reset", "admin"];
const hours = (max) => {
  const k = rnd();
  return k < 0.15 ? 0 : k < 0.55 ? Math.round(rnd() * max * 4) / 4 : Math.round(rnd() * max * 1e6) / 1e6;
};
const shuffle = (xs) => {
  const a = [...xs];
  for (let i = a.length - 1; i > 0; i--) {
    const j = Math.floor(rnd() * (i + 1));
    [a[i], a[j]] = [a[j], a[i]];
  }
  return a;
};
const ADMIN = ["Longlife Date Checks", "Register Assignment", "UKG End of Day", "Fresh Ordering", "Safe Handover", "Cash-Up", "End of Day", "CGOS Ordering"];
const RESET = ["Re-run 1", "Re-run 2", "First declare", "Full decard"];

function stage1Input() {
  const tradingOpen = pick([420, 450, 480, 510, 540]);
  const tradingClose = pick([1020, 1080, 1140, 1200, 1260, 1320]);
  const staffed = rnd() < 0.85;
  const staffOpen = staffed ? Math.max(300, tradingOpen - pick([60, 90, 120, 150])) : undefined;
  const staffClose = staffed ? tradingClose + pick([0, 15, 30]) : undefined;
  const so = staffOpen ?? tradingOpen;
  const sc = staffClose ?? tradingClose;
  const input = {
    tradingOpen,
    tradingClose,
    ...(staffed ? { staffOpen, staffClose } : {}),
    benchmark90: { load_running: hours(16), cleaning: hours(4), specials: hours(6), reset: hours(10), admin: hours(10) },
    windows: [],
    checkout: [],
  };
  for (const g of GROUPS) {
    const n = rnd() < 0.08 ? 0 : rnd() < 0.7 ? 1 : 2;
    for (let i = 0; i < n; i++) {
      const start = q(so - (rnd() < 0.05 ? 120 : 0) + rnd() * (sc - so));
      const w = { group: g, start, end: Math.min(sc + (rnd() < 0.1 ? 60 : 0), start + q(30 + rnd() * 600)) };
      if (g === "cleaning" && rnd() < 0.3) w.hours = pick([0.5, 1, 1.5]);
      if (rnd() < 0.05) w.makeUpOnly = true;
      input.windows.push(w);
    }
  }
  const peakAt = q(tradingOpen + rnd() * (tradingClose - tradingOpen));
  for (let t = tradingOpen; t < tradingClose; t += 15) {
    const hump = Math.max(0, 4 - Math.abs(t - peakAt) / 90);
    input.checkout.push({ time: t, headcount: rnd() < 0.05 ? 0 : Math.round(1 + hump + rnd() * 1.5) });
  }
  if (rnd() < 0.2) input.nonOpHours = hours(6);
  if (rnd() < 0.1) input.eveningLoadRunningHours = hours(4);
  if (rnd() < 0.3) input.adminDepth = { oneDeep: pick([1, 2]), handover: pick([1, 2, 3]) };
  if (rnd() < 0.6) {
    input.adminTasks = [];
    for (let i = 0, n = Math.floor(rnd() * 7); i < n; i++) {
      const start = q(so - 60 + rnd() * (sc - so + 30));
      input.adminTasks.push({ name: pick(ADMIN), minutes: pick([5, 6, 8, 15, 17, 20, 30, 45, 58.5, 60, 90, 120]), windowStart: start, windowEnd: start + q(15 + rnd() * 480) });
    }
  }
  if (rnd() < 0.5) {
    input.resetTasks = [];
    for (let i = 0, n = Math.floor(rnd() * 5); i < n; i++) {
      const start = q(so + rnd() * (sc - so));
      input.resetTasks.push({
        name: pick(RESET),
        minutes: pick([5, 8.25, 11.5, 15, 30, 60, 90, 180]),
        windowStart: start,
        windowEnd: Math.min(sc, start + q(15 + rnd() * 400)),
        placement: pick(["spread", "avoid_peak", "follow_demand", "late", "early"]),
      });
    }
  }
  let curve = null;
  if (rnd() < 0.4) {
    const rules = {};
    for (const g of shuffle(GROUPS).slice(0, Math.floor(rnd() * 4))) {
      rules[g] = {
        strategy: pick(["spread", "follow_demand", "backfill", "forward_fill", "peak_split", "avoid_peak"]),
        anchor: q(so + rnd() * (sc - so)),
        prePeakPct: pick([null, 10, 25, 50]),
        postPeakPct: pick([null, 10, 25]),
        avoidStrength: pick([null, 0, 50, 100]),
      };
    }
    curve = new Map();
    if (rnd() < 0.85) for (let t = so; t < sc; t += 15) curve.set(t, Math.round((0.002 + Math.max(0, 0.03 - Math.abs(t - peakAt) / 9000) + rnd() * 0.004) * 1e6) / 1e6);
    const peak = rnd() < 0.7 ? [peakAt - 60, peakAt + 60] : [null, null];
    input.placement = { rules, curve, peakStart: peak[0], peakEnd: peak[1] };
  }
  if (rnd() < 0.25) input.stepOrder = shuffle(GROUPS).slice(0, 3 + Math.floor(rnd() * 3));
  return input;
}

// The input as JSON: the curve Map as [time, share] pairs (RosterJson's JsMap form).
const inputJson = (input) =>
  input.placement ? { ...input, placement: { ...input.placement, curve: [...input.placement.curve] } } : input;

const TILE_GROUPS = ["flex", "break", "checkout", "registers", "scos", "load_running", "reset", "admin", "specials", "cleaning", "non_op"];

function dayCase(input, result) {
  const so = input.staffOpen ?? input.tradingOpen;
  const sc = input.staffClose ?? input.tradingClose;
  const s1 = {
    ...result,
    hasData: true,
    closed: false,
    tradingOpen: input.tradingOpen,
    tradingClose: input.tradingClose,
    staffOpen: so,
    staffClose: sc,
    windows: input.windows,
    adminTasks: (input.adminTasks ?? []).map((t) => ({ name: t.name, minutes: t.minutes, windowStart: t.windowStart, windowEnd: t.windowEnd, windowLabel: "" })),
  };
  if (rnd() < 0.6) {
    s1.benchmarkFile = { checkout: hours(14), load_running: hours(16), cleaning: hours(4), specials: hours(6), reset: hours(10), admin: hours(10) };
    if (rnd() < 0.3) s1.phMovedHours = pick([-0.75, 0.5, 1.25]);
  }
  if (rnd() < 0.2) s1.tillCapped = { hours: hours(6), registers: pick([3, 4, 5]), start: q(input.tradingOpen + 120), end: q(input.tradingOpen + 300) };
  const shifts = [];
  for (let i = 0, n = 1 + Math.floor(rnd() * 7); i < n; i++) {
    const planRole = i < 2 && rnd() < 0.8 ? "MGR" : pick(["SA", "SA", "JSA", "MGR"]);
    const start = q(so - (rnd() < 0.1 ? 120 : 0) + rnd() * (sc - so - 120));
    const end = Math.min(sc + (rnd() < 0.1 ? 60 : 0), start + q(120 + rnd() * 540));
    const blocks = [];
    let g = pick(TILE_GROUPS);
    for (let t = start - (rnd() < 0.05 ? 15 : 0); t < end; t += 15) {
      if (rnd() < 0.25) g = pick(TILE_GROUPS);
      if (rnd() < 0.04) continue; // a quarter-hour with no tile
      blocks.push({ time: t, group: g });
    }
    shifts.push({ planRole, slotId: `s${i}`, start, end, blocks });
  }
  const opts = {};
  if (rnd() < 0.5) opts.cover = { minPeopleOnFloor: pick([1, 2, 3]), minAdultsWithJsa: pick([2, 3, 4]) };
  if (rnd() < 0.5) opts.maxClock = { SA: pick([570, 600]), MGR: 720, JSA: pick([480, 540]) };
  if (rnd() < 0.4) opts.minShiftMin = pick([180, 240]);
  if (rnd() < 0.4) opts.managerMinShiftMin = pick([300, 360]);
  if (rnd() < 0.5) opts.isSchoolDay = rnd() < 0.5;
  if (rnd() < 0.3) opts.breakTimeAdded = pick([0.25, 0.5, 1]);
  // Recorded before the run, in case the run touches what it was given.
  const given = JSON.parse(JSON.stringify({ s1, shifts, opts }));
  return { ...given, out: checkPlanDay(s1, shifts, opts) };
}

const CASES = Number(process.env.CASES ?? 300);
const stage1 = [];
const dayCheck = [];
for (let i = 0; i < CASES; i++) {
  const input = stage1Input();
  const given = JSON.parse(JSON.stringify(inputJson(input)));
  const out = runStage1(input);
  stage1.push({ input: given, out: JSON.parse(JSON.stringify(out)) });
  if (i % 2 === 0) dayCheck.push(dayCase(input, JSON.parse(JSON.stringify(out))));
}

const hundredths = [];
for (let i = 0; i < 300; i++) {
  const keys = shuffle(["checkout", "load_running", "reset", "admin", "specials", "cleaning", "non_op"]).slice(0, 2 + Math.floor(rnd() * 6));
  const parts = {};
  for (const k of keys) if (rnd() < 0.9) parts[k] = rnd() < 0.3 ? Math.round(rnd() * 2000) / 100 : Math.round(rnd() * 20 * 1e7) / 1e7;
  const sum = Object.values(parts).reduce((a, b) => a + b, 0);
  const total = rnd() < 0.7 ? sum : sum + (rnd() - 0.5) * 0.05;
  const withKeys = rnd() < 0.5 ? keys : undefined;
  const given = JSON.parse(JSON.stringify({ parts, total, keys: withKeys ?? null }));
  hundredths.push({ ...given, out: toHundredths(parts, total, withKeys) });
}

writeFileSync(process.argv[2], JSON.stringify({ stage1, dayCheck, hundredths }));
console.log(stage1.length, dayCheck.length, hundredths.length);
