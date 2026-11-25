// New: the parity corpus's JSON form of TypeScript values. The C# side reads it
// with System.Text.Json and Roster.Engine.Core.RosterJson's conventions:
//
//   - objects keep their TypeScript field names (camelCase); an undefined field
//     is left out, as JSON.stringify leaves it out;
//   - a Map with string keys is a JSON object, its keys in insertion order; a
//     Map with any other keys is an array of [key, value] pairs in insertion
//     order (JSON.stringify would write {} for a Map); an EMPTY Map is [] (the
//     key type is not known at run time; the C# reader accepts either form);
//   - a Set is an array in insertion order;
//   - NaN, Infinity and -Infinity are the strings "NaN", "Infinity" and
//     "-Infinity" (System.Text.Json's named floating-point literals); -0 is 0,
//     as JSON.stringify writes it;
//   - a function has no JSON form: the caller tabulates it first (see
//     timeTable) or the encoder refuses;
//   - a Date is refused: the engine passes dates as ISO strings.
//
// Numbers are written by JSON.stringify, the shortest form that reads back
// exactly, so a double read on the C# side is bit-for-bit the TypeScript one.

export type Json = null | boolean | number | string | Json[] | { [k: string]: Json };

export class EncodeError extends Error {}

/** A value with no JSON form, met at `path`. */
function refuse(what: string, path: string): never {
  throw new EncodeError(`${what} at ${path} has no JSON form in the parity corpus`);
}

export function encodeNumber(n: number): Json {
  if (Number.isNaN(n)) return "NaN";
  if (n === Infinity) return "Infinity";
  if (n === -Infinity) return "-Infinity";
  return Object.is(n, -0) ? 0 : n;
}

/**
 * Encode `v`; undefined for a value JSON leaves out (an undefined field).
 * `fn` encodes a function met at `path` (a tabulated stand-in); without it a
 * function is refused.
 */
export function encode(
  v: unknown,
  fn?: (f: (...a: unknown[]) => unknown, path: string) => Json,
  path = "$",
  stack: object[] = [],
): Json | undefined {
  if (v === undefined) return undefined;
  if (v === null) return null;
  switch (typeof v) {
    case "number":
      return encodeNumber(v);
    case "string":
    case "boolean":
      return v;
    case "function":
      return fn ? fn(v as (...a: unknown[]) => unknown, path) : refuse("a function", path);
    case "bigint":
    case "symbol":
      return refuse(`a ${typeof v}`, path);
  }
  const o = v as object;
  if (stack.includes(o)) refuse("a cycle", path);
  stack.push(o);
  try {
    if (o instanceof Date) refuse("a Date", path);
    if (Array.isArray(o)) return o.map((x, i) => encode(x, fn, `${path}[${i}]`, stack) ?? null);
    if (o instanceof Set) return [...o].map((x, i) => encode(x, fn, `${path}[${i}]`, stack) ?? null);
    if (o instanceof Map) {
      const entries = [...o.entries()];
      if (entries.length > 0 && entries.every(([k]) => typeof k === "string")) {
        const out: Record<string, Json> = {};
        // A Map entry holding undefined still has its key: written as null.
        for (const [k, x] of entries) defineKey(out, k as string, encode(x, fn, `${path}.${k}`, stack) ?? null);
        return out;
      }
      return entries.map(([k, x]) => [
        encode(k, fn, `${path}.<key>`, stack) ?? null,
        encode(x, fn, `${path}[${String(k)}]`, stack) ?? null,
      ]);
    }
    const out: Record<string, Json> = {};
    for (const k of Object.keys(o)) {
      const x = encode((o as Record<string, unknown>)[k], fn, `${path}.${k}`, stack);
      if (x !== undefined) defineKey(out, k, x);
    }
    return out;
  } finally {
    stack.pop();
  }
}

// "__proto__" as a key must be an own property, not the prototype setter.
function defineKey(out: Record<string, Json>, k: string, x: Json) {
  if (k === "__proto__") Object.defineProperty(out, k, { value: x, enumerable: true, writable: true, configurable: true });
  else out[k] = x;
}

/** Only the named fields of an object (the ones an engine reads), undefined ones left out. */
export function pick<T extends object>(o: T, keys: readonly string[]): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const k of keys) if ((o as Record<string, unknown>)[k] !== undefined) out[k] = (o as Record<string, unknown>)[k];
  return out;
}

// ─── Functions as tables ────────────────────────────────────────────────────
// The painter and the shaper take "which groups may work at minute t" as a
// function. It is pure, so the corpus carries it as a table: its value at every
// quarter-hour of the day (and at any other minute the engine asked about),
// written only where it differs from its value outside the day.
//
//   { "default": <value at -15>, "at": [[t, value], ...] }   t ascending
//
// The C# side rebuilds it as t => at[t] if present, else default.

export const TABLE_DOMAIN: number[] = Array.from({ length: 24 * 4 + 1 }, (_, i) => i * 15);
const OUTSIDE = -15;

export interface TimeTable {
  /** The function, wrapped to note every minute the engine asks about. */
  traced: (t: number) => unknown;
  /** The table: call after the engine has run, so traced minutes are in it. */
  table: () => Json;
}

export function timeTable(f: (t: number) => unknown): TimeTable {
  const asked = new Set<number>();
  const traced = (t: number) => {
    asked.add(t);
    return f(t);
  };
  const table = (): Json => {
    const dflt = encode(f(OUTSIDE)) ?? null;
    const d = JSON.stringify(dflt);
    const times = [...new Set([...TABLE_DOMAIN, ...asked])].sort((a, b) => a - b);
    const at: Json[] = [];
    for (const t of times) {
      if (t === OUTSIDE) continue;
      const x = encode(f(t)) ?? null;
      if (JSON.stringify(x) !== d) at.push([encodeNumber(t), x]);
    }
    return { default: dflt, at };
  };
  return { traced, table };
}

/** A function of a role, as a table over the given roles: { role: value }. */
export function roleTable(f: (role: string) => unknown, roles: readonly string[]): Json {
  const out: Record<string, Json> = {};
  for (const r of roles) defineKey(out, r, encode(f(r)) ?? null);
  return out;
}
