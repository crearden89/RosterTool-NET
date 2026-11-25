// New: the engine entry points the parity corpus records, and how each call is
// written down. Every call made while the drivers run the app's compute code
// is recorded (once per distinct input) under its entry point:
//
//   stage1     runStage1                       lib/stage1/engine        (E1)
//   painter    paintTasks                      lib/stage2/engine        (E5)
//   shaper     shapePlan, labelJsaSlots        lib/stage2/shaper        (E6)
//   fortnight  runFortnightStage2              lib/stage2/fortnight     (E2)
//   pay        breakForShift ... breakTileError lib/payroll/breaks,
//              tierPayPeriod, makeUpHours       lib/payroll/payTiers,
//              computeShiftCosts                lib/payroll/shiftCost    (E1)
//   load       allocateLoad                    lib/load/allocate        (E3)
//   subtasks   labelShifts                     lib/subtasks/label       (E3)
//   till       computeBlock, liftToOneTill,    lib/till/engine,
//              dailyCheckoutHours, computeBorrow lib/till/borrow        (E3)
//
// "input" holds the arguments by their TypeScript parameter names. Two things
// are written in a form of their own (see encode.ts): functions of a minute
// (the painter's allowedAt, the shaper's allowedAt and placeAt) as tables, and
// the painter's shifts as the fields the painter reads (PaintableShift), with
// each blank in its result naming its shift by index.

import { performance } from "node:perf_hooks";
import { encode, pick, roleTable, timeTable, type Json, type TimeTable } from "./encode";
import { context, sink } from "./recorder";
import { overrideModule } from "./hooks";

type Fn = (...a: any[]) => any; // eslint-disable-line @typescript-eslint/no-explicit-any

/** PaintableShift's fields: all the painter reads or writes on a shift. */
export const PAINTABLE_FIELDS = ["role", "start", "end", "blocks", "restricted", "unfilled", "fixed"] as const;
export const ROLES = ["SM", "ASM", "DSM", "RSM", "SA", "JSA"] as const;

interface CallSpec {
  entry: string;
  fn: string;
  /** The arguments as recorded (functions already swapped for traced ones). */
  args: Record<string, unknown>;
  /** Tables to fill in once the call has run (keyed by the traced function). */
  tables?: Map<unknown, TimeTable>;
  /** What to compare after the call for in-place changes; default: args itself. Null: skip. */
  mutable?: (() => Record<string, unknown>) | null;
}

/** Encode args, writing each traced function as a placeholder to fill after the call. */
function encodeArgs(spec: CallSpec): { json: Json; fill: () => void } {
  const pending: [Record<string, Json>, TimeTable][] = [];
  const json = encode(spec.args, (f, path) => {
    const tt = spec.tables?.get(f);
    if (!tt) throw new Error(`${spec.entry}.${spec.fn}: untabulated function at ${path}`);
    const slot: Record<string, Json> = {};
    pending.push([slot, tt]);
    return slot;
  }) as Json;
  return { json, fill: () => pending.forEach(([slot, tt]) => Object.assign(slot, tt.table())) };
}

/** Run `call`, recording it. `output` encodes the result. */
function recordCall<R>(spec: CallSpec, call: () => R, output: (r: R) => unknown): R {
  if (!context.enabled) return call();
  const before = encodeArgs(spec);
  const mutable = spec.mutable === undefined ? () => spec.args : spec.mutable;
  // Each argument's JSON before the call, to see which ones the call changed.
  const snapshot = () =>
    Object.fromEntries(Object.entries(mutable!()).map(([k, v]) => [k, JSON.stringify(encode(v, () => null) ?? null)]));
  const beforeMut = mutable ? snapshot() : {};
  const t0 = performance.now();
  const result = call();
  const ms = performance.now() - t0;
  before.fill();
  const inputText = JSON.stringify(before.json);
  const s = sink(spec.entry);
  if (s.has(spec.fn, inputText)) return result;
  // Only the arguments the call changed in place go in "after".
  let after: Record<string, Json> | undefined;
  if (mutable) {
    const now = snapshot();
    const args = mutable();
    for (const k of Object.keys(now))
      if (now[k] !== beforeMut[k]) (after ??= {})[k] = encode(args[k], () => null) ?? null;
  }
  s.add(spec.fn, inputText, encode(output(result)) ?? null, after, ms);
  return result;
}

/** Record a function of numbers and strings only: deduped on a cheap key before any encoding. */
function recordPrimitive<R>(entry: string, fn: string, names: string[], args: unknown[], call: () => R): R {
  if (!context.enabled) return call();
  const key = `${fn}|${args.map((a) => (typeof a === "object" ? JSON.stringify(a) : String(a))).join("|")}`;
  const seen = primitiveSeen.get(entry) ?? new Set<string>();
  primitiveSeen.set(entry, seen);
  if (seen.has(key)) return call();
  seen.add(key);
  const input: Record<string, unknown> = {};
  names.forEach((n, i) => {
    if (args[i] !== undefined) input[n] = args[i];
  });
  return recordCall({ entry, fn, args: input, mutable: null }, call, (r) => r);
}
const primitiveSeen = new Map<string, Set<string>>();

