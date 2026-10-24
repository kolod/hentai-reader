// QuickJS engine wrapper: one isolated WebAssembly module per engine, contract prelude + runner
// glue + source module, JSON-only calls, promise pumping and per-call limits (research R1, R7).
import { readFileSync } from "node:fs";
import {
  newQuickJSWASMModule,
  shouldInterruptAfterDeadline,
  type QuickJSContext,
  type QuickJSHandle,
  type QuickJSRuntime,
} from "quickjs-emscripten";
import { paths } from "./paths.ts";

const PRELUDE = readFileSync(paths.prelude, "utf8");
const GLUE = readFileSync(paths.hostGlue, "utf8");

export const DEFAULT_LIMITS = { callTimeoutMs: 60_000, memoryBytes: 128 * 1024 * 1024 };

/**
 * Host side of the private bridge consumed by contract/host-glue.js (protocol:
 * specs/003-windows-host/contracts/host-bridge.md). Every method returns a JSON envelope.
 */
export interface HostBridge {
  http(meta: string, body: Uint8Array | undefined): Promise<{ env: string; bytes?: Uint8Array }>;
  htmlSelect(markup: string, elementId: number, selector: string): string;
  htmlCall(op: string, elementId: number, arg: string): string;
  storageGet(key: string): Promise<string>;
  storageSet(key: string, valueJson: string): Promise<string>;
  sleep(ms: number): Promise<string>;
  log(level: string, text: string): void;
  /** Called when no source call is in flight, so per-call handles can be dropped. */
  idle(): void;
  /** A failure that must abort the run regardless of what the script does (e.g. unmatched request). */
  fatal(): Error | null;
}

export type CallError =
  | { type: "SourceError"; kind: string; message: string; retryAfterSeconds?: number; details?: Record<string, unknown> }
  | { type: "ScriptError"; name: string; message: string; stack?: string; details?: Record<string, unknown> };

export type CallResult = { ok: true; value: unknown } | { ok: false; error: CallError };

export interface SourceDescription {
  manifest: unknown;
  functions: string[];
}

export class LoadError extends Error {}

export interface EngineOptions {
  source: string;
  sourceName: string;
  bridge: HostBridge;
  frozenNow?: number;
  limits?: Partial<typeof DEFAULT_LIMITS>;
}

export class Engine {
  private inFlight = 0;
  private broken: string | null = null;
  private readonly runtime: QuickJSRuntime;
  private readonly ctx: QuickJSContext;
  private readonly invoker: QuickJSHandle;
  private readonly bridge: HostBridge;
  private readonly limits: typeof DEFAULT_LIMITS;

  private constructor(
    runtime: QuickJSRuntime,
    ctx: QuickJSContext,
    invoker: QuickJSHandle,
    bridge: HostBridge,
    limits: typeof DEFAULT_LIMITS,
  ) {
    this.runtime = runtime;
    this.ctx = ctx;
    this.invoker = invoker;
    this.bridge = bridge;
    this.limits = limits;
  }

  static async create(opts: EngineOptions): Promise<Engine> {
    const limits = { ...DEFAULT_LIMITS, ...opts.limits };
    const QuickJS = await newQuickJSWASMModule();
    const runtime = QuickJS.newRuntime();
    runtime.setMemoryLimit(limits.memoryBytes);
    const ctx = runtime.newContext();

    installBridge(ctx, runtime, opts.bridge, opts.frozenNow);
    evalGlobal(ctx, PRELUDE, "prelude.js").dispose();
    const bind = evalGlobal(ctx, GLUE, "host-glue.js");

    runtime.setInterruptHandler(shouldInterruptAfterDeadline(Date.now() + limits.callTimeoutMs));
    const loaded = ctx.evalCode(opts.source, opts.sourceName, { type: "module" });
    if (loaded.error) {
      const err = describeVmError(ctx.dump(loaded.error));
      loaded.error.dispose();
      throw new LoadError(`${opts.sourceName}: ${err.name}: ${err.message}`);
    }
    const exportsHandle = loaded.value;
    const bound = ctx.callFunction(bind, ctx.undefined, exportsHandle);
    exportsHandle.dispose();
    bind.dispose();
    const invoker = ctx.unwrapResult(bound);
    return new Engine(runtime, ctx, invoker, opts.bridge, limits);
  }

