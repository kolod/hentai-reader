// Recording transport: performs live requests and writes sanitised fixtures (FR-026,
// contracts/fixtures-and-tests.md). Never stores cookies, credentials or image bodies.
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { FixtureRecordingError } from "../errors.ts";
import { fixtureKey, loadFixtureIndex, sha256Hex, type FixtureEntry } from "./offline.ts";
import { isImage } from "./sniff.ts";
import type { RawRequest, RawResponse, Transport } from "./types.ts";

const KEPT_RESPONSE_HEADERS = new Set([
  "content-type",
  "content-range",
  "content-length",
  "location",
  "retry-after",
  "last-modified",
  "etag",
]);

export class RecordingTransport implements Transport {
  private readonly dir: string;
  private readonly live: Transport;
  private readonly entries = new Map<string, FixtureEntry>();
  private readonly bodies = new Map<string, Uint8Array>();

  constructor(fixturesDir: string, live: Transport) {
    this.dir = fixturesDir;
    this.live = live;
    for (const entry of loadFixtureIndex(fixturesDir).entries) {
      const r = entry.request;
      this.entries.set(fixtureKey(r.method, r.url, r.headers?.range, r.bodySha256), entry);
    }
  }

  async send(req: RawRequest): Promise<RawResponse> {
    const res = await this.live.send(req);
    const contentType = res.headers.find(([n]) => n === "content-type")?.[1] ?? "";
    if (/^image\//i.test(contentType) || isImage(res.body)) {
      throw new FixtureRecordingError(`refusing to record image data from ${req.url} (Constitution X)`);
    }
    const headers: Record<string, string> = {};
    for (const [name, value] of res.headers) {
      if (KEPT_RESPONSE_HEADERS.has(name)) headers[name] = value;
    }
    let body: string | null = null;
    if (res.body.length > 0) {
      body = `bodies/${sha256Hex(res.body).slice(0, 16)}.${extension(contentType)}`;
      this.bodies.set(body, res.body);
    }
    const bodySha256 = req.body ? sha256Hex(req.body) : null;
    const range = req.headers.range;
    this.entries.set(fixtureKey(req.method, req.url, range, bodySha256), {
      request: {
        method: req.method,
        url: req.url,
        ...(range ? { headers: { range } } : {}),
        ...(bodySha256 ? { bodySha256 } : {}),
      },
      response: { status: res.status, headers, body },
    });
    return res;
  }

  /** Writes new bodies and the merged, sorted index; sets recordedAt to now. */
  flush(): void {
    mkdirSync(join(this.dir, "bodies"), { recursive: true });
    for (const [path, bytes] of this.bodies) writeFileSync(join(this.dir, path), bytes);
    this.bodies.clear();
    const entries = [...this.entries.values()].sort(
      (a, b) =>
        a.request.url.localeCompare(b.request.url) ||
        (a.request.headers?.range ?? "").localeCompare(b.request.headers?.range ?? "") ||
        a.request.method.localeCompare(b.request.method),
    );
    const index = { format: 1, recordedAt: new Date().toISOString(), entries };
    writeFileSync(join(this.dir, "index.json"), `${JSON.stringify(index, null, 2)}\n`);
  }
}

function extension(contentType: string): string {
  const t = contentType.toLowerCase();
  if (t.includes("html")) return "html";
  if (t.includes("json")) return "json";
  if (t.includes("javascript") || t.includes("ecmascript")) return "js";
  if (t.startsWith("text/")) return "txt";
  return "bin";
}
