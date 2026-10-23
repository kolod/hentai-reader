import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, readdirSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { FixtureRecordingError } from "../src/errors.ts";
import { OfflineTransport } from "../src/transport/offline.ts";
import { RecordingTransport } from "../src/transport/recorder.ts";
import { FakeTransport, text } from "./helpers.ts";

const req = (url: string, headers: Record<string, string> = {}) => ({ method: "GET" as const, url, headers, timeoutMs: 1000 });

function setup(handler: ConstructorParameters<typeof FakeTransport>[0]) {
  const dir = mkdtempSync(join(tmpdir(), "fixtures-"));
  return { dir, recorder: new RecordingTransport(dir, new FakeTransport(handler)) };
}

test("cookies, set-cookie and authorization are never written", async () => {
  const { dir, recorder } = setup(() =>
    text(200, "<p>hi</p>", [
      ["set-cookie", "sid=secret"],
      ["x-internal", "drop me"],
      ["etag", '"v1"'],
    ]),
  );
  await recorder.send(req("https://a.test/", { cookie: "sid=secret", authorization: "Bearer t", range: "bytes=0-9" }));
  recorder.flush();
  const index = readFileSync(join(dir, "index.json"), "utf8");
  assert.doesNotMatch(index, /secret|Bearer|set-cookie|authorization|"cookie"|x-internal/i);
  const entry = JSON.parse(index).entries[0];
  assert.deepEqual(entry.request.headers, { range: "bytes=0-9" });
  assert.deepEqual(Object.keys(entry.response.headers).sort(), ["content-type", "etag"]);
});

test("image bodies are refused loudly", async () => {
  const webp = new Uint8Array([0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50]);
  const { recorder } = setup(() => ({ status: 200, headers: [["content-type", "application/octet-stream"]], body: webp }));
  await assert.rejects(recorder.send(req("https://a.test/img")), FixtureRecordingError);
  const typed = setup(() => ({ status: 200, headers: [["content-type", "image/png"]], body: new TextEncoder().encode("x") }));
  await assert.rejects(typed.recorder.send(req("https://a.test/png")), FixtureRecordingError);
});

test("identical bodies are stored once", async () => {
  const { dir, recorder } = setup(() => text(200, "same"));
  await recorder.send(req("https://a.test/1"));
  await recorder.send(req("https://a.test/2"));
  recorder.flush();
  assert.equal(readdirSync(join(dir, "bodies")).length, 1);
  assert.equal(JSON.parse(readFileSync(join(dir, "index.json"), "utf8")).entries.length, 2);
});

test("recorded responses replay offline unchanged", async () => {
  const bin = new Uint8Array([0, 1, 2, 255]);
  const { dir, recorder } = setup((r) =>
    r.url.endsWith("bin") ? { status: 206, headers: [["content-range", "bytes 0-3/9"]], body: bin } : text(404, "gone"),
  );
  const live = [await recorder.send(req("https://a.test/bin", { range: "bytes=0-3" })), await recorder.send(req("https://a.test/x"))];
  recorder.flush();
  const offline = new OfflineTransport(dir);
  const replay = [await offline.send(req("https://a.test/bin", { range: "bytes=0-3" })), await offline.send(req("https://a.test/x"))];
  for (let i = 0; i < 2; i++) {
    assert.equal(replay[i].status, live[i].status);
    assert.deepEqual([...replay[i].body], [...live[i].body]);
  }
  assert.deepEqual(replay[0].headers, [["content-range", "bytes 0-3/9"]]);
});
