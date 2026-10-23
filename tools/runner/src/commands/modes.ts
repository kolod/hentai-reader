// Transport selection for --record; offline and live use openSource's defaults.
import type { TestCase } from "../cases.ts";
import type { Mode, SourceFiles } from "../source.ts";
import type { Transport } from "../transport/types.ts";
import { loadRedactor } from "../redaction.ts";
import { LiveTransport } from "../transport/live.ts";
import { RecordingTransport } from "../transport/recorder.ts";

export interface FlushableTransport extends Transport {
  flush(): void;
}

export async function modeTransport(files: SourceFiles, mode: Mode): Promise<FlushableTransport | undefined> {
  if (mode !== "record") return undefined;
  return new RecordingTransport(files.fixturesDir, new LiveTransport(), await loadRedactor(files.dir));
}

/** One recorder shared by every case of a run, so all responses land in the same pool. */
export async function caseTransport(
  files: SourceFiles,
  mode: Mode,
): Promise<{ forCase: (c: TestCase) => Transport | undefined; flush(): void } | undefined> {
  const recorder = await modeTransport(files, mode);
  if (!recorder) return undefined;
  return { forCase: () => recorder, flush: () => recorder.flush() };
}
