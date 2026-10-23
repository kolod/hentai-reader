// Recording transport: performs live requests and writes sanitised fixtures (FR-026,
// contracts/fixtures-and-tests.md). Never stores cookies, credentials or image bodies; applies the
// source's redactor (redact.mjs) to text bodies before storing them.
import { mkdirSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { FixtureRecordingError } from "../errors.ts";
import { fixtureKey, loadFixtureIndex, sha256Hex, type FixtureEntry } from "./offline.ts";
import { isImage } from "./sniff.ts";
import { isTextBody, type Redactor } from "../redaction.ts";
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

  private readonly redact: Redactor | null;

  constructor(fixturesDir: string, live: Transport, redact: Redactor | null = null) {
    this.dir = fixturesDir;
    this.live = live;
    this.redact = redact;
    for (const entry of loadFixtureIndex(fixturesDir).entries) {
      const r = entry.request;
      this.entries.set(fixtureKey(r.method, r.url, r.headers?.range, r.bodySha256), entry);
    }
  }

  async send(req: RawRequest): Promise<RawResponse> {
    let res = await this.live.send(req);
    const contentType = res.headers.find(([n]) => n === "content-type")?.[1] ?? "";
    if (/^image\//i.test(contentType) || isImage(res.body)) {
      throw new FixtureRecordingError(`refusing to record image data from ${req.url} (Constitution X)`);
    }
    const ext = extension(contentType);
    if (this.redact && res.body.length > 0 && isTextBody(`.${ext}`)) {
      // The live run sees exactly what will be replayed, so live and offline results match.
      const text = new TextDecoder().decode(res.body);
      const redacted = this.redact.body(req.url, text);
      if (redacted !== text) {
        res = {
          ...res,
          headers: res.headers.filter(([n]) => n !== "content-length"),
          body: new TextEncoder().encode(redacted),
        };
      }
    }
    // Account-derived URL parts are replaced in the stored key and in stored redirects alike, so
    // the offline replay follows the same (redacted) chain; the live run keeps the real values.
    const redactUrl = this.redact ? this.redact.url : (u: string) => u;
    const headers: Record<string, string> = {};
    for (const [name, value] of res.headers) {
      if (KEPT_RESPONSE_HEADERS.has(name)) headers[name] = name === "location" ? redactUrl(value) : value;
    }
    let body: string | null = null;
    if (res.body.length > 0) {
      body = `bodies/${sha256Hex(res.body).slice(0, 16)}.${ext}`;
      this.bodies.set(body, res.body);
    }
    const bodySha256 = req.body ? sha256Hex(req.body) : null;
    const range = req.headers.range;
    const url = redactUrl(req.url);
    this.entries.set(fixtureKey(req.method, url, range, bodySha256), {
      request: {
        method: req.method,
        url,
        ...(range ? { headers: { range } } : {}),
        ...(bodySha256 ? { bodySha256 } : {}),
      },
      response: { status: res.status, headers, body },
    });
    return res;
  }

  /**
   * Writes new bodies and the merged, sorted index; sets recordedAt to now; removes bodies the
   * index no longer references (a re-recorded response replaces its old body).
   */
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
    const referenced = new Set(entries.map((e) => ("body" in e.response ? e.response.body : null)));
    for (const name of readdirSync(join(this.dir, "bodies"))) {
      if (!referenced.has(`bodies/${name}`)) rmSync(join(this.dir, "bodies", name));
    }
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
