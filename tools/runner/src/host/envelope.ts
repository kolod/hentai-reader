// JSON envelopes exchanged with glue.js: `{ value }` or `{ error: { kind, message, options } }`.

export type ErrorKind = "RateLimited" | "LoginRequired" | "NotFound" | "SiteChanged" | "InvalidInput" | "Network";

export function envelopeValue(value: unknown): string {
  return JSON.stringify({ value: value === undefined ? null : value });
}

export function envelopeError(kind: ErrorKind, message: string, options?: Record<string, unknown>): string {
  return JSON.stringify({ error: { kind, message, options } });
}
