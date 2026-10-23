// `test <sourceDir>` — run test cases against snapshots; non-zero exit on any failure (FR-025).
// With --record: run live while recording fixtures, then replay offline and require identical
// results, then compare (or --update) snapshots from the offline replay.
import type { CliOptions } from "../cli.ts";
import { loadCases, runCases, selectCases, type CaseResult } from "../cases.ts";
import { diff } from "../diff.ts";
import { EXIT, UsageError } from "../errors.ts";
import { locateSource } from "../source.ts";
import { caseTransport } from "./modes.ts";
import { cookieJar } from "./run.ts";

export async function test(args: string[], opts: CliOptions): Promise<number> {
  const [dir] = args;
  if (!dir) throw new UsageError("test <sourceDir>");
  const files = locateSource(dir);
  const cases = selectCases(loadCases(files.testsDir), opts.case);
  if (cases.length === 0) {
    console.error(`no test cases in ${files.testsDir}`);
    return EXIT.failures;
  }
  const cookies = opts.cookies ? () => cookieJar(opts.cookies!, opts.mode) : undefined;
  const started = Date.now();
  let results: CaseResult[];

  if (opts.mode === "record") {
    const recorder = caseTransport(files, "record")!;
    const live = await runCases(files, cases, { mode: "record", update: false, compareSnapshots: false, transport: recorder.forCase, cookies });
    recorder.flush();
    const replay = await runCases(files, cases, { mode: "offline", update: opts.update });
    results = replay.map((r, i) => {
      const l = live[i];
      const extra = l.messages.map((m) => `live: ${m}`);
      if (l.result?.ok && r.result?.ok) {
        const d = diff(l.result.value, r.result.value);
        if (d.length) extra.push("offline replay differs from the live result:", ...d.map((x) => `  ${x}`));
      }
      const status = extra.length > 0 || l.status === "fail" ? "fail" : r.status;
      return { ...r, messages: [...extra, ...r.messages], status };
    });
  } else {
    results = await runCases(files, cases, { mode: opts.mode, update: opts.update, cookies });
  }

  if (opts.json) {
    console.log(JSON.stringify(results.map(({ result: _r, ...r }) => r), null, 2));
  } else {
    for (const r of results) {
      const mark = r.status === "pass" ? "✓" : r.status === "updated" ? "↻" : "✗";
      console.log(`${mark} ${r.name}`);
      for (const m of r.messages) console.log(`    ${m}`);
    }
  }
  const failed = results.filter((r) => r.status === "fail").length;
  const updated = results.filter((r) => r.status === "updated").length;
  console.log(
    `\n${results.length - failed - updated} passed, ${failed} failed${updated ? `, ${updated} updated` : ""} (${Date.now() - started} ms)`,
  );
  return failed ? EXIT.failures : EXIT.ok;
}
