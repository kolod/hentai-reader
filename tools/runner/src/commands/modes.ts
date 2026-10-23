// Transport selection for --record; offline and live use openSource's defaults.
import { caseFixturesDir, type TestCase } from "../cases.ts";
import type { Mode, SourceFiles } from "../source.ts";
import type { Transport } from "../transport/types.ts";
import { loadRedactor, type Redactor } from "../redaction.ts";
import { LiveTransport } from "../transport/live.ts";
import { RecordingTransport } from "../transport/recorder.ts";

export interface FlushableTransport extends Transport {
  flush(): void;
}

export async function modeTransport(files: SourceFiles, mode: Mode): Promise<FlushableTransport | undefined> {
  if (mode !== "record") return undefined;
  return new RecordingTransport(files.fixturesDir, new LiveTransport(), await loadRedactor(files.dir));
}

/** One recorder per fixture pool, shared by every case of a run that uses that pool. */
export async function caseTransport(
  files: SourceFiles,
  mode: Mode,
): Promise<{ forCase: (c: TestCase) => Transport | undefined; flush(): void } | undefined> {
  if (mode !== "record") return undefined;
  const redact: Redactor | null = await loadRedactor(files.dir);
  const recorders = new Map<string, RecordingTransport>();
  return {
    forCase: (c) => {
      const dir = caseFixturesDir(files, c);
      let recorder = recorders.get(dir);
      if (!recorder) {
        recorder = new RecordingTransport(dir, new LiveTransport(), redact);
        recorders.set(dir, recorder);
      }
      return recorder;
    },
    flush: () => {
      for (const r of recorders.values()) r.flush();
    },
  };
}
