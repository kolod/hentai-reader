// `run <sourceDir> <fn> [args…]` — call one source function and print the result (FR-020).
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import type { CliOptions } from "../cli.ts";
import type { CallResult } from "../engine.ts";
import { EXIT, UsageError } from "../errors.ts";
import { CookieJar } from "../host/cookies.ts";
import { RESULT_TYPES, validate } from "../schema.ts";
import { locateSource, openSource, readJsonFile } from "../source.ts";
import { modeTransport } from "./modes.ts";

export async function run(args: string[], opts: CliOptions): Promise<number> {
  const [dir, fn, ...rawArgs] = args;
  if (!dir || !fn) throw new UsageError("run <sourceDir> <fn> [args…]");
  const files = locateSource(dir);
  const fnArgs = rawArgs.map(parseArg);
  const cookies = opts.cookies ? cookieJar(opts.cookies, opts.mode) : undefined;
  const transport = modeTransport(files, opts.mode);
  const source = await openSource(files, {
    mode: opts.mode,
    storage: opts.storage ? (readJsonFile(resolve(opts.storage)) as Record<string, string>) : undefined,
    cookies,
    transport,
    onLog: opts.verbose ? (l) => console.error(`[console.${l.level}] ${l.text}`) : undefined,
  });
  const { result, violations } = await callAndValidate(source, fn, fnArgs);
  if (opts.verbose) printRequests(source.host.requestLog);
  transport?.flush();

  if (!result.ok) {
    const e = result.error;
    const label = e.type === "SourceError" ? `SourceError(${e.kind})` : `ScriptError(${e.name})`;
    if (opts.json) console.log(JSON.stringify({ ok: false, error: e }, null, 2));
    else console.error(`${label}: ${e.message}`);
    return EXIT.scriptError;
  }
  if (opts.json) console.log(JSON.stringify({ ok: true, value: result.value, violations }, null, 2));
  else console.log(JSON.stringify(result.value, null, 2));
  if (violations.length) {
    console.error(`\ncontract violations in ${fn} result:`);
    for (const v of violations) console.error(`  ${fn}.${v}`);
    return EXIT.failures;
  }
  return EXIT.ok;
}

/** Calls a function and validates its result against the contract; also round-trips `sourceData`. */
export async function callAndValidate(
  source: { engine: { call(fn: string, args: unknown[]): Promise<CallResult> }; host: { httpHost: { isAllowed(url: string): boolean } } },
  fn: string,
  args: unknown[],
): Promise<{ result: CallResult; violations: string[] }> {
  const result = await source.engine.call(fn, args);
  if (!result.ok) return { result, violations: [] };
  const type = RESULT_TYPES[fn];
  const violations = type ? validate(type, result.value) : [];
  if (violations.length === 0) {
    for (const [path, url] of imageUrls(result.value, "")) {
      let allowed = false;
      try {
        allowed = source.host.httpHost.isAllowed(url);
      } catch {
        allowed = false;
      }
      if (!allowed) violations.push(`${path}: image host is not in manifest.domains (${url})`);
    }
  }
  if (fn === "getGallery" && result.value && typeof result.value === "object") {
    const sd = (result.value as { sourceData?: unknown }).sourceData;
    if (sd !== undefined && JSON.stringify(JSON.parse(JSON.stringify(sd))) !== JSON.stringify(sd)) {
      violations.push("sourceData: does not survive a JSON round-trip");
    }
  }
  return { result, violations };
}

/** Every image request URL in a result (cover, thumbnail, page image), with its field path. */
function* imageUrls(value: unknown, path: string): Generator<[string, string]> {
  if (Array.isArray(value)) {
    for (let i = 0; i < value.length; i++) yield* imageUrls(value[i], `${path}.${i}`);
    return;
  }
  if (!value || typeof value !== "object") return;
  const obj = value as Record<string, unknown>;
  const isRoot = path === "";
  if (isRoot && typeof obj.url === "string" && !("items" in obj)) yield ["url", obj.url];
  for (const key of ["cover", "thumbnail"]) {
    const img = obj[key] as { url?: unknown } | null | undefined;
    if (img && typeof img.url === "string") yield [`${path ? `${path.slice(1)}.` : ""}${key}.url`, img.url];
  }
  for (const key of ["items", "pages"]) {
    if (Array.isArray(obj[key])) yield* imageUrls(obj[key], `${path}.${key}`);
  }
}

function parseArg(raw: string): unknown {
  const text = raw.startsWith("@") ? readFileSync(resolve(raw.slice(1)), "utf8") : raw;
  try {
    return JSON.parse(text);
  } catch {
    throw new UsageError(`argument is not valid JSON: ${raw}`);
  }
}

export function cookieJar(file: string, mode: CliOptions["mode"]): CookieJar {
  if (mode === "offline") throw new UsageError("--cookies is only allowed with --live or --record");
  const jar = new CookieJar();
  jar.seedNetscape(readFileSync(resolve(file), "utf8"));
  return jar;
}

export function printRequests(log: { method: string; url: string; startedAt: number; endedAt?: number; status?: number; refused?: string; headers: Record<string, string> }[]) {
  if (!log.length) return;
  const t0 = log[0].startedAt;
  console.error("\nrequests:");
  for (const r of log) {
    const range = r.headers.range ? ` [${r.headers.range}]` : "";
    const status = r.refused ? `refused(${r.refused})` : String(r.status ?? "…");
    const dur = r.endedAt ? `${r.endedAt - r.startedAt}ms` : "";
    console.error(`  +${String(r.startedAt - t0).padStart(5)}ms ${status} ${r.method} ${r.url}${range} ${dur}`);
  }
}
