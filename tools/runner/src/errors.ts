// Runner-level errors that map to CLI exit codes (contracts/runner-cli.md).

export const EXIT = {
  ok: 0,
  failures: 1,
  usage: 2,
  scriptError: 3,
  unmatchedRequest: 4,
} as const;

/** Bad arguments or missing files. */
export class UsageError extends Error {}

/** A transport failure that aborts the whole run, whatever the script does with the request. */
export class FatalTransportError extends Error {}

/** Offline mode received a request with no recorded fixture. */
export class UnmatchedRequestError extends FatalTransportError {
  constructor(method: string, url: string, range: string | undefined) {
    super(`no recorded fixture for ${method} ${url}${range ? ` (range: ${range})` : ""}`);
  }
}

/** Record mode refused to store a response (image body — Constitution X). */
export class FixtureRecordingError extends FatalTransportError {}
