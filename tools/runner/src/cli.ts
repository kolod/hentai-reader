#!/usr/bin/env node
// source-runner — runs Hentai Reader source scripts outside the apps (contracts/runner-cli.md).
import { parseArgs } from "node:util";
import { EXIT, FatalTransportError, UnmatchedRequestError, UsageError } from "./errors.ts";
import { LoadError } from "./engine.ts";

const USAGE = `source-runner <command> [options]

Commands:
  run <sourceDir> <fn> [args…]   Call one source function (args are JSON, @file.json reads a file)
  test <sourceDir>               Run the source's test cases against snapshots
  check <sourceDir>              Conformance check (static types, manifest, exports, fixtures)
  contract-test                  Run the host contract suite against the reference host
  redact <sourceDir>             Re-apply the source's redact.mjs to its recorded fixtures

Options:
  --offline        Replay recorded fixtures, no network (default)
  --live           Use the real network
  --record         Use the real network and record fixtures
  --storage <file> Seed per-source storage from a JSON object
  --cookies <file> Seed cookies from a Netscape cookies.txt (live/record only)
  --case <name>    Only this test case (test, contract-test)
  --update         Rewrite snapshots (test)
  --json           Machine-readable output
  --verbose        Print requests and script console output
  -h, --help       Show this help`;

export interface CliOptions {
  mode: "offline" | "live" | "record";
  storage?: string;
  cookies?: string;
  case?: string;
  update: boolean;
  json: boolean;
  verbose: boolean;
}

async function main(argv: string[]): Promise<number> {
  const { values, positionals } = parseArgs({
    args: argv,
    allowPositionals: true,
    options: {
      offline: { type: "boolean" },
      live: { type: "boolean" },
      record: { type: "boolean" },
      storage: { type: "string" },
      cookies: { type: "string" },
      case: { type: "string" },
      update: { type: "boolean" },
      json: { type: "boolean" },
      verbose: { type: "boolean" },
      help: { type: "boolean", short: "h" },
    },
  });
  const [command, ...rest] = positionals;
  if (values.help || !command) {
    console.log(USAGE);
    return values.help ? EXIT.ok : EXIT.usage;
  }
  const modes = [values.offline, values.live, values.record].filter(Boolean).length;
  if (modes > 1) throw new UsageError("choose one of --offline, --live, --record");
  const opts: CliOptions = {
    mode: values.live ? "live" : values.record ? "record" : "offline",
    storage: values.storage,
    cookies: values.cookies,
    case: values.case,
    update: values.update ?? false,
    json: values.json ?? false,
    verbose: values.verbose ?? false,
  };

  switch (command) {
    case "run":
      return (await import("./commands/run.ts")).run(rest, opts);
    case "test":
      return (await import("./commands/test.ts")).test(rest, opts);
    case "check":
      return (await import("./commands/check.ts")).check(rest, opts);
    case "contract-test":
      return (await import("./commands/contract-test.ts")).contractTest(rest, opts);
    case "redact":
      return (await import("./commands/redact.ts")).redact(rest, opts);
    default:
      throw new UsageError(`unknown command '${command}'\n\n${USAGE}`);
  }
}

main(process.argv.slice(2)).then(
  (code) => {
    process.exitCode = code;
  },
  (e: unknown) => {
    if (e instanceof UsageError) {
      console.error(`usage error: ${e.message}`);
      process.exitCode = EXIT.usage;
    } else if (e instanceof UnmatchedRequestError) {
      console.error(`unmatched request: ${e.message}`);
      process.exitCode = EXIT.unmatchedRequest;
    } else if (e instanceof FatalTransportError) {
      console.error(`aborted: ${e.message}`);
      process.exitCode = EXIT.scriptError;
    } else if (e instanceof LoadError) {
      console.error(`load error: ${e.message}`);
      process.exitCode = EXIT.scriptError;
    } else {
      console.error(e);
      process.exitCode = EXIT.scriptError;
    }
  },
);
