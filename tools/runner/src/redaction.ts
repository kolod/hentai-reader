// Optional per-source fixture redaction: `sources/<id>/redact.mjs` exports
// `redact(url, text) → text`, which replaces real site content with neutral placeholders before it
// is stored. It must be idempotent; `check` re-applies it to prove every stored body is redacted.
import { existsSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { loadFixtureIndex, sha256Hex } from "./transport/offline.ts";

export type Redactor = (url: string, text: string) => string;

const TEXT_BODY = /\.(html|json|js|txt)$/;

export async function loadRedactor(sourceDir: string): Promise<Redactor | null> {
  const file = join(sourceDir, "redact.mjs");
  if (!existsSync(file)) return null;
  const mod = (await import(pathToFileURL(file).href)) as { redact?: Redactor };
  if (typeof mod.redact !== "function") throw new Error(`${file} must export function redact(url, text)`);
  return mod.redact;
}

export function isTextBody(path: string): boolean {
  return TEXT_BODY.test(path);
}

/** Bodies whose stored text changes when redacted again (empty = fully redacted). */
export function unredacted(fixturesDir: string, redact: Redactor): string[] {
  const out: string[] = [];
  for (const entry of loadFixtureIndex(fixturesDir).entries) {
    const body = "body" in entry.response ? entry.response.body : null;
    if (!body || !isTextBody(body) || !existsSync(join(fixturesDir, body))) continue;
    const text = readFileSync(join(fixturesDir, body), "utf8");
    if (redact(entry.request.url, text) !== text) out.push(`${body} (${entry.request.url})`);
  }
  return out;
}

/** Redacts an existing fixture pool in place; drops bodies no entry references any more. */
export function redactPool(fixturesDir: string, redact: Redactor): number {
  const index = loadFixtureIndex(fixturesDir);
  let changed = 0;
  for (const entry of index.entries) {
    const res = entry.response;
    if (!("body" in res) || !res.body || !isTextBody(res.body)) continue;
    const text = readFileSync(join(fixturesDir, res.body), "utf8");
    const redacted = redact(entry.request.url, text);
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
  for (const name of readdirSync(join(fixturesDir, "bodies"))) {
    if (!referenced.has(`bodies/${name}`)) rmSync(join(fixturesDir, "bodies", name));
  }
  return changed;
}