  async describe(): Promise<SourceDescription> {
    const res = await this.call("__describe", []);
    if (!res.ok) throw new LoadError(`cannot read source exports: ${res.error.message}`);
    return res.value as SourceDescription;
  }

  /** Calls an exported source function with JSON arguments. Never throws for script errors. */
  async call(fn: string, args: unknown[]): Promise<CallResult> {
    if (this.broken) return scriptError("EngineBroken", this.broken, { limit: "time" });
    const { ctx, runtime } = this;
    this.inFlight++;
    try {
      runtime.setInterruptHandler(shouldInterruptAfterDeadline(Date.now() + this.limits.callTimeoutMs));
      const fnHandle = ctx.newString(fn);
      const argsHandle = ctx.newString(JSON.stringify(args));
      const started = ctx.callFunction(this.invoker, ctx.undefined, fnHandle, argsHandle);
      fnHandle.dispose();
      argsHandle.dispose();
      if (started.error) {
        const err = describeVmError(ctx.dump(started.error));
        started.error.dispose();
        return classify(err);
      }
      const settled = ctx.resolvePromise(started.value);
      started.value.dispose();
      pump(runtime);

      let timer: NodeJS.Timeout | undefined;
      const timeout = new Promise<"timeout">((resolve) => {
        timer = setTimeout(() => resolve("timeout"), this.limits.callTimeoutMs);
      });
      const outcome = await Promise.race([settled, timeout]);
      clearTimeout(timer);

      const fatal = this.bridge.fatal();
      if (fatal) throw fatal;
      if (outcome === "timeout") {
        this.broken = `call '${fn}' exceeded ${this.limits.callTimeoutMs} ms`;
        return scriptError("Timeout", this.broken, { limit: "time" });
      }
      if (outcome.error) {
        const err = describeVmError(ctx.dump(outcome.error));
        outcome.error.dispose();
        return classify(err);
      }
      const json = ctx.getString(outcome.value);
      outcome.value.dispose();
      const envelope = JSON.parse(json) as CallResult;
      return envelope.ok ? envelope : { ok: false, error: classify(envelope.error as VmError).error };
    } finally {
      this.inFlight--;
      if (this.inFlight === 0) this.bridge.idle();
    }
  }
}

// --- bridge installation -----------------------------------------------------------------------