const paintableView = (shifts: object[]) => shifts.map((s) => pick(s, PAINTABLE_FIELDS));

// ─── The overrides ──────────────────────────────────────────────────────────

export function installRecorders(root: string) {
  const lib = (p: string) => `${root}/src/lib/${p}`;

  overrideModule(lib("stage1/engine"), (real) => ({
    runStage1: (input: unknown) =>
      recordCall({ entry: "stage1", fn: "runStage1", args: { input } }, () => (real.runStage1 as Fn)(input), (r) => r),
  }));

  overrideModule(lib("stage2/engine"), (real) => ({
    paintTasks: (shifts: object[], demandByBlock: unknown, priority: unknown, allowedAt?: (t: number) => unknown, opts?: unknown) => {
      const tables = new Map<unknown, TimeTable>();
      let traced = allowedAt;
      if (allowedAt) {
        const tt = timeTable(allowedAt);
        traced = tt.traced;
        tables.set(traced, tt);
      }
      return recordCall(
        {
          entry: "painter",
          fn: "paintTasks",
          args: { shifts: paintableView(shifts), demandByBlock, priority, allowedAt: traced, opts },
          tables,
          mutable: () => ({ shifts: paintableView(shifts) }),
        },
        () => (real.paintTasks as Fn)(shifts, demandByBlock, priority, traced, opts),
        (r: { blanks: { shift: object; times: number[] }[]; lost: unknown; searchCapped?: boolean }) => ({
          blanks: r.blanks.map((b) => ({ shift: shifts.indexOf(b.shift), times: b.times })),
          lost: r.lost,
          searchCapped: r.searchCapped,
        }),
      );
    },
  }));

  overrideModule(lib("stage2/shaper"), (real) => ({
    shapePlan: (input: Record<string, unknown>) => {
      const tables = new Map<unknown, TimeTable>();
      const call = { ...input };
      for (const k of ["allowedAt", "placeAt"]) {
        const f = input[k] as ((t: number) => unknown) | undefined;
        if (!f) continue;
        const tt = timeTable(f);
        call[k] = tt.traced;
        tables.set(tt.traced, tt);
      }
      return recordCall(
        { entry: "shaper", fn: "shapePlan", args: { input: call }, tables },
        () => (real.shapePlan as Fn)(call),
        (r) => r,
      );
    },
    labelJsaSlots: (slots: unknown, isSchoolDay: boolean, jsaRule: unknown, rules?: unknown, keep?: () => boolean) =>
      keep
        ? (real.labelJsaSlots as Fn)(slots, isSchoolDay, jsaRule, rules, keep)
        : recordCall(
            { entry: "shaper", fn: "labelJsaSlots", args: { slots, isSchoolDay, jsaRule, rules } },
            () => (real.labelJsaSlots as Fn)(slots, isSchoolDay, jsaRule, rules),
            () => null,
          ),
  }));

  overrideModule(lib("stage2/fortnight"), (real) => ({
    runFortnightStage2: (input: unknown) =>
      recordCall(
        { entry: "fortnight", fn: "runFortnightStage2", args: { input } },
        () => (real.runFortnightStage2 as Fn)(input),
        (r) => r,
      ),
  }));

  overrideModule(lib("till/engine"), (real) => ({
    computeBlock: (segment: number, a: unknown) =>
      recordPrimitive("till", "computeBlock", ["segment", "a"], [segment, a], () => (real.computeBlock as Fn)(segment, a)),
    liftToOneTill: (r: unknown, a: unknown) =>
      recordPrimitive("till", "liftToOneTill", ["r", "a"], [r, a], () => (real.liftToOneTill as Fn)(r, a)),
    dailyCheckoutHours: (blocks: unknown) =>
      recordCall(
        { entry: "till", fn: "dailyCheckoutHours", args: { blocks }, mutable: null },
        () => (real.dailyCheckoutHours as Fn)(blocks),
        (r) => r,
      ),
  }));

  overrideModule(lib("till/borrow"), (real) => ({
    computeBorrow: (blocks: unknown, dowMon0: number, available: unknown, bands?: unknown, minRetained?: number, committed?: unknown) =>
      recordCall(
        { entry: "till", fn: "computeBorrow", args: { blocks, dowMon0, available, bands, minRetained, committed } },
        () => (real.computeBorrow as Fn)(blocks, dowMon0, available, bands, minRetained, committed),
        (r) => r,
      ),
  }));

  overrideModule(lib("subtasks/label"), (real) => ({
    labelShifts: (shifts: { blocks: unknown[] }[], ctx: Record<string, unknown>) => {
      // Only blocks are read or written; Stage 1 only as a ResetSource.
      const s1 = ctx.stage1 as Record<string, unknown> | null;
      const stage1 = s1 && {
        ...pick(s1, ["staffOpen", "staffClose"]),
        blocks: (s1.blocks as object[] | undefined)?.map((b) => pick(b, ["group", "time", "headcount"])),
        unitWindows: (s1.unitWindows as object[] | undefined)?.map((u) => pick(u, ["group", "time", "start", "end", "named", "names"])),
        resetTasks: (s1.resetTasks as object[] | undefined)?.map((t) => pick(t, ["name", "minutes", "windowStart", "windowEnd"])),
      };
      return recordCall(
        {
          entry: "subtasks",
          fn: "labelShifts",
          args: { shifts: shifts.map((s) => ({ blocks: s.blocks })), ctx: { ...ctx, stage1 } },
          mutable: null,
        },
        () => (real.labelShifts as Fn)(shifts, ctx),
        (r: { blocks: unknown[] }[]) => r.map((s) => ({ blocks: s.blocks })),
      );
    },
  }));

  const breaksFns: [string, string[]][] = [
    ["breakForShift", ["breakType", "lengthHours"]],
    ["breakForRole", ["role", "lengthHours"]],
    ["maxClockMinutes", ["breakType", "maxPaidHours"]],
    ["maxWorkMinutes", ["breakType", "maxPaidHours"]],
    ["breakSegmentsFor", ["totalMinutes"]],
    ["breakTileError", ["times", "entitledMinutes", "shiftStart", "shiftEnd"]],
  ];
  overrideModule(lib("payroll/breaks"), (real) =>
    Object.fromEntries(
      breaksFns.map(([name, params]) => [
        name,
        (...args: unknown[]) => recordPrimitive("pay", name, params, args, () => (real[name] as Fn)(...args)),
      ]),
    ),
  );

  overrideModule(lib("payroll/payTiers"), (real) => ({
    tierPayPeriod: (shifts: unknown, rulesFor: (role: string) => unknown) =>
      recordCall(
        { entry: "pay", fn: "tierPayPeriod", args: { shifts, rulesFor: roleTable(rulesFor, ROLES) }, mutable: () => ({ shifts }) },
        () => (real.tierPayPeriod as Fn)(shifts, rulesFor),
        (r) => r,
      ),
    makeUpHours: (role: string, contractPeriodHours: number, paidHours: number, creditedHours: number, exemptRoles?: string[]) =>
      recordPrimitive(
        "pay",
        "makeUpHours",
        ["role", "contractPeriodHours", "paidHours", "creditedHours", "exemptRoles"],
        [role, contractPeriodHours, paidHours, creditedHours, exemptRoles],
        () => (real.makeUpHours as Fn)(role, contractPeriodHours, paidHours, creditedHours, exemptRoles),
      ),
  }));

  overrideModule(lib("load/allocate"), (real) => ({
    allocateLoad: (crewIn: unknown, loads: unknown, opts?: unknown) =>
      recordCall(
        { entry: "load", fn: "allocateLoad", args: { crewIn, loads, opts } },
        () => (real.allocateLoad as Fn)(crewIn, loads, opts),
        (r) => r,
      ),
  }));
}

