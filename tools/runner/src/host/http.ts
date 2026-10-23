// http.request for the reference host: validation, domain allow-list, politeness scheduling,
// cookie jar, redirects, response shaping (contract HostHttp, research R4).
import { CookieJar, domainMatches, mergeCookieHeader } from "./cookies.ts";
import { envelopeError, envelopeValue, type ErrorKind } from "./envelope.ts";
import { FatalTransportError } from "../errors.ts";
import type { RawRequest, RawResponse, Transport } from "../transport/types.ts";

export interface Politeness {
  minIntervalMs: number;
  maxConcurrency: number;
}

export interface HttpManifest {
  domains: string[];
  politeness: Politeness;
}

export interface RequestLogEntry {
  method: string;
  url: string;
  headers: Record<string, string>;
  startedAt: number;
  endedAt?: number;
  status?: number;
  refused?: string;
}

const REDIRECTS = new Set([301, 302, 303, 307, 308]);
const MAX_REDIRECTS = 5;
const DEFAULT_TIMEOUT_MS = 30_000;

class HttpFailure extends Error {
  readonly kind: ErrorKind;
  constructor(kind: ErrorKind, message: string) {
    super(message);
    this.kind = kind;
  }
}

/** Enforces min interval between request starts and max requests in flight. */
export class Scheduler {
  private inFlight = 0;
  private lastStart = -Infinity;
  private waiters: (() => void)[] = [];
  private chain: Promise<void> = Promise.resolve();
  private readonly limits: () => Politeness | null;

  constructor(limits: () => Politeness | null) {
    this.limits = limits;
  }

  acquire(): Promise<void> {
    const next = this.chain.then(async () => {
      const lim = this.limits();
      if (lim) {
        while (this.inFlight >= lim.maxConcurrency) {
          await new Promise<void>((resolve) => this.waiters.push(resolve));
        }
        const wait = this.lastStart + lim.minIntervalMs - Date.now();
        if (wait > 0) await new Promise((resolve) => setTimeout(resolve, wait));
      }
      this.lastStart = Date.now();
      this.inFlight++;
    });
    this.chain = next.catch(() => {});
    return next;
  }

  release(): void {
    this.inFlight--;
    this.waiters.shift()?.();
  }
}

export interface HttpHostOptions {
  transport: Transport;
  /** "skip" disables politeness (offline replay); "enforce" applies the manifest's limits. */
  politeness: "enforce" | "skip";
  cookies?: CookieJar;
  log?: RequestLogEntry[];
  onFatal?: (e: Error) => void;
}

export class HttpHost {
  readonly cookies: CookieJar;
  readonly log: RequestLogEntry[];
  private manifest: HttpManifest | null = null;
  private readonly scheduler: Scheduler;
  private readonly opts: HttpHostOptions;

  constructor(opts: HttpHostOptions) {
    this.opts = opts;
    this.cookies = opts.cookies ?? new CookieJar();
    this.log = opts.log ?? [];
    this.scheduler = new Scheduler(() =>
      opts.politeness === "enforce" && this.manifest ? this.manifest.politeness : null,
    );
  }

  setManifest(manifest: HttpManifest): void {
    this.manifest = manifest;
  }

  isAllowed(url: string): boolean {
    if (!this.manifest) return false;
    const host = new URL(url).hostname.toLowerCase();
    return this.manifest.domains.some((d) => {
      const pattern = d.toLowerCase();
      return pattern.startsWith("*.") ? domainMatches(host, pattern.slice(2)) && host !== pattern.slice(2) : host === pattern;
    });
  }

  /** Bridge entry point: JSON request metadata (+ optional byte body) → envelope (+ bytes). */
  async request(metaJson: string, bodyBytes: Uint8Array | undefined): Promise<{ env: string; bytes?: Uint8Array }> {
    try {
      const req = this.validate(JSON.parse(metaJson), bodyBytes);
      const res = await this.perform(req.raw);
      const headers: Record<string, string> = {};
      for (const [name, value] of res.response.headers) {
        if (name === "set-cookie") continue;
        headers[name] = headers[name] === undefined ? value : `${headers[name]}, ${value}`;
      }
      const base = { status: res.response.status, url: res.url, headers };
      if (req.responseType === "bytes") {
        return { env: envelopeValue({ ...base, bytes: true }), bytes: res.response.body };
      }
      return { env: envelopeValue({ ...base, text: decode(res.response.body, headers["content-type"]) }) };
    } catch (e) {
      if (e instanceof HttpFailure) return { env: envelopeError(e.kind, e.message) };
      if (e instanceof FatalTransportError) {
        this.opts.onFatal?.(e);
        return { env: envelopeError("Network", e.message) };
      }
      return { env: envelopeError("Network", e instanceof Error ? e.message : String(e)) };
    }
  }

