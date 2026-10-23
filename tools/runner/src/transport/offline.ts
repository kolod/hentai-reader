// Offline transport: answers requests only from a recorded fixture pool (contracts/fixtures-and-tests.md).
import { createHash } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { UnmatchedRequestError } from "../errors.ts";
import type { RawRequest, RawResponse, Transport } from "./types.ts";

export interface FixtureRequest {
  method: string;
  url: string;
  headers?: { range?: string };
  bodySha256?: string | null;
}

/** `delayMs` simulates latency (contract tests of concurrency); recorded fixtures never set it. */
export type FixtureResponse =
  | { status: number; headers?: Record<string, string>; body?: string | null; delayMs?: number }
  | { echo: true; delayMs?: number };

export interface FixtureEntry {
  request: FixtureRequest;
  response: FixtureResponse;
}

export interface FixtureIndex {
  format: 1;
  recordedAt: string;
  entries: FixtureEntry[];
}

export function sha256Hex(bytes: Uint8Array): string {
  return createHash("sha256").update(bytes).digest("hex");
}

export function fixtureKey(method: string, url: string, range: string | undefined, bodySha256: string | null | undefined) {
  return [method.toUpperCase(), url, range ?? "", bodySha256 ?? ""].join("\n");
}

export function loadFixtureIndex(fixturesDir: string): FixtureIndex {
  const file = join(fixturesDir, "index.json");
  if (!existsSync(file)) return { format: 1, recordedAt: new Date(0).toISOString(), entries: [] };
  return JSON.parse(readFileSync(file, "utf8")) as FixtureIndex;
}

export class OfflineTransport implements Transport {
  readonly recordedAt: number;
  private readonly entries = new Map<string, FixtureEntry>();
  private readonly dir: string;

  constructor(fixturesDir: string) {
    this.dir = fixturesDir;
    const index = loadFixtureIndex(fixturesDir);
    this.recordedAt = Date.parse(index.recordedAt);
    for (const entry of index.entries) {
      const r = entry.request;
      this.entries.set(fixtureKey(r.method, r.url, r.headers?.range, r.bodySha256), entry);
    }
  }

  async send(req: RawRequest): Promise<RawResponse> {
    const bodySha = req.body ? sha256Hex(req.body) : null;
    const entry = this.entries.get(fixtureKey(req.method, req.url, req.headers.range, bodySha));
    if (!entry) throw new UnmatchedRequestError(req.method, req.url, req.headers.range);
    if (entry.response.delayMs) await new Promise((resolve) => setTimeout(resolve, entry.response.delayMs));

    if ("echo" in entry.response) {
      const echo = { method: req.method, url: req.url, headers: req.headers, bodySha256: bodySha };
      return {
        status: 200,
        headers: [["content-type", "application/json"]],
        body: new TextEncoder().encode(JSON.stringify(echo)),
      };
    }
    const res = entry.response;
    const body = res.body ? readFileSync(join(this.dir, res.body)) : new Uint8Array();
    return {
      status: res.status,
      headers: Object.entries(res.headers ?? {}).map(([k, v]) => [k.toLowerCase(), v]),
      body: new Uint8Array(body),
    };
  }
}
