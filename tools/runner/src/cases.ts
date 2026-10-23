// Test cases (`tests/<name>.case.json`) and their snapshots (`<name>.expected.json`):
// loading, "@case" argument references, dependency order, execution (contracts/fixtures-and-tests.md).
import { existsSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import type { CallResult } from "./engine.ts";
import { UsageError } from "./errors.ts";
import { callAndValidate } from "./commands/run.ts";
import { openSource, type Mode, type SourceFiles } from "./source.ts";
import type { Transport } from "./transport/types.ts";
import type { CookieJar } from "./host/cookies.ts";
import { diff } from "./diff.ts";

const FUNCTIONS = new Set(["search", "getGallery", "getPageUrl", "suggestTags", "getAccountStatus"]);

export interface TestCase {
  name: string;
  description: string;
  call: { fn: string; args: unknown[] };
  storage?: Record<string, string>;
  /** Source settings for contract 1.1 sources, merged over the manifest defaults. */
  settings?: Record<string, string>;
  /**
   * Fixture pool (`fixtures/<name>/`) for cases whose requests equal another case's but whose
   * responses differ by account state, e.g. "logged-in". Default: the main pool.
   */
  fixtures?: string;
  expectError?: { kind: string } | null;
}

/** Fixtures directory of a case (its pool, or the source's main pool). */
export function caseFixturesDir(files: SourceFiles, c: TestCase): string {
  return c.fixtures ? join(files.fixturesDir, c.fixtures) : files.fixturesDir;
}

export interface CaseResult {
  name: string;
  status: "pass" | "fail" | "updated";
  messages: string[];
  result?: CallResult;
}

export function loadCases(testsDir: string): TestCase[] {
  if (!existsSync(testsDir)) return [];
  const cases = readdirSync(testsDir)
    .filter((f) => f.endsWith(".case.json"))
    .sort()
    .map((f) => {
      const name = f.slice(0, -".case.json".length);
      const raw = JSON.parse(readFileSync(join(testsDir, f), "utf8")) as Partial<TestCase>;
      const problems: string[] = [];
      if (typeof raw.description !== "string") problems.push("description must be a string");
      if (!raw.call || !FUNCTIONS.has(raw.call.fn as string)) problems.push(`call.fn must be one of ${[...FUNCTIONS].join(", ")}`);
      if (!raw.call || !Array.isArray(raw.call.args)) problems.push("call.args must be an array");
      if (raw.settings !== undefined && (typeof raw.settings !== "object" || raw.settings === null)) {
        problems.push("settings must be an object");
      }
      if (raw.fixtures !== undefined && !(typeof raw.fixtures === "string" && /^[a-z0-9-]+$/.test(raw.fixtures) && raw.fixtures !== "bodies")) {
        problems.push("fixtures must be a pool name ([a-z0-9-]+, not 'bodies')");
      }
      if (problems.length) throw new UsageError(`${f}: ${problems.join("; ")}`);
      return { name, ...raw } as TestCase;
    });
  orderCases(cases);
  return cases;
}

/** Names of all cases referenced anywhere in the arguments (also inside objects and arrays). */
function refsOf(c: TestCase): string[] {
  const out: string[] = [];
  const walk = (v: unknown): void => {
    if (typeof v === "string" && v.startsWith("@")) out.push(parseRef(v).name);
    else if (Array.isArray(v)) v.forEach(walk);
    else if (v && typeof v === "object") Object.values(v).forEach(walk);
  };
  c.call.args.forEach(walk);
  return out;
}

/** Replaces every "@case[.path]" string, at any depth, with the referenced value. */
function substitute(v: unknown, lookup: (name: string) => unknown): unknown {
  if (typeof v === "string" && v.startsWith("@")) {
    const ref = parseRef(v);
    return pick(lookup(ref.name), ref.path);
  }
  if (Array.isArray(v)) return v.map((x) => substitute(x, lookup));
  if (v && typeof v === "object") {
    return Object.fromEntries(Object.entries(v).map(([k, x]) => [k, substitute(x, lookup)]));
  }
  return v;
}

/** "@case" or "@case.path.to.field" (e.g. "@search-front.nextCursor"). */
function parseRef(arg: string): { name: string; path: string[] } {
  const [name, ...path] = arg.slice(1).split(".");
  return { name, path };
}

function pick(value: unknown, path: string[]): unknown {
  return path.reduce<unknown>((v, key) => (v !== null && typeof v === "object" ? (v as Record<string, unknown>)[key] : undefined), value);
}

/** Sorts cases so referenced cases run first; rejects unknown references and cycles. */
function orderCases(cases: TestCase[]): void {
  const byName = new Map(cases.map((c) => [c.name, c]));
  const ordered: TestCase[] = [];
  const state = new Map<string, "visiting" | "done">();
  const visit = (c: TestCase, path: string[]) => {
    if (state.get(c.name) === "done") return;
    if (state.get(c.name) === "visiting") throw new UsageError(`test case reference cycle: ${[...path, c.name].join(" → ")}`);
    state.set(c.name, "visiting");
    for (const ref of refsOf(c)) {
      const dep = byName.get(ref);
      if (!dep) throw new UsageError(`${c.name}.case.json references unknown case @${ref}`);
      visit(dep, [...path, c.name]);
    }
    state.set(c.name, "done");
    ordered.push(c);
  };
  for (const c of cases) visit(c, []);
  cases.splice(0, cases.length, ...ordered);
}

/** The case plus every case it (transitively) references, in execution order. */
export function selectCases(cases: TestCase[], only: string | undefined): TestCase[] {
  if (!only) return cases;
  const byName = new Map(cases.map((c) => [c.name, c]));
  if (!byName.has(only)) throw new UsageError(`no test case named '${only}'`);
  const needed = new Set<string>();
  const add = (name: string) => {
    if (needed.has(name)) return;
    needed.add(name);
    for (const ref of refsOf(byName.get(name)!)) add(ref);
  };
  add(only);
  return cases.filter((c) => needed.has(c.name));
}

function snapshotPath(files: SourceFiles, name: string): string {
  return join(files.testsDir, `${name}.expected.json`);
}

function readSnapshot(files: SourceFiles, name: string): unknown | undefined {
  const p = snapshotPath(files, name);
  return existsSync(p) ? JSON.parse(readFileSync(p, "utf8")) : undefined;
}

export interface RunCasesOptions {
  mode: Mode;
  update: boolean;
  /** Per-case transport factory (recording); default is chosen by mode. */
  transport?: (c: TestCase) => Transport | undefined;
  /** False for the live pass of --record: results are kept, snapshots are not consulted. */
  compareSnapshots?: boolean;
  /** Fresh cookie jar per case (live/record with --cookies). */
  cookies?: () => CookieJar;
}

export async function runCases(files: SourceFiles, cases: TestCase[], opts: RunCasesOptions): Promise<CaseResult[]> {
  const values = new Map<string, unknown>();
  const results: CaseResult[] = [];
  for (const c of cases) {
    const args = substitute(c.call.args, (name) => (values.has(name) ? values.get(name) : readSnapshot(files, name))) as unknown[];
    const source = await openSource({ ...files, fixturesDir: caseFixturesDir(files, c) }, {
      mode: opts.mode,
      storage: c.storage,
      transport: opts.transport?.(c),
      cookies: opts.cookies?.(),
    });
    const { result, violations } = await callAndValidate(source, c.call.fn, args, c.settings);
    const messages = violations.map((v) => `contract violation: ${c.call.fn}.${v}`);

    if (c.expectError) {
      const kind = !result.ok && result.error.type === "SourceError" ? result.error.kind : null;
      if (kind !== c.expectError.kind) {
        messages.push(
          result.ok
            ? `expected SourceError(${c.expectError.kind}), got a result`
            : `expected SourceError(${c.expectError.kind}), got ${errorLabel(result)}`,
        );
      }
      results.push({ name: c.name, status: messages.length ? "fail" : "pass", messages, result });
      continue;
    }
    if (!result.ok) {
      messages.push(`unexpected ${errorLabel(result)}`);
      results.push({ name: c.name, status: "fail", messages, result });
      continue;
    }
    values.set(c.name, result.value);
    if (opts.compareSnapshots === false) {
      results.push({ name: c.name, status: messages.length ? "fail" : "pass", messages, result });
      continue;
    }
    const expected = readSnapshot(files, c.name);
    if (opts.update) {
      writeFileSync(snapshotPath(files, c.name), `${JSON.stringify(result.value, null, 2)}\n`);
      results.push({ name: c.name, status: messages.length ? "fail" : "updated", messages, result });
      continue;
    }
    if (expected === undefined) {
      messages.push(`missing snapshot ${c.name}.expected.json (run with --update)`);
    } else {
      const d = diff(expected, result.value);
      if (d.length) messages.push("result differs from snapshot:", ...d.map((l) => `  ${l}`));
    }
    results.push({ name: c.name, status: messages.length ? "fail" : "pass", messages, result });
  }
  return results;
}

function errorLabel(result: CallResult): string {
  if (result.ok) return "result";
  const e = result.error;
  return e.type === "SourceError" ? `SourceError(${e.kind}): ${e.message}` : `ScriptError(${e.name}): ${e.message}`;
}
