import { test } from "node:test";
import assert from "node:assert/strict";
import { engineFor, FakeTransport, MANIFEST, sleep, text } from "./helpers.ts";

const get = (url: string) => `export async function f() { const r = await http.request({ url: "${url}" }); return r.status; }`;

test("domain allow-list: exact, wildcard subdomains, refusal", async () => {
  for (const [url, ok] of [
    ["https://a.test/x", true],
    ["https://img.b.test/x", true],
    ["https://b.test/x", false],
    ["https://sub.a.test/x", false],
    ["https://evil.test/x", false],
  ] as const) {
    const { engine } = await engineFor(get(url));
    const res = await engine.call("f", []);
    if (ok) assert.deepEqual(res, { ok: true, value: 200 }, url);
    else assert.equal(!res.ok && res.error.type === "SourceError" && res.error.kind, "InvalidInput", url);
  }
});

test("politeness: start gap >= minIntervalMs and in-flight <= maxConcurrency", async () => {
  let inFlight = 0;
  let maxSeen = 0;
  const transport = new FakeTransport(async () => {
    inFlight++;
    maxSeen = Math.max(maxSeen, inFlight);
    await sleep(40);
    inFlight--;
    return text(200, "ok");
  });
  const manifest = { ...MANIFEST, politeness: { minIntervalMs: 25, maxConcurrency: 2 } };
  const { engine, host } = await engineFor(
    `export async function f() { await Promise.all([1,2,3,4,5].map((i) => http.request({ url: "https://a.test/" + i }))); }`,
    { transport, manifest, politeness: "enforce" },
  );
  await engine.call("f", []);
  assert.ok(maxSeen <= 2, `max in flight ${maxSeen}`);
  const starts = host.requestLog.map((r) => r.startedAt);
  for (let i = 1; i < starts.length; i++) assert.ok(starts[i] - starts[i - 1] >= 24, `gap ${starts[i] - starts[i - 1]}`);
});

test("cookies: scoped to their domain, merged with script cookie header, hidden from scripts", async () => {
  const transport = new FakeTransport((req) =>
    req.url.endsWith("/login") ? text(200, "ok", [["set-cookie", "sid=abc; Path=/"]]) : text(200, "ok"),
  );
  const { engine, host } = await engineFor(
    `export async function f() {
       const r = await http.request({ url: "https://a.test/login" });
       await http.request({ url: "https://a.test/next", headers: { Cookie: "nw=1; sid=override" } });
       await http.request({ url: "https://img.b.test/x" });
       return Object.keys(r.headers);
     }`,
    { transport },
  );
  host.httpHost.cookies.seed("img.b.test", "seeded=1");
  const res = await engine.call("f", []);
  assert.deepEqual(res, { ok: true, value: ["content-type"] });
  assert.equal(transport.sent[0].headers.cookie, undefined);
  assert.equal(transport.sent[1].headers.cookie, "sid=override; nw=1");
  assert.equal(transport.sent[2].headers.cookie, "seeded=1");
});

test("redirects are followed (max 5) and re-checked against domains", async () => {
  const transport = new FakeTransport((req) => {
    const n = Number(new URL(req.url).pathname.slice(1));
    if (req.url.includes("away")) return text(200, "never");
    if (n === 2) return text(302, "", [["location", "https://evil.test/away"]]);
    return n < 9 ? text(302, "", [["location", `/${n + 1}`]]) : text(200, "end");
  });
  const { engine } = await engineFor(
    `export async function ok() { return (await http.request({ url: "https://a.test/7" })).url; }
     export async function far() { return (await http.request({ url: "https://a.test/3" })).status; }
     export async function evil() { return (await http.request({ url: "https://a.test/1" })).status; }`,
    { transport },
  );
  assert.deepEqual(await engine.call("ok", []), { ok: true, value: "https://a.test/9" });
  const far = await engine.call("far", []);
  assert.equal(!far.ok && far.error.type === "SourceError" && far.error.kind, "Network");
  const evil = await engine.call("evil", []);
  assert.equal(!evil.ok && evil.error.type === "SourceError" && evil.error.kind, "InvalidInput");
});

test("non-2xx statuses are returned, not thrown; bytes and POST bodies cross the boundary", async () => {
  const transport = new FakeTransport((req) =>
    req.method === "POST"
      ? { status: 201, headers: [], body: req.body ?? new Uint8Array() }
      : text(404, "missing"),
  );
  const { engine } = await engineFor(
    `export async function f() {
       const a = await http.request({ url: "https://a.test/x" });
       const b = await http.request({ method: "POST", url: "https://a.test/p", body: new Uint8Array([7, 8, 9]), responseType: "bytes" });
       return [a.status, a.body, b.status, Array.from(b.body)];
     }
     export async function g() { return http.request({ url: "https://a.test/x", body: "no" }); }`,
    { transport },
  );
  assert.deepEqual(await engine.call("f", []), { ok: true, value: [404, "missing", 201, [7, 8, 9]] });
  const g = await engine.call("g", []);
  assert.equal(!g.ok && g.error.type === "SourceError" && g.error.kind, "InvalidInput");
});
