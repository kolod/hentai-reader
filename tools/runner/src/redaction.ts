// Optional per-source fixture redaction: `sources/<id>/redact.mjs` exports
// `redact(url, text) → text`, which replaces real site content with neutral placeholders before it
// is stored, and optionally `redactUrl(url) → url` for URLs that carry account-derived values
// (applied to stored request URLs and `location` headers alike, so redirects still replay).
// Both must be idempotent; `check` re-applies them to prove every stored body and URL is redacted.
import { existsSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { loadFixtureIndex, sha256Hex } from "./transport/offline.ts";

export interface Redactor {
  body: (url: string, text: string) => string;
  url: (url: string) => string;
}

const TEXT_BODY = /\.(html|json|js|txt)$/;

export async function loadRedactor(sourceDir: string): Promise<Redactor | null> {
  const file = join(sourceDir, "redact.mjs");
  if (!existsSync(file)) return null;
  const mod = (await import(pathToFileURL(file).href)) as { redact?: Redactor["body"]; redactUrl?: Redactor["url"] };
  if (typeof mod.redact !== "function") throw new Error(`${file} must export function redact(url, text)`);
  return { body: mod.redact, url: typeof mod.redactUrl === "function" ? mod.redactUrl : (u) => u };
}

export function isTextBody(path: string): boolean {
  return TEXT_BODY.test(path);
}

/** Bodies and URLs that would change when redacted again (empty = fully redacted). */
export function unredacted(fixturesDir: string, redact: Redactor): string[] {
  const out: string[] = [];
  for (const entry of loadFixtureIndex(fixturesDir).entries) {
    const url = entry.request.url;
    if (redact.url(url) !== url) out.push(`request URL ${url}`);
    const location = "headers" in entry.response ? entry.response.headers?.location : undefined;
    if (location && redact.url(location) !== location) out.push(`location header ${location}`);
    const body = "body" in entry.response ? entry.response.body : null;
    if (!body || !isTextBody(body) || !existsSync(join(fixturesDir, body))) continue;
    const text = readFileSync(join(fixturesDir, body), "utf8");
    if (redact.body(url, text) !== text) out.push(`${body} (${url})`);
  }
  return out;
}

/** Redacts an existing fixture pool in place; drops bodies no entry references any more. */
export function redactPool(fixturesDir: string, redact: Redactor): number {
  const index = loadFixtureIndex(fixturesDir);
  let changed = 0;
  for (const entry of index.entries) {
    const res = entry.response;
    const url = redact.url(entry.request.url);
    if (url !== entry.request.url) {
      entry.request.url = url;
      changed++;
    }
    if ("headers" in res && res.headers?.location) {
      const location = redact.url(res.headers.location);
      if (location !== res.headers.location) {
        res.headers.location = location;
        changed++;
      }
    }
    if (!("body" in res) || !res.body || !isTextBody(res.body)) continue;
    const text = readFileSync(join(fixturesDir, res.body), "utf8");
    const redacted = redact.body(entry.request.url, text);
    if (redacted === text) continue;
    const bytes = Buffer.from(redacted, "utf8");
    const ext = res.body.slice(res.body.lastIndexOf(".") + 1);
    res.body = `bodies/${sha256Hex(bytes).slice(0, 16)}.${ext}`;
    writeFileSync(join(fixturesDir, res.body), bytes);
    if (res.headers) delete res.headers["content-length"];
    changed++;
  }
  writeFileSync(join(fixturesDir, "index.json"), `${JSON.stringify(index, null, 2)}\n`);
  const referenced = new Set(index.entries.map((e) => ("body" in e.response ? e.response.body : null)));
  const bodies = join(fixturesDir, "bodies");
  if (existsSync(bodies)) {
    for (const name of readdirSync(bodies)) {
      if (!referenced.has(`bodies/${name}`)) rmSync(join(bodies, name));
    }
  }
  return changed;
}
