// `check <sourceDir>` — conformance check (FR-019): static types against the contract, manifest,
// exports, contract version, results of the source's own test cases, and fixture hygiene.
import { relative } from "node:path";
import ts from "typescript";
import type { CliOptions } from "../cli.ts";
import { loadCases, runCases } from "../cases.ts";
import { LoadError } from "../engine.ts";
import { EXIT, UsageError } from "../errors.ts";
import { paths } from "../paths.ts";
import { validate } from "../schema.ts";
import { ContractVersionError, locateSource, openSource, type SourceFiles } from "../source.ts";
import { auditFixtures } from "../fixture-audit.ts";
import { loadRedactor } from "../redaction.ts";

const REQUIRED = ["search", "getGallery", "getPageUrl"];

export async function check(args: string[], opts: CliOptions): Promise<number> {
  const [dir] = args;
  if (!dir) throw new UsageError("check <sourceDir>");
  const files = locateSource(dir);
  const violations = [
    ...staticCheck(files),
    ...(await runtimeCheck(files)),
    ...auditFixtures(files.fixturesDir, await loadRedactor(files.dir)),
  ];

  if (opts.json) {
    console.log(JSON.stringify({ source: files.name, violations }, null, 2));
  } else if (violations.length === 0) {
    console.log(`✓ ${files.name}: no violations`);
  } else {
    console.log(`✗ ${files.name}: ${violations.length} violation(s)`);
    for (const v of violations) console.log(`  ${v}`);
  }
  return violations.length ? EXIT.failures : EXIT.ok;
}

/** `tsc --noEmit` over the script with contract/tsconfig.scripts.json. */
export function staticCheck(files: SourceFiles): string[] {
  const config = ts.readConfigFile(paths.scriptsTsconfig, ts.sys.readFile);
  if (config.error) throw new UsageError(ts.flattenDiagnosticMessageText(config.error.messageText, " "));
  const parsed = ts.parseJsonConfigFileContent(config.config, ts.sys, paths.contract);
  const program = ts.createProgram({ rootNames: [paths.contractTypes, files.scriptPath], options: parsed.options });
  return ts.getPreEmitDiagnostics(program).map((d) => {
    const text = ts.flattenDiagnosticMessageText(d.messageText, " ");
    if (!d.file || d.start === undefined) return `static: ${text}`;
    const { line } = d.file.getLineAndCharacterOfPosition(d.start);
    return `static: ${relative(paths.root, d.file.fileName).replace(/\\/g, "/")}:${line + 1}: ${text}`;
  });
}

async function runtimeCheck(files: SourceFiles): Promise<string[]> {
  let source;
  try {
    source = await openSource(files, { mode: "offline", strictManifest: false });
  } catch (e) {
    if (e instanceof ContractVersionError) return [`manifest.contract: ${e.message}`];
    if (e instanceof LoadError) return [`load: ${e.message}`];
    throw e;
  }
  const out: string[] = [];
  for (const v of validate("SourceManifest", source.manifest)) out.push(`manifest.${v}`);

  const fns = new Set(source.description.functions);
  for (const fn of REQUIRED) if (!fns.has(fn)) out.push(`${fn}: required export missing`);
  const declared = (source.manifest.capabilities as { suggestTags?: boolean } | undefined)?.suggestTags === true;
  if (declared && !fns.has("suggestTags")) out.push("suggestTags: declared in manifest.capabilities but not exported");
  if (!declared && fns.has("suggestTags")) out.push("suggestTags: exported but not declared in manifest.capabilities");

  const cases = loadCases(files.testsDir);
  if (cases.length && out.length === 0) {
    for (const r of await runCases(files, cases, { mode: "offline", update: false })) {
      for (const m of r.messages) {
        if (m.startsWith("contract violation: ")) out.push(`${m.slice("contract violation: ".length)} (case ${r.name})`);
      }
      const err = r.result && !r.result.ok ? r.result.error : null;
      if (err && err.type === "ScriptError" && err.message.startsWith("forbidden capability")) {
        out.push(`${err.message} (case ${r.name})`);
      }
    }
  }
  return out;
}
