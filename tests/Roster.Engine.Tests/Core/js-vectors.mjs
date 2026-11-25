// New: test vectors for the C# Js helpers (Roster.Engine.Core.Js), produced by V8 itself.
// Run from this folder: node js-vectors.mjs js-vectors.json
import { writeFileSync } from "node:fs";
let seed = 12345;
const rnd = () => { seed = (seed * 1103515245 + 12345) % 2147483648; return seed / 2147483648; };
const nums = [0, -0, 0.5, 1.5, 2.5, -0.5, -1.5, -2.5, 1.005, 1.045, 2.675, 0.1 + 0.2, 1e21, 1e-7, 123.456, 0.000001234,
  -0.001, 14.28 / 0.9, 11.8 / 0.9, 4.53 / 0.9, 0.49999999999999994, 1e20, 123456789012345680000, 5e-324, 1.7976931348623157e308,
  45.77, 47.35, 0.25, 0.75, 17.5, 9.5, 1 / 3, 2 / 3, 100 / 3, 1234.5678, 0.05, 0.15, 0.35, 8.345, 8.355, 1.255, 0.0001, 3.14159e-10, 6.02e23];
for (let i = 0; i < 200; i++) nums.push((rnd() - 0.5) * 10 ** Math.floor(rnd() * 8 - 3));
for (let i = 0; i < 100; i++) nums.push(Math.round(rnd() * 100000) / 100 + 0.005);
const toFixed = [];
for (const x of nums) for (const d of [0, 1, 2, 3, 6]) toFixed.push([x, d, x.toFixed(d)]);
const round = nums.map((x) => [x, Math.round(x)]);
const str = nums.map((x) => [x, String(x)]);
// Sorting: stability and inconsistent comparators, recorded as permutations.
const sorts = [];
for (let t = 0; t < 120; t++) {
  const n = 1 + Math.floor(rnd() * (t < 60 ? 70 : 400));
  const keys = Array.from({ length: n }, () => Math.floor(rnd() * (t % 3 === 0 ? 5 : 1000)));
  const mode = t % 4; // 0: by key (stable ties), 1: inconsistent hash, 2: desc by key, 3: key with NaN sometimes
  const idx = keys.map((_, i) => i);
  const cmp = (a, b) => {
    if (mode === 0) return keys[a] - keys[b];
    if (mode === 1) return ((a * 31 + b * 17 + keys[a]) % 7) - 3;
    if (mode === 2) return keys[b] - keys[a];
    return (keys[a] % 4 === 0 ? NaN : keys[a] - keys[b]);
  };
  idx.sort(cmp);
  sorts.push({ mode, keys, order: idx });
}
const words = ["CGOS Ordering", "Cash-Up", "End of Day", "UKG End of Day", "Fresh Ordering", "Longlife Date Checks", "Register Cash-Up",
  "Safe Handover", "apple", "Apple", "APPLE", "a-b", "ab", "a b", "Zed", "zed", "Émile", "emile", "no_manager", "lone_worker",
  "junior_supervision", "P10", "P2", "P1", "Ds", "D's", "a", "A", "b", "B", "", "10:00:00", "09:45:00", "Till Calibration", "till calibration"];
const lc = [];
for (const a of words) for (const b of words) lc.push([a, b, Math.sign(a.localeCompare(b))]);
writeFileSync(process.argv[2], JSON.stringify({ toFixed, round, str, sorts, localeCompare: lc }));
console.log(toFixed.length, round.length, sorts.length, lc.length);
