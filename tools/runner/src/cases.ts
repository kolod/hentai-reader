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

const FUNCTIONS = new Set(["search", "getGallery", "getPageUrl", "suggestTags"]);

export interface TestCase {
  name: string;
  description: string;
  call: { fn: string; args: unknown[] };
  storage?: Record<string, string>;
  expectError?: { kind: string } | null;
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
      if (problems.length) throw new UsageError(`${f}: ${problems.join("; ")}`);
      return { name, ...raw } as TestCase;
    });
  orderCases(cases);
  return cases;
}

function refsOf(c: TestCase): string[] {
  return c.call.args.filter((a): a is string => typeof a === "string" && a.startsWith("@")).map((a) => a.slice(1));
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
    const args = c.call.args.map((a) => {
      if (typeof a !== "string" || !a.startsWith("@")) return a;
      const ref = a.slice(1);
      return values.has(ref) ? values.get(ref) : readSnapshot(files, ref);
    });
    const source = await openSource(files, {
      mode: opts.mode,
      storage: c.storage,
      transport: opts.transport?.(c),
      cookies: opts.cookies?.(),
    });
    const { result, violations } = await callAndValidate(source, c.call.fn, args);
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
