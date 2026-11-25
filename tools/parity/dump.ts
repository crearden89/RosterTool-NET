// New: the parity dumper. Runs the TypeScript app's engine on the shared inputs
// and records, for every engine entry point, the exact input and output of each
// distinct call (lib/entries.ts lists them). The C# parity tests
// (tests/Roster.Engine.Tests/Parity) run the same inputs through the C# engine
// and require identical outputs.
//
// The inputs:
//   - the rosterScenario generator (src/test/rosterScenario.ts): seed 0 is the
//     real G999 fixture fortnight (31/08-13/09), seeds 1-39 generated stores;
//   - the replay kit (docs/plan-review-kit/replay-kit-p5.tar.gz): data-p5's 19
//     stores planned for 9-22/11/2026, and data-roster2's and data-roster's six
//     stores planned, held and rostered for both fortnights from 9/11/2026;
//   - the G999 fixtures (src/lib/stage2/g999.fixture.ts) through the audit
//     harness (g999.audit.ts): the fortnight engine on the frozen plan.
// The app's compute code runs as its own production harness runs it: on the
// in-memory database (src/test/fakeSupabase), with the trade curve and POS
// morning customers handed over at the refdata boundary. No Supabase, no
// network.
//
// Run it from the TypeScript checkout, so its "@/..." imports resolve:
//
//   cd /home/user/rostertool
//   npx tsx@4.20.3 <this repo>/tools/parity/dump.ts [--full DIR] [--out DIR] [--kit DIR] [--seeds 0-39] [--sources seeds,kit,g999]
//
//   --full   where the full corpus goes (every distinct call; large; default $TMPDIR/roster-parity-full)
//   --out    where the committed representative subset goes (default tests/parity-corpus of this repo)
//   --kit    an unpacked replay kit (default: unpacked from the checkout into a temp folder)
//   --seeds  rosterScenario seeds, e.g. 0-39 or 0,3,7 (default 0-39)
//   --sources which inputs to run (default seeds,kit,g999)
//   --no-select  write the full corpus only
//   --select-only  pick the committed subset again from the full corpus already in --full

import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, copyFileSync, symlinkSync, writeFileSync, readdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { installHooks, overrideModule, stubModule, stubRequest } from "./lib/hooks";
import { installRecorders } from "./lib/entries";
import { breathe, closeCorpus, context, copyCases, fileSize, openCorpus, readMetas, select, type Budget, type CaseMeta } from "./lib/recorder";

// eslint-disable-next-line @typescript-eslint/no-explicit-any
type Any = any;

const argv = process.argv.slice(2);
const opt = (name: string, dflt?: string) => {
  const i = argv.indexOf(`--${name}`);
  return i >= 0 ? argv[i + 1] : dflt;
};
const TS = resolve(opt("ts", process.cwd())!);
const HERE = __dirname;
const REPO = resolve(HERE, "../..");
const FULL = resolve(opt("full", join(tmpdir(), "roster-parity-full"))!);
const OUT = resolve(opt("out", join(REPO, "tests/parity-corpus"))!);
const SOURCES = new Set(opt("sources", "seeds,kit,g999")!.split(","));
const SEEDS = parseSeeds(opt("seeds", "0-39")!);

function parseSeeds(s: string): number[] {
  return s.split(",").flatMap((part) => {
    const [a, b] = part.split("-").map(Number);
    return b === undefined ? [a] : Array.from({ length: b - a + 1 }, (_, i) => a + i);
  });
}

if (!existsSync(join(TS, "src/lib/stage2/fortnight.ts"))) {
  console.error(`Not the TypeScript checkout: ${TS} (run from it, or pass --ts)`);
  process.exit(2);
}

// What the database stand-in and the two derived reads answer right now.
interface Current {
  db: { client: unknown; tables: Record<string, unknown[]> };
  curveWeek: unknown;
  posCustomers: unknown;
}
let current: Current | null = null;

installHooks();
stubRequest("server-only", {});
stubModule(`${TS}/src/lib/supabase/server`, { createClient: async () => current!.db.client });
overrideModule(`${TS}/src/lib/refdata`, () => ({
  getTradeCurveWeek: async () => current!.curveWeek,
  getPosCustomerRows: async () => current!.posCustomers,
}));
installRecorders(TS);

const req = (p: string): Any => require(`${TS}/src/${p}`);
const { withRequestMemo } = req("lib/requestCache");
const { computeStage1 } = req("lib/stage1/compute");
const { computePlanShifts } = req("lib/stage1/planShifts");
const { computeRosterFortnight } = req("lib/stage2/compute");
const { tierRosterPeriod } = req("lib/payroll/periodTiers");
const { periodDatesFrom } = req("lib/period");

/** Plan every day of a store-fortnight (Plan page), then roster it. */
async function runFortnight(label: string, sc: Current, storeId: string, periodStart: string, roster: "fresh" | "held" | "none") {
  current = sc;
  const pay = (sc.db.tables as Record<string, unknown[]>).pay_settings;
  if (!pay) (sc.db.tables as Record<string, unknown[]>).pay_settings = [];
  await withRequestMemo(async () => {
    const held: unknown[] = [];
    for (const date of periodDatesFrom(periodStart) as string[]) {
      context.source = `${label} ${date}`;
      const result = await computeStage1(storeId, date);
      const plan = result.hasData && !result.closed ? await computePlanShifts(storeId, date) : null;
      held.push({ date, result, plan });
      await breathe();
    }
    if (roster === "none") return;
    context.source = `${label} fortnight ${periodStart}`;
    const f = await computeRosterFortnight(storeId, periodStart, roster === "held" ? held : undefined);
    context.source = `${label} pay ${periodStart}`;
    await tierRosterPeriod(f.byDate);
  });
  await breathe();
}

