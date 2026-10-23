// Assembles the reference host: implements the engine's HostBridge from the http, html, storage,
// sleep and console pieces, and wires manifest-dependent settings once the source is loaded.
import type { HostBridge } from "../engine.ts";
import { envelopeError, envelopeValue } from "./envelope.ts";
import { HtmlHost } from "./html.ts";
import { HttpHost, type HttpManifest, type RequestLogEntry } from "./http.ts";
import { StorageHost } from "./storage.ts";
import type { CookieJar } from "./cookies.ts";
import type { Transport } from "../transport/types.ts";

const MAX_SLEEP_MS = 60_000;

export interface LogLine {
  level: string;
  text: string;
}

export interface HostOptions {
  transport: Transport;
  politeness: "enforce" | "skip";
  storage?: StorageHost;
  /** Storage per source id, shared across engines (contract test of storage isolation). */
  storageRegistry?: Map<string, StorageHost>;
  cookies?: CookieJar;
  onLog?: (line: LogLine) => void;
}

export class ReferenceHost implements HostBridge {
  readonly httpHost: HttpHost;
  readonly htmlHost = new HtmlHost();
  storageHost: StorageHost;
  readonly consoleLines: LogLine[] = [];
  private fatalError: Error | null = null;
  private readonly opts: HostOptions;

  constructor(opts: HostOptions) {
    this.opts = opts;
    this.storageHost = opts.storage ?? new StorageHost();
    this.httpHost = new HttpHost({
      transport: opts.transport,
      politeness: opts.politeness,
      cookies: opts.cookies,
      onFatal: (e) => {
        this.fatalError ??= e;
      },
    });
  }

  get requestLog(): RequestLogEntry[] {
    return this.httpHost.log;
  }

  setManifest(manifest: HttpManifest & { id?: string }): void {
    this.httpHost.setManifest(manifest);
    const registry = this.opts.storageRegistry;
    if (registry && manifest.id) {
      let storage = registry.get(manifest.id);
      if (!storage) {
        storage = new StorageHost();
        registry.set(manifest.id, storage);
      }
      this.storageHost = storage;
    }
  }

  // --- HostBridge ---

  http(meta: string, body: Uint8Array | undefined): Promise<{ env: string; bytes?: Uint8Array }> {
    return this.httpHost.request(meta, body);
  }

  htmlSelect(markup: string, elementId: number, selector: string): string {
    return this.htmlHost.select(markup, elementId, selector);
  }

  htmlCall(op: string, elementId: number, arg: string): string {
    return this.htmlHost.call(op, elementId, arg);
  }

  storageGet(key: string): Promise<string> {
    return this.storageHost.get(key);
  }

  storageSet(key: string, valueJson: string): Promise<string> {
    return this.storageHost.set(key, valueJson);
  }

  async sleep(ms: number): Promise<string> {
    if (!Number.isFinite(ms) || ms < 0 || ms > MAX_SLEEP_MS) {
      return envelopeError("InvalidInput", `sleep: ms must be between 0 and ${MAX_SLEEP_MS}`);
    }
    if (this.opts.politeness === "enforce") await new Promise((resolve) => setTimeout(resolve, ms));
    return envelopeValue(null);
  }

  log(level: string, text: string): void {
    const line = { level, text };
    this.consoleLines.push(line);
    this.opts.onLog?.(line);
  }

  idle(): void {
    this.htmlHost.releaseHandles();
  }

  fatal(): Error | null {
    return this.fatalError;
  }
}
