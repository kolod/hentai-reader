// `contract-test` — runs contract/tests/<name>/ against the reference host (FR-018, FR-021).
// Each test's run() executes inside the engine; expect.json adds host-side assertions on the
// request log (research R12, contracts/fixtures-and-tests.md).
import { existsSync, readdirSync } from "node:fs";
import { join } from "node:path";
import type { CliOptions } from "../cli.ts";
import { EXIT, UsageError } from "../errors.ts";
import { CookieJar } from "../host/cookies.ts";
import type { RequestLogEntry } from "../host/http.ts";
import type { StorageHost } from "../host/storage.ts";
import { paths } from "../paths.ts";
import { openSource, readJsonFile, type SourceFiles } from "../source.ts";
import { OfflineTransport } from "../transport/offline.ts";

/** Timer granularity tolerance for gap assertions. */
const GAP_TOLERANCE_MS = 2;

interface Expectations {
  minGapMs?: number;
  maxInFlight?: number;
  refusedUrls?: string[];
  cookies?: { seed?: Record<string, string>; sentTo?: string[]; notSentTo?: string[] };
  other?: string;
}

export async function contractTest(_args: string[], opts: CliOptions): Promise<number> {
  if (!existsSync(paths.contractTests)) throw new UsageError(`no contract tests at ${paths.contractTests}`);
  let names = readdirSync(paths.contractTests).filter((n) => existsSync(join(paths.contractTests, n, "test.js")));
  if (opts.case) {
    if (!names.includes(opts.case)) throw new UsageError(`no contract test named '${opts.case}'`);
    names = [opts.case];
  }
  const started = Date.now();
  let failed = 0;
  for (const name of names.sort()) {
    const problems = await runOne(join(paths.contractTests, name));
    if (problems.length) failed++;
    console.log(`${problems.length ? "✗" : "✓"} ${name}`);
    for (const p of problems) console.log(`    ${p}`);
  }
  console.log(`\n${names.length - failed} passed, ${failed} failed (${Date.now() - started} ms)`);
  return failed ? EXIT.failures : EXIT.ok;
}

function filesFor(dir: string, script: string): SourceFiles {
  return { dir, name: script, scriptPath: join(dir, script), fixturesDir: join(dir, "fixtures"), testsDir: join(dir, "tests") };
}

async function runOne(dir: string): Promise<string[]> {
  const expect = (existsSync(join(dir, "expect.json")) ? readJsonFile(join(dir, "expect.json")) : {}) as Expectations;
  const storageRegistry = new Map<string, StorageHost>();
  const problems: string[] = [];

  const execute = async (script: string): Promise<RequestLogEntry[]> => {
    const cookies = new CookieJar();
    for (const [domain, value] of Object.entries(expect.cookies?.seed ?? {})) cookies.seed(domain, value);
    // Politeness is enforced here: timing assertions depend on it.
    const transport = new OfflineTransport(join(dir, "fixtures"));
    const source = await openSource(filesFor(dir, script), { mode: "live", transport, cookies, storageRegistry });
    const res = await source.engine.call("run", []);
    if (!res.ok) {
      const e = res.error;
      problems.push(`${script}: ${e.type === "SourceError" ? `SourceError(${e.kind})` : e.name}: ${e.message}`);
    }
    return source.host.requestLog;
  };

  try {
    if (expect.other) await execute(expect.other);
    const log = await execute("test.js");
    problems.push(...assertLog(log, expect));
  } catch (e) {
    problems.push(e instanceof Error ? e.message : String(e));
  }
  return problems;
}

function assertLog(log: RequestLogEntry[], expect: Expectations): string[] {
  const out: string[] = [];
  const sent = log.filter((r) => !r.refused);

  if (expect.minGapMs !== undefined) {
    const starts = sent.map((r) => r.startedAt).sort((a, b) => a - b);
    for (let i = 1; i < starts.length; i++) {
      const gap = starts[i] - starts[i - 1];
      if (gap < expect.minGapMs - GAP_TOLERANCE_MS) out.push(`request start gap ${gap} ms < minGapMs ${expect.minGapMs}`);
    }
    if (starts.length < 2) out.push("minGapMs: fewer than two requests were sent");
  }

  if (expect.maxInFlight !== undefined) {
    // In flight at each request's start: itself plus every other request started no later and
    // not yet ended (an end at the same instant frees the slot).
    let max = 0;
    for (const r of sent) {
      const overlapping = sent.filter(
        (o) => o !== r && o.startedAt <= r.startedAt && (o.endedAt ?? Infinity) > r.startedAt,
      ).length;
      max = Math.max(max, overlapping + 1);
    }
    if (max > expect.maxInFlight) out.push(`${max} requests in flight > maxInFlight ${expect.maxInFlight}`);
    if (max < expect.maxInFlight) out.push(`only ${max} requests in flight; the test does not exercise maxInFlight ${expect.maxInFlight}`);
  }

  for (const url of expect.refusedUrls ?? []) {
    if (!log.some((r) => r.refused && r.url === url)) out.push(`expected the host to refuse ${url}`);
  }
  if (sent.some((r) => (expect.refusedUrls ?? []).includes(r.url))) out.push("a refused URL reached the transport");

  const hostOf = (r: RequestLogEntry) => new URL(r.url).hostname;
  for (const host of expect.cookies?.sentTo ?? []) {
    if (!sent.some((r) => hostOf(r) === host && r.headers.cookie)) out.push(`expected cookies to be sent to ${host}`);
  }
  for (const host of expect.cookies?.notSentTo ?? []) {
    if (sent.some((r) => hostOf(r) === host && r.headers.cookie)) out.push(`cookies must not be sent to ${host}`);
  }
  return out;
}
