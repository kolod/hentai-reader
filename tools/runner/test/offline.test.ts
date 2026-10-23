import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const CLI = fileURLToPath(new URL("../src/cli.ts", import.meta.url));
const ROOT = fileURLToPath(new URL("../../..", import.meta.url));

function cli(...args: string[]) {
  const r = spawnSync(process.execPath, [CLI, ...args], { cwd: ROOT, encoding: "utf8" });
  return { code: r.status, stdout: r.stdout, stderr: r.stderr };
}

test("unmatched request in offline mode exits 4 and names the request", () => {
  const r = cli("run", "contract/conformance/minimal", "getGallery", '"2"');
  assert.equal(r.code, 4);
  assert.match(r.stderr, /GET https:\/\/example\.test\/g\/2/);
});

test("offline runs are deterministic", () => {
  const strip = (s: string) => s.replace(/\(\d+ ms\)/g, "");
  const outputs = [1, 2, 3].map(() => strip(cli("test", "contract/conformance/minimal").stdout));
  assert.equal(outputs[0], outputs[1]);
  assert.equal(outputs[1], outputs[2]);
  const gallery = [1, 2].map(() => cli("run", "contract/conformance/minimal", "getGallery", '"1"').stdout);
  assert.equal(gallery[0], gallery[1]);
});

test("a forbidden capability fails the run with its name (US2-3)", () => {
  const r = cli("run", "contract/conformance/broken-forbidden-capability", "search", '{"text":"","filters":{}}', "null");
  assert.equal(r.code, 3);
  assert.match(r.stderr, /forbidden capability: fetch/);
});

test("usage errors exit 2", () => {
  assert.equal(cli("run").code, 2);
  assert.equal(cli("nope").code, 2);
  assert.equal(cli("run", "contract/conformance/minimal", "search", "{not json").code, 2);
});

test("incompatible contract version is refused by every command", () => {
  const r = cli("run", "contract/conformance/broken-contract-version", "search", '{"text":"","filters":{}}', "null");
  assert.equal(r.code, 3);
  assert.match(r.stderr, /requires contract 2\.0, host supports 1\.0/);
});
