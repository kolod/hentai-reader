import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { hostInDomains, loginPageViolations } from "../src/login-page.ts";

const CLI = fileURLToPath(new URL("../src/cli.ts", import.meta.url));
const ROOT = fileURLToPath(new URL("../../..", import.meta.url));

const base = {
  contract: "1.2",
  login: "optional",
  domains: ["e-hentai.org", "*.e-hentai.org", "exhentai.org"],
};

test("a valid loginPage has no violations", () => {
  assert.deepEqual(
    loginPageViolations({ ...base, loginPage: { url: "https://e-hentai.org/bounce_login.php", cookies: ["a", "b"], shareWith: ["exhentai.org"] } }),
    [],
  );
  assert.deepEqual(loginPageViolations({ ...base }), []);
});

test("every rule of the contract table is reported", () => {
  const v = (loginPage: unknown, extra: Record<string, unknown> = {}) => loginPageViolations({ ...base, ...extra, loginPage });
  assert.match(v({ url: "http://e-hentai.org/x", cookies: ["a"] })[0], /absolute https URL/);
  assert.match(v({ url: "not a url", cookies: ["a"] })[0], /absolute https URL/);
  assert.match(v({ url: "https://evil.test/x", cookies: ["a"] })[0], /host evil.test is outside manifest.domains/);
  assert.match(v({ url: "https://e-hentai.org/x", cookies: [] })[0], /at least one cookie name/);
  assert.match(v({ url: "https://e-hentai.org/x", cookies: ["a", "a"] })[0], /each once/);
  assert.match(v({ url: "https://e-hentai.org/x", cookies: ["a"], shareWith: ["other.org"] })[0], /other.org is outside/);
  assert.match(v({ url: "https://e-hentai.org/x", cookies: ["a"] }, { login: "none" })[0], /not allowed when login is "none"/);
  assert.match(v({ url: "https://e-hentai.org/x", cookies: ["a"] }, { contract: "1.1" })[0], /requires contract 1.2/);
});

test("domain patterns follow the http allow-list", () => {
  assert.ok(hostInDomains("forums.e-hentai.org", ["*.e-hentai.org"]));
  assert.ok(!hostInDomains("e-hentai.org", ["*.e-hentai.org"]));
  assert.ok(hostInDomains("E-Hentai.org", ["e-hentai.org"]));
  assert.ok(!hostInDomains("xe-hentai.org", ["e-hentai.org", "*.e-hentai.org"]));
});

test("check reports the conformance sample and run refuses to load it", () => {
  const check = spawnSync(process.execPath, [CLI, "check", "contract/conformance/broken-login-page"], { cwd: ROOT, encoding: "utf8" });
  assert.equal(check.status, 1);
  assert.match(check.stdout, /manifest\.loginPage\.url: host elsewhere\.test is outside manifest\.domains/);
  const run = spawnSync(process.execPath, [CLI, "run", "contract/conformance/broken-login-page", "search", '{"text":"","filters":{}}', "null"], { cwd: ROOT, encoding: "utf8" });
  assert.equal(run.status, 3);
  assert.match(run.stderr, /outside manifest\.domains/);
});