  private validate(meta: Record<string, unknown>, bodyBytes: Uint8Array | undefined) {
    const method = meta.method === undefined ? "GET" : meta.method;
    if (method !== "GET" && method !== "POST") {
      throw new HttpFailure("InvalidInput", `http.request: unsupported method ${String(method)}`);
    }
    if (typeof meta.url !== "string" || !/^https?:\/\//i.test(meta.url)) {
      throw new HttpFailure("InvalidInput", `http.request: absolute http(s) URL required, got ${String(meta.url)}`);
    }
    try {
      new URL(meta.url);
    } catch {
      throw new HttpFailure("InvalidInput", `http.request: invalid URL ${meta.url}`);
    }
    const headers: Record<string, string> = {};
    if (meta.headers !== undefined && meta.headers !== null) {
      if (typeof meta.headers !== "object") throw new HttpFailure("InvalidInput", "http.request: headers must be an object");
      for (const [k, v] of Object.entries(meta.headers as Record<string, unknown>)) {
        if (typeof v !== "string") throw new HttpFailure("InvalidInput", `http.request: header ${k} must be a string`);
        headers[k.toLowerCase()] = v;
      }
    }
    let body: Uint8Array | undefined = bodyBytes;
    if (typeof meta.body === "string") body = new TextEncoder().encode(meta.body);
    else if (meta.body !== undefined && meta.body !== null) {
      throw new HttpFailure("InvalidInput", "http.request: body must be a string or Uint8Array");
    }
    if (body && method !== "POST") throw new HttpFailure("InvalidInput", "http.request: body is only allowed with POST");
    const responseType = meta.responseType === undefined ? "text" : meta.responseType;
    if (responseType !== "text" && responseType !== "bytes") {
      throw new HttpFailure("InvalidInput", `http.request: unsupported responseType ${String(responseType)}`);
    }
    const timeoutMs = typeof meta.timeoutMs === "number" ? Math.min(Math.max(meta.timeoutMs, 1), 120_000) : DEFAULT_TIMEOUT_MS;
    const raw: RawRequest = { method, url: meta.url, headers, body, timeoutMs };
    return { raw, responseType };
  }

  private async perform(first: RawRequest): Promise<{ url: string; response: RawResponse }> {
    let req = first;
    for (let hop = 0; ; hop++) {
      if (!this.isAllowed(req.url)) {
        const host = new URL(req.url).hostname;
        this.log.push({ method: req.method, url: req.url, headers: req.headers, startedAt: Date.now(), refused: "domain" });
        throw new HttpFailure("InvalidInput", `http.request: domain not declared in manifest: ${host}`);
      }
      const sent: RawRequest = { ...req, headers: { ...req.headers } };
      const cookie = mergeCookieHeader(this.cookies.for(req.url), req.headers.cookie);
      if (cookie) sent.headers.cookie = cookie;
      else delete sent.headers.cookie;

      await this.scheduler.acquire();
      const entry: RequestLogEntry = { method: sent.method, url: sent.url, headers: sent.headers, startedAt: Date.now() };
      this.log.push(entry);
      let response: RawResponse;
      try {
        response = await this.opts.transport.send(sent);
      } finally {
        entry.endedAt = Date.now();
        this.scheduler.release();
      }
      entry.status = response.status;
      this.cookies.store(
        req.url,
        response.headers.filter(([n]) => n === "set-cookie").map(([, v]) => v),
      );

      const location = response.headers.find(([n]) => n === "location")?.[1];
      if (!REDIRECTS.has(response.status) || !location) return { url: req.url, response };
      if (hop >= MAX_REDIRECTS) throw new HttpFailure("Network", `http.request: more than ${MAX_REDIRECTS} redirects`);
      const toGet = response.status === 303 || ((response.status === 301 || response.status === 302) && req.method === "POST");
      req = {
        ...req,
        url: new URL(location, req.url).toString(),
        method: toGet ? "GET" : req.method,
        body: toGet ? undefined : req.body,
      };
    }
  }
}

function decode(body: Uint8Array, contentType: string | undefined): string {
  const charset = /charset=([^;]+)/i.exec(contentType ?? "")?.[1]?.trim().replace(/^"|"$/g, "");
  try {
    return new TextDecoder(charset || "utf-8").decode(body);
  } catch {
    return new TextDecoder("utf-8").decode(body);
  }
}
