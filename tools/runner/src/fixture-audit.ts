// Fixture hygiene (Constitution X, SC-007): size limit, no image bodies, no credentials, body
// references intact, and — when the source has a redactor — no unredacted site content.
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { isImage } from "./transport/sniff.ts";
import { unredacted, type Redactor } from "./redaction.ts";

export const FIXTURE_LIMIT_BYTES = 5 * 1024 * 1024;
const FORBIDDEN_KEYS = /"(cookie|set-cookie|authorization)"\s*:/i;

export function auditFixtures(fixturesDir: string, redact: Redactor | null = null): string[] {
  if (!existsSync(fixturesDir)) return [];
  const out: string[] = [];
  let total = 0;
  const walk = (dir: string) => {
    for (const name of readdirSync(dir)) {
      const p = join(dir, name);
      const st = statSync(p);
      if (st.isDirectory()) {
        walk(p);
        continue;
      }
      total += st.size;
      if (isImage(readFileSync(p))) out.push(`fixtures: image data is not allowed: ${p}`);
    }
  };
  walk(fixturesDir);
  if (total > FIXTURE_LIMIT_BYTES) out.push(`fixtures: total size ${total} bytes exceeds ${FIXTURE_LIMIT_BYTES}`);
  const index = join(fixturesDir, "index.json");
  if (!existsSync(index)) return out;
  const text = readFileSync(index, "utf8");
  if (FORBIDDEN_KEYS.test(text)) out.push("fixtures: index.json contains a cookie, set-cookie or authorization header");

  // Bodies are shared by content hash: compute references instead of guessing.
  const referenced = new Set<string>();
  for (const entry of (JSON.parse(text) as { entries: { response: { body?: string | null } }[] }).entries) {
    const body = entry.response.body;
    if (!body) continue;
    referenced.add(body);
    if (!existsSync(join(fixturesDir, body))) out.push(`fixtures: referenced body is missing: ${body}`);
  }
  const bodiesDir = join(fixturesDir, "bodies");
  if (existsSync(bodiesDir)) {
    for (const name of readdirSync(bodiesDir)) {
      if (!referenced.has(`bodies/${name}`)) out.push(`fixtures: unreferenced body: bodies/${name}`);
    }
  }
  if (redact) {
    for (const body of unredacted(fixturesDir, redact)) {
      out.push(`fixtures: not redacted: ${body} — run \`source-runner redact\``);
    }
  }
  return out;
}
