// New: records engine calls as parity cases, one gzipped JSON-lines file per
// entry point, and picks the committed representative subset from them.
//
// A case is one line:
//   {"id": "<16 hex>", "fn": "<TypeScript function>", "source": "<where it came from>",
//    "input": {<parameter name>: <value>, ...}, "output": <return value>,
//    "after": {<parameter name>: <value after the call>, ...}}
// "after" is present only when the call changed an argument in place (the
// painter recolours the shifts it is given). The id is the start of the SHA-1
// of fn + input, so the same call always has the same id, and a call made
// twice is recorded once.

import { createHash } from "node:crypto";
import { createReadStream, createWriteStream, mkdirSync, readdirSync, statSync, type WriteStream } from "node:fs";
import { join } from "node:path";
import { createGunzip, createGzip, type Gzip } from "node:zlib";
import { createInterface } from "node:readline";
import type { Json } from "./encode";

/** Where the calls being made now come from (set by the drivers). */
export const context = { source: "", enabled: true };

export interface CaseMeta {
  id: string;
  fn: string;
  source: string;
  bytes: number;
  ms: number;
}

class Sink {
  readonly metas: CaseMeta[] = [];
  private readonly seen = new Set<string>();
  private readonly gz: Gzip;
  private readonly file: WriteStream;
  private done?: Promise<void>;

  constructor(readonly path: string) {
    this.gz = createGzip({ level: 9 });
    this.file = createWriteStream(path);
    this.gz.pipe(this.file);
    this.done = new Promise((resolve, reject) => {
      this.file.on("close", () => resolve());
      this.file.on("error", reject);
      this.gz.on("error", reject);
    });
  }

  /** Has this input been recorded already? (Cheap check before encoding the output.) */
  has(fn: string, inputText: string): boolean {
    return this.seen.has(hash(fn, inputText));
  }

  add(fn: string, inputText: string, output: Json, after: Json | undefined, ms: number): boolean {
    const h = hash(fn, inputText);
    if (this.seen.has(h)) return false;
    this.seen.add(h);
    const id = h.slice(0, 16);
    const line =
      `{"id":"${id}","fn":${JSON.stringify(fn)},"source":${JSON.stringify(context.source)},` +
      `"input":${inputText},"output":${JSON.stringify(output)}` +
      (after === undefined ? "" : `,"after":${JSON.stringify(after)}`) +
      "}\n";
    this.gz.write(line);
    this.metas.push({ id, fn, source: context.source, bytes: Buffer.byteLength(line), ms });
    return true;
  }

  async close() {
    this.gz.end();
    await this.done;
  }
}

function hash(fn: string, inputText: string): string {
  return createHash("sha1").update(fn).update("\n").update(inputText).digest("hex");
}

const sinks = new Map<string, Sink>();
let outDir = "";

export function openCorpus(dir: string) {
  outDir = dir;
  mkdirSync(dir, { recursive: true });
}

export function sink(entry: string): Sink {
  let s = sinks.get(entry);
  if (!s) {
    if (!outDir) throw new Error("openCorpus first");
    s = new Sink(join(outDir, `${entry}.jsonl.gz`));
    sinks.set(entry, s);
  }
  return s;
}

/** Let the gzip streams drain (between scenario days). */
export const breathe = () => new Promise<void>((r) => setImmediate(r));

export async function closeCorpus(): Promise<Map<string, CaseMeta[]>> {
  const out = new Map<string, CaseMeta[]>();
  for (const [entry, s] of sinks) {
    await s.close();
    out.set(entry, s.metas);
  }
  return out;
}

// ─── The committed subset ───────────────────────────────────────────────────
// Every function of the entry point keeps its share; inside each, cases are
// taken at an even stride through the run (so every source contributes), then
// the largest are dropped until the entry fits its byte budget.

export interface Budget {
  cases: number;
  bytes: number; // uncompressed
}

export function select(metas: CaseMeta[], budget: Budget): Set<string> {
  const byFn = new Map<string, CaseMeta[]>();
  for (const m of metas) {
    const arr = byFn.get(m.fn) ?? [];
    arr.push(m);
    byFn.set(m.fn, arr);
  }
  const chosen: CaseMeta[] = [];
  const perFn = Math.max(1, Math.floor(budget.cases / Math.max(1, byFn.size)));
  for (const list of byFn.values()) {
    if (list.length <= perFn) {
      chosen.push(...list);
      continue;
    }
    const stride = list.length / perFn;
    for (let i = 0; i < perFn; i++) chosen.push(list[Math.floor(i * stride)]);
  }
  let bytes = chosen.reduce((n, m) => n + m.bytes, 0);
  const bySize = [...chosen].sort((a, b) => b.bytes - a.bytes || (a.id < b.id ? -1 : 1));
  const dropped = new Set<string>();
  for (const m of bySize) {
    if (bytes <= budget.bytes) break;
    if (chosen.length - dropped.size <= 1) break;
    dropped.add(m.id);
    bytes -= m.bytes;
  }
  return new Set(chosen.filter((m) => !dropped.has(m.id)).map((m) => m.id));
}

/** Copy the chosen cases of `from` (a full corpus file) into `to`, in their original order. */
export async function copyCases(from: string, to: string, ids: Set<string>): Promise<number> {
  const gz = createGzip({ level: 9 });
  const file = createWriteStream(to);
  gz.pipe(file);
  const closed = new Promise<void>((resolve, reject) => {
    file.on("close", () => resolve());
    file.on("error", reject);
  });
  const rl = createInterface({ input: createReadStream(from).pipe(createGunzip()), crlfDelay: Infinity });
  let n = 0;
  for await (const line of rl) {
    const id = line.slice(7, 23); // {"id":"<16 hex>"
    if (!ids.has(id)) continue;
    if (!gz.write(`${line}\n`)) await new Promise((r) => gz.once("drain", r));
    n++;
  }
  gz.end();
  await closed;
  return n;
}

export const fileSize = (p: string) => statSync(p).size;

/** The cases of every entry point in a full corpus folder, as the dump left them. */
export async function readMetas(dir: string): Promise<Map<string, CaseMeta[]>> {
  const out = new Map<string, CaseMeta[]>();
  for (const f of readdirSync(dir).filter((x) => x.endsWith(".jsonl.gz")).sort()) {
    const metas: CaseMeta[] = [];
    const rl = createInterface({ input: createReadStream(join(dir, f)).pipe(createGunzip()), crlfDelay: Infinity });
    for await (const line of rl) {
      const head = JSON.parse(`${line.slice(0, line.indexOf(',"input":'))}}`) as { id: string; fn: string; source: string };
      metas.push({ ...head, bytes: Buffer.byteLength(line) + 1, ms: 0 });
    }
    out.set(f.replace(/\.jsonl\.gz$/, ""), metas);
  }
  return out;
}