async function seeds() {
  const S = req("test/rosterScenario");
  for (const seed of SEEDS) {
    const p = seed === 0 ? S.realG999Params() : S.genParams(seed);
    const t0 = Date.now();
    await runFortnight(seed === 0 ? "g999" : `seed ${seed}`, S.buildScenario(p), S.STORE, p.periodStart, "fresh");
    log(`seed ${seed}: ${((Date.now() - t0) / 1000).toFixed(1)}s`);
  }
}

/** The replay kit, unpacked (or the folder given). */
function kitDir(): string {
  const given = opt("kit");
  if (given) return resolve(given);
  const dir = mkdtempSync(join(tmpdir(), "roster-kit-"));
  execFileSync("tar", ["xzf", join(TS, "docs/plan-review-kit/replay-kit-p5.tar.gz"), "-C", dir]);
  return join(dir, "plan-review-kit");
}

// The kit's own loader (live/loader.ts) reads ./data next to it and the period
// from LIVE_PERIOD when it loads, so each (export, period) gets its own copy.
function kitLoader(kit: string, data: string, period: string): Any {
  const lane = mkdtempSync(join(tmpdir(), `roster-lane-${data}-`));
  copyFileSync(join(kit, "live/loader.ts"), join(lane, "loader.ts"));
  symlinkSync(join(kit, data), join(lane, "data"));
  process.env.LIVE_PERIOD = period;
  return require(join(lane, "loader.ts"));
}

async function kit() {
  const k = kitDir();
  const runs: [string, string, "held" | "none"][] = [
    ["data-p5", "2026-11-09", "none"],
    ["data-roster2", "2026-11-09", "held"],
    ["data-roster2", "2026-11-23", "held"],
    ["data-roster", "2026-11-09", "held"],
    ["data-roster", "2026-11-23", "held"],
  ];
  for (const [data, period, roster] of runs) {
    const L = kitLoader(k, data, period);
    for (const code of L.listStoreCodes() as string[]) {
      const t0 = Date.now();
      await runFortnight(`${data} ${code}`, L.buildLive(code), L.STORE, period, roster);
      log(`${data} ${code} ${period}: ${((Date.now() - t0) / 1000).toFixed(1)}s`);
    }
  }
}

async function g999() {
  const { runAudit } = req("lib/stage2/g999.audit");
  context.source = "g999 audit";
  runAudit();
  context.source = "g999 audit no weekend markers";
  runAudit({ markers: [] });
  context.source = "g999 audit no leave";
  runAudit({ leave: [] });
  log("g999 audit: done");
}

// What the committed corpus keeps of each entry point (uncompressed budget).
const BUDGETS: Record<string, Budget> = {
  stage1: { cases: 240, bytes: 8e6 },
  painter: { cases: 150, bytes: 16e6 },
  shaper: { cases: 160, bytes: 10e6 },
  fortnight: { cases: 4, bytes: 6e6 },
  pay: { cases: 1200, bytes: 2e6 },
  till: { cases: 1200, bytes: 3e6 },
  subtasks: { cases: 160, bytes: 3e6 },
  load: { cases: 200, bytes: 2e6 },
};

const log = (s: string) => console.log(`[parity] ${s}`);

async function main() {
  let metas: Map<string, CaseMeta[]>;
  if (argv.includes("--select-only")) metas = await readMetas(FULL);
  else {
    openCorpus(FULL);
    if (SOURCES.has("g999")) await g999();
    if (SOURCES.has("seeds")) await seeds();
    if (SOURCES.has("kit")) await kit();
    metas = await closeCorpus();
  }
  const tsCommit = execFileSync("git", ["-C", TS, "rev-parse", "--short", "HEAD"]).toString().trim();
  const manifest: Record<string, unknown> = { tsCommit, sources: [...SOURCES], seeds: opt("seeds", "0-39"), entries: {} };
  for (const [entry, m] of metas) log(`${entry}: ${m.length} distinct calls (${(fileSize(join(FULL, `${entry}.jsonl.gz`)) / 1e6).toFixed(1)} MB gz)`);
  if (argv.includes("--no-select")) return;
  mkdirSync(OUT, { recursive: true });
  for (const [entry, m] of metas) {
    const ids = select(m, BUDGETS[entry] ?? { cases: 200, bytes: 2e6 });
    const n = await copyCases(join(FULL, `${entry}.jsonl.gz`), join(OUT, `${entry}.jsonl.gz`), ids);
    const size = fileSize(join(OUT, `${entry}.jsonl.gz`));
    (manifest.entries as Record<string, unknown>)[entry] = { cases: n, full: m.length };
    log(`${entry}: committed ${n} of ${m.length} (${(size / 1e6).toFixed(2)} MB gz)`);
  }
  for (const f of readdirSync(OUT)) if (f.endsWith(".jsonl.gz") && !metas.has(f.replace(/\.jsonl\.gz$/, ""))) log(`note: ${f} not refreshed this run`);
  writeFileSync(join(OUT, "manifest.json"), `${JSON.stringify(manifest, null, 2)}\n`);
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