function installBridge(ctx: QuickJSContext, runtime: QuickJSRuntime, bridge: HostBridge, frozenNow?: number) {
  const obj = ctx.newObject();

  const sync = (name: string, fn: (...args: QuickJSHandle[]) => string) => {
    ctx.newFunction(name, (...args) => ctx.newString(fn(...args))).consume((h) => ctx.setProp(obj, name, h));
  };
  const async = (name: string, fn: (...args: QuickJSHandle[]) => Promise<QuickJSHandle>) => {
    ctx
      .newFunction(name, (...args) => {
        const deferred = ctx.newPromise();
        fn(...args).then(
          (value) => {
            deferred.resolve(value);
            value.dispose();
            pump(runtime);
          },
          (e: unknown) => {
            const env = ctx.newString(JSON.stringify({ error: { kind: "Network", message: String(e) } }));
            deferred.resolve(env);
            env.dispose();
            pump(runtime);
          },
        );
        return deferred.handle;
      })
      .consume((h) => ctx.setProp(obj, name, h));
  };
  const str = (h: QuickJSHandle) => ctx.getString(h);
  const num = (h: QuickJSHandle) => ctx.getNumber(h);

  async("http", async (metaH, bodyH) => {
    const meta = str(metaH);
    let body: Uint8Array | undefined;
    if (bodyH !== undefined && ctx.typeof(bodyH) !== "undefined") {
      const lifetime = ctx.getArrayBuffer(bodyH);
      body = lifetime.value.slice();
      lifetime.dispose();
    }
    const res = await bridge.http(meta, body);
    const out = ctx.newObject();
    ctx.newString(res.env).consume((h) => ctx.setProp(out, "env", h));
    if (res.bytes) {
      const copy = res.bytes.buffer.slice(res.bytes.byteOffset, res.bytes.byteOffset + res.bytes.byteLength);
      ctx.newArrayBuffer(copy).consume((h) => ctx.setProp(out, "bytes", h));
    }
    return out;
  });
  sync("htmlSelect", (markup, id, selector) => bridge.htmlSelect(str(markup), num(id), str(selector)));
  sync("htmlCall", (op, id, arg) => bridge.htmlCall(str(op), num(id), str(arg)));
  async("storageGet", async (key) => ctx.newString(await bridge.storageGet(str(key))));
  async("storageSet", async (key, value) => ctx.newString(await bridge.storageSet(str(key), str(value))));
  async("sleep", async (ms) => ctx.newString(await bridge.sleep(num(ms))));
  ctx
    .newFunction("log", (level, text) => {
      bridge.log(str(level), str(text));
    })
    .consume((h) => ctx.setProp(obj, "log", h));
  if (frozenNow !== undefined) {
    ctx.newNumber(frozenNow).consume((h) => ctx.setProp(obj, "frozenNow", h));
  }

  ctx.setProp(ctx.global, "__hostBridge", obj);
  obj.dispose();
}

// --- helpers -------------------------------------------------------------------------------------

function evalGlobal(ctx: QuickJSContext, code: string, name: string): QuickJSHandle {
  const res = ctx.evalCode(code, name, { type: "global" });
  if (res.error) {
    const err = describeVmError(ctx.dump(res.error));
    res.error.dispose();
    throw new LoadError(`${name}: ${err.name}: ${err.message}`);
  }
  return res.value;
}

function pump(runtime: QuickJSRuntime) {
  const res = runtime.executePendingJobs();
  if (res.error) res.error.dispose();
}

interface VmError {
  type?: string;
  kind?: string;
  name?: string;
  message?: string;
  stack?: string;
  retryAfterSeconds?: number;
  details?: Record<string, unknown>;
}

function describeVmError(dumped: unknown): VmError {
  if (dumped && typeof dumped === "object") return dumped as VmError;
  return { name: "Thrown", message: String(dumped) };
}

function scriptError(name: string, message: string, details?: Record<string, unknown>): CallResult {
  return { ok: false, error: { type: "ScriptError", name, message, details } };
}

const NOT_DEFINED = /^'?([\w$]+)'? is not defined$/;

/** Maps raw VM errors to contract-level classifications (data-model.md › SourceError). */
function classify(err: VmError): { ok: false; error: CallError } {
  if (err.type === "SourceError" && err.kind) {
    return {
      ok: false,
      error: {
        type: "SourceError",
        kind: err.kind,
        message: err.message ?? "",
        retryAfterSeconds: err.retryAfterSeconds,
        details: err.details,
      },
    };
  }
  const name = err.name ?? "Error";
  const message = err.message ?? "";
  const missing = name === "ReferenceError" ? NOT_DEFINED.exec(message) : null;
  if (missing) {
    return scriptError(name, `forbidden capability: ${missing[1]}`, { capability: missing[1] }) as {
      ok: false;
      error: CallError;
    };
  }
  if (name === "InternalError" && message === "interrupted") {
    return scriptError(name, "call exceeded the time limit", { limit: "time" }) as { ok: false; error: CallError };
  }
  if (name === "InternalError" && /out of memory/.test(message)) {
    return scriptError(name, "engine memory limit exceeded", { limit: "memory" }) as { ok: false; error: CallError };
  }
  return { ok: false, error: { type: "ScriptError", name, message, stack: err.stack } };
}