/**
 * computeShiftCosts reads its rates, the team, the settings and the day type
 * itself (lib/payroll/shiftCost.ts); the C# port takes them as arguments
 * (Pay.ShiftCost.ComputeShiftCosts). The recorder reads them through the same
 * request-cached functions the TypeScript uses, so the case's input is exactly
 * what the TypeScript computed with: { date, shifts, payRates, employees,
 * tiers, paySettings, dayType }.
 */
export function installShiftCostRecorder(root: string) {
  const lib = (p: string) => `${root}/src/lib/${p}`;
  overrideModule(lib("payroll/shiftCost"), (real) => ({
    computeShiftCosts: async (storeId: string, date: string, shifts: object[], periodTiers?: unknown) => {
      const result = await (real.computeShiftCosts as Fn)(storeId, date, shifts, periodTiers);
      if (!context.enabled || !periodTiers || shifts.length === 0) return result;
      const settings = require(lib("payroll/settings"));
      const [payRates, emps, paySettings, dayType] = await Promise.all([
        settings.getPayRates(),
        require(lib("refdata")).getEmployees(storeId),
        settings.getPaySettings(),
        require(lib("payroll/dayType")).dayType(storeId, date),
      ]);
      const employees = (emps as Record<string, unknown>[]).map((e) => ({ id: e.id, positionCode: e.position_code, smYearBand: e.sm_year_band }));
      const args = {
        date,
        shifts: shifts.map((x) => pick(x, ["employeeId", "role", "start", "end", "breakMinutes"])),
        payRates,
        employees,
        tiers: periodTiers,
        paySettings,
        dayType,
      };
      const inputText = JSON.stringify(encode(args));
      const s = sink("pay");
      if (!s.has("computeShiftCosts", inputText)) s.add("computeShiftCosts", inputText, encode(result) ?? null, undefined, 0);
      return result;
    },
  }));
}

/** Record a call the driver makes itself (no app code calls it offline). */
export function recordDirect<R>(entry: string, fn: string, args: Record<string, unknown>, call: () => R, output: (r: R) => unknown = (r) => r): R {
  return recordCall({ entry, fn, args }, call, output);
}
