import { test } from "node:test";
import assert from "node:assert/strict";
import { engineFor, FakeTransport, sleep, text } from "./helpers.ts";

test("reads module exports", async () => {
  const { engine } = await engineFor("export function a() {} export const b = 1;");
  const d = await engine.describe();
  assert.deepEqual(d.functions, ["a"]);
  assert.equal((d.manifest as { id: string }).id, "t");
});

test("three concurrent host promises resolve in any order", async () => {
  const delays: Record<string, number> = { "https://a.test/1": 30, "https://a.test/2": 5, "https://a.test/3": 15 };
  const transport = new FakeTransport(async (req) => {
    await sleep(delays[req.url]);
    return text(200, req.url.slice(-1));
  });
  const { engine } = await engineFor(
    `export async function f() {
       const r = await Promise.all([1, 2, 3].map((i) => http.request({ url: "https://a.test/" + i })));
       return r.map((x) => x.body).join("");
     }`,
    { transport },
  );
  assert.deepEqual(await engine.call("f", []), { ok: true, value: "123" });
});

test("frozen clock is visible through Date.now() and new Date()", async () => {
  const { engine } = await engineFor(`export function f() { return [Date.now(), new Date().toISOString(), new Date(0).getTime()]; }`, {
    frozenNow: Date.parse("2026-01-02T03:04:05Z"),
  });
  assert.deepEqual(await engine.call("f", []), {
    ok: true,
    value: [Date.parse("2026-01-02T03:04:05Z"), "2026-01-02T03:04:05.000Z", 0],
  });
});

test("using a capability outside the contract is reported by name", async () => {
  for (const name of ["fetch", "setTimeout", "URL", "TextEncoder", "require", "process"]) {
    const { engine } = await engineFor(`export function f() { return ${name}; }`);
    const res = await engine.call("f", []);
    assert.equal(res.ok, false);
    if (!res.ok) assert.equal(res.error.message, `forbidden capability: ${name}`);
  }
});

test("the private host bridge is not reachable from the source", async () => {
  const { engine } = await engineFor(`export function f() { return typeof globalThis.__hostBridge; }`);
  assert.deepEqual(await engine.call("f", []), { ok: true, value: "undefined" });
});

test("time limit interrupts a runaway loop", async () => {
  const { engine } = await engineFor(`export function f() { for (;;) {} }`, { callTimeoutMs: 100 });
  const res = await engine.call("f", []);
  assert.equal(res.ok, false);
  if (!res.ok) assert.deepEqual(res.error.details, { limit: "time" });
});

test("SourceError keeps kind, message and options; other errors become ScriptError", async () => {
  const { engine } = await engineFor(
    `export function a() { throw new SourceError("RateLimited", "slow down", { retryAfterSeconds: 30 }); }
     export function b() { throw new TypeError("bad"); }
     export function c() { return new SourceError("Nope", "x"); }
     export function d() { return new SourceError("NotFound", "x") instanceof Error; }`,
  );
  const a = await engine.call("a", []);
  assert.deepEqual(a.ok ? null : a.error, {
    type: "SourceError",
    kind: "RateLimited",
    message: "slow down",
    retryAfterSeconds: 30,
    details: undefined,
  });
  const b = await engine.call("b", []);
  assert.equal(!b.ok && b.error.type, "ScriptError");
  const c = await engine.call("c", []);
  assert.equal(!c.ok && c.error.message, "SourceError: unknown kind 'Nope'");
  assert.deepEqual(await engine.call("d", []), { ok: true, value: true });
});
