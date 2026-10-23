// Test helpers: inline sources, scriptable fake transport.
import { Engine } from "../src/engine.ts";
import { ReferenceHost } from "../src/host/index.ts";
import type { RawRequest, RawResponse, Transport } from "../src/transport/types.ts";

export const MANIFEST = {
  id: "t",
  name: "T",
  version: "0.0.1",
  contract: "1.0",
  domains: ["a.test", "*.b.test"],
  politeness: { minIntervalMs: 0, maxConcurrency: 4 },
  login: "none",
  filters: [],
  capabilities: { suggestTags: false },
};

export class FakeTransport implements Transport {
  readonly sent: RawRequest[] = [];
  handler: (req: RawRequest) => Promise<RawResponse> | RawResponse;

  constructor(handler?: (req: RawRequest) => Promise<RawResponse> | RawResponse) {
    this.handler = handler ?? (() => text(200, "ok"));
  }

  async send(req: RawRequest): Promise<RawResponse> {
    this.sent.push(req);
    return this.handler(req);
  }
}

export function text(status: number, body: string, headers: [string, string][] = []): RawResponse {
  return { status, headers: [["content-type", "text/plain; charset=utf-8"], ...headers], body: new TextEncoder().encode(body) };
}

export async function engineFor(
  code: string,
  opts: { transport?: Transport; politeness?: "enforce" | "skip"; manifest?: object; frozenNow?: number; callTimeoutMs?: number } = {},
) {
  const host = new ReferenceHost({ transport: opts.transport ?? new FakeTransport(), politeness: opts.politeness ?? "skip" });
  const manifest = opts.manifest ?? MANIFEST;
  const engine = await Engine.create({
    source: `export const manifest = ${JSON.stringify(manifest)};\n${code}`,
    sourceName: "inline.js",
    bridge: host,
    frozenNow: opts.frozenNow,
    limits: opts.callTimeoutMs ? { callTimeoutMs: opts.callTimeoutMs } : undefined,
  });
  host.setManifest(manifest as never);
  return { engine, host };
}

export const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
