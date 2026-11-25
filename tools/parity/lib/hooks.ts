// New: module interception for the dumper. The TypeScript app's compute code
// reads Supabase and imports Next.js server-only markers; the dumper runs it
// offline on an in-memory database instead (src/test/fakeSupabase, the same
// stand-in the app's own production harness uses), and records every call to
// the engine's pure entry points.
//
// tsx runs the app's .ts files as CommonJS, and a compiled `import { f } from
// "./engine"` reads `engine.f` at each call - so handing the importer a proxy
// of the module's exports, with `f` replaced, routes every call through the
// recorder without touching the app's files. (vi.mock does the same in the
// app's own harness.)

import Module from "node:module";

type Exports = Record<string | symbol, unknown>;
type Loader = (request: string, parent: unknown, isMain: boolean) => unknown;
const M = Module as unknown as {
  _load: Loader;
  _resolveFilename: (request: string, parent: unknown, isMain: boolean) => string;
};

const stubs = new Map<string, unknown>();
const requestStubs = new Map<string, unknown>();
const overrides = new Map<string, (real: Exports) => Record<string, unknown>>();
const proxies = new Map<string, unknown>();
let installed = false;

/** Resolve a module the way the app's own imports resolve it. */
export function resolveModule(request: string, parent: NodeModule = module): string {
  return M._resolveFilename(request, parent, false);
}

/** Every importer of `request` gets `exports` instead of the real module. */
export function stubModule(request: string, exports: unknown) {
  stubs.set(resolveModule(request), exports);
}

/** Every `import "<name>"` gets `exports`: for a package that is not installed (Next's server-only marker). */
export function stubRequest(name: string, exports: unknown) {
  requestStubs.set(name, exports);
}

/**
 * Every importer of `request` gets the real module with the fields `make`
 * returns put in its place. `make` gets the real exports; read them lazily
 * (inside the replacement functions), as a module may still be loading.
 */
export function overrideModule(request: string, make: (real: Exports) => Record<string, unknown>) {
  const file = resolveModule(request);
  if (proxies.has(file)) throw new Error(`${request} was loaded before its override was set`);
  overrides.set(file, make);
}

export function installHooks() {
  if (installed) return;
  installed = true;
  const realLoad = M._load;
  M._load = function (this: unknown, request: string, parent: unknown, isMain: boolean) {
    if (requestStubs.has(request)) return requestStubs.get(request);
    let file: string | undefined;
    try {
      file = M._resolveFilename(request, parent, isMain);
    } catch {
      // Let the real loader report it.
    }
    if (file !== undefined) {
      if (stubs.has(file)) return stubs.get(file);
      const make = overrides.get(file);
      if (make) {
        let p = proxies.get(file);
        if (!p) {
          const real = realLoad.call(this, request, parent, isMain) as Exports;
          const repl = make(real);
          p = new Proxy(real, {
            get: (target, key, receiver) =>
              typeof key === "string" && Object.prototype.hasOwnProperty.call(repl, key)
                ? repl[key]
                : Reflect.get(target, key, receiver),
          });
          proxies.set(file, p);
        }
        return p;
      }
    }
    return realLoad.call(this, request, parent, isMain);
  } as Loader;
}
