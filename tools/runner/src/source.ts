// Locating a source on disk and opening it: transport per mode, reference host, engine, manifest.
import { existsSync, readFileSync } from "node:fs";
import { basename, join, resolve } from "node:path";
import { compatibilityError } from "./compat.ts";
import { Engine, LoadError, type SourceDescription } from "./engine.ts";
import { UsageError } from "./errors.ts";
import { CookieJar } from "./host/cookies.ts";
import type { HttpManifest } from "./host/http.ts";
import { ReferenceHost, type LogLine } from "./host/index.ts";
import { StorageHost } from "./host/storage.ts";
import { loginPageViolations } from "./login-page.ts";
import { validate } from "./schema.ts";
import { LiveTransport } from "./transport/live.ts";
import { OfflineTransport } from "./transport/offline.ts";
import type { Transport } from "./transport/types.ts";

export type Mode = "offline" | "live" | "record";

/** The source requires a contract version this host does not support (FR-002). */
export class ContractVersionError extends LoadError {}

export interface SourceFiles {
  dir: string;
  name: string;
  scriptPath: string;
  fixturesDir: string;
  testsDir: string;
}

export function locateSource(dir: string): SourceFiles {
  const abs = resolve(dir);
  const name = basename(abs);
  const scriptPath = join(abs, `${name}.js`);
  if (!existsSync(scriptPath)) throw new UsageError(`source script not found: ${scriptPath}`);
  return { dir: abs, name, scriptPath, fixturesDir: join(abs, "fixtures"), testsDir: join(abs, "tests") };
}

export interface OpenOptions {
  mode: Mode;
  storage?: Record<string, string>;
  storageRegistry?: Map<string, StorageHost>;
  cookies?: CookieJar;
  onLog?: (line: LogLine) => void;
  /** Overrides the transport chosen by `mode` (tests, recording). */
  transport?: Transport;
  /** Validate the manifest before use (default true). */
  strictManifest?: boolean;
}

export interface OpenSource {
  files: SourceFiles;
  engine: Engine;
  host: ReferenceHost;
  description: SourceDescription;
  manifest: Record<string, unknown>;
}

export async function openSource(files: SourceFiles, opts: OpenOptions): Promise<OpenSource> {
  const offline = opts.mode === "offline" ? new OfflineTransport(files.fixturesDir) : null;
  const transport = opts.transport ?? offline ?? new LiveTransport();
  const storage = new StorageHost();
  if (opts.storage) storage.seed(opts.storage);
  const host = new ReferenceHost({
    transport,
    politeness: opts.mode === "offline" ? "skip" : "enforce",
    storage,
    storageRegistry: opts.storageRegistry,
    cookies: opts.cookies,
    onLog: opts.onLog,
  });
  const engine = await Engine.create({
    source: readFileSync(files.scriptPath, "utf8"),
    sourceName: basename(files.scriptPath),
    bridge: host,
    frozenNow: offline ? offline.recordedAt : undefined,
  });
  const description = await engine.describe();
  const manifest = description.manifest as Record<string, unknown> | null;
  if (!manifest || typeof manifest !== "object") throw new LoadError("source does not export `manifest`");
  const incompatible = compatibilityError(manifest.contract);
  if (incompatible) throw new ContractVersionError(incompatible);
  if (opts.strictManifest !== false) {
    const violations = validate("SourceManifest", manifest);
    if (violations.length) throw new LoadError(`invalid manifest:\n  manifest.${violations.join("\n  manifest.")}`);
    const login = loginPageViolations(manifest);
    if (login.length) throw new LoadError(`invalid manifest:\n  ${login.join("\n  ")}`);
  }
  host.setManifest(manifest as unknown as HttpManifest);
  return { files, engine, host, description, manifest };
}

// --- settings and calling convention (contract 1.1) -------------------------------------------

interface SettingDef {
  id: string;
  values: string[];
  default: string;
}

/** Manifest defaults merged with user overrides; unknown ids or values are usage errors. */
export function resolveSettings(manifest: Record<string, unknown>, overrides: Record<string, string> = {}): Record<string, string> {
  const defs = (manifest.settings ?? []) as SettingDef[];
  const out: Record<string, string> = Object.fromEntries(defs.map((d) => [d.id, d.default]));
  for (const [id, value] of Object.entries(overrides)) {
    const def = defs.find((d) => d.id === id);
    if (!def) throw new UsageError(`unknown setting '${id}' (declared: ${defs.map((d) => d.id).join(", ") || "none"})`);
    if (!def.values.includes(value)) throw new UsageError(`setting ${id}: '${value}' not in ${def.values.join(", ")}`);
    out[id] = value;
  }
  return out;
}

/** True when the source declares contract 1.1 or later (receives settings, null retry). */
export function takesSettings(manifest: Record<string, unknown>): boolean {
  const m = /^(\d+)\.(\d+)$/.exec(String(manifest.contract));
  return !!m && (Number(m[1]) > 1 || Number(m[2]) >= 1);
}

/** Positional arity of each source function before the settings argument. */
const ARITY: Record<string, number> = { search: 2, getGallery: 1, getPageUrl: 3, suggestTags: 1, getAccountStatus: 0 };

/**
 * Arguments as the contract's calling convention requires: 1.0 sources get exactly the 1.0 call
 * (no settings, absent retry omitted); 1.1 sources get null for absent positions plus settings.
 */
export function callArgs(manifest: Record<string, unknown>, fn: string, args: unknown[], settings: Record<string, string>): unknown[] {
  if (fn === "getAccountStatus" && (manifest.capabilities as { accountStatus?: boolean } | undefined)?.accountStatus !== true) {
    throw new UsageError("getAccountStatus is not declared in manifest.capabilities");
  }
  if (!takesSettings(manifest)) {
    return fn === "getPageUrl" && args.length > 2 && (args[2] === null || args[2] === undefined) ? args.slice(0, 2) : args;
  }
  const arity = ARITY[fn] ?? args.length;
  const positional = args.slice(0, arity);
  while (positional.length < arity) positional.push(null);
  return [...positional.map((a) => (a === undefined ? null : a)), settings];
}

export function readJsonFile(path: string): unknown {
  if (!existsSync(path)) throw new UsageError(`file not found: ${path}`);
  return JSON.parse(readFileSync(path, "utf8"));
}
