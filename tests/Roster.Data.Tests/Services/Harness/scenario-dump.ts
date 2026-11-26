// New: regenerates harness-scenarios.json.gz from the TypeScript app (not built by .NET). From the
// TypeScript checkout: npx tsx@4.20.3 <this file> <out.json>, then gzip -9 it here.
// Dumps the TypeScript harness's store-fortnights (src/test/rosterScenario.ts) as the
// database rows they are, for the .NET data tests to seed SQL Server with.
import { writeFileSync } from "node:fs";
import { buildScenario, genParams, realG999Params } from "@/test/rosterScenario";

const out = process.argv[2];
// g999 and generated seeds 1-20: the corpus of production.invariants.test.ts, which covers
// every scenario the other whole-pipeline tests name (held plan 1-5, contract flags 1, 4, 10).
const cases: Record<string, ReturnType<typeof realG999Params>> = { g999: realG999Params() };
for (let seed = 1; seed <= 20; seed++) cases[`gen${seed}`] = genParams(seed);
const dump: Record<string, unknown> = {};
for (const [k, p] of Object.entries(cases)) {
  const s = buildScenario(p);
  dump[k] = { params: s.params, tables: s.db.tables, curveWeek: [...s.curveWeek.entries()], posCustomers: s.posCustomers };
}
writeFileSync(out, JSON.stringify(dump));
console.log("wrote", out, Object.keys(dump).join(","));
