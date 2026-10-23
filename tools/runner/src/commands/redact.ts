// `redact <sourceDir>` — re-applies the source's redact.mjs to its existing fixture pool (after the
// redaction rules change), without touching the network. Snapshots then need `test --update`.
import type { CliOptions } from "../cli.ts";
import { EXIT, UsageError } from "../errors.ts";
import { loadRedactor, redactPool } from "../redaction.ts";
import { locateSource } from "../source.ts";

export async function redact(args: string[], _opts: CliOptions): Promise<number> {
  const [dir] = args;
  if (!dir) throw new UsageError("redact <sourceDir>");
  const files = locateSource(dir);
  const redactor = await loadRedactor(files.dir);
  if (!redactor) throw new UsageError(`${files.dir} has no redact.mjs`);
  const changed = redactPool(files.fixturesDir, redactor);
  console.log(`${changed} fixture bod${changed === 1 ? "y" : "ies"} redacted; run \`test ${dir} --update\` and review the snapshots`);
  return EXIT.ok;
}
