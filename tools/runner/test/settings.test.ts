import { test } from "node:test";
import assert from "node:assert/strict";
import { UsageError } from "../src/errors.ts";
import { callArgs, resolveSettings, takesSettings } from "../src/source.ts";
import { engineFor, MANIFEST } from "./helpers.ts";

const V11 = {
  ...MANIFEST,
  contract: "1.1",
  settings: [
    { id: "image_quality", values: ["resampled", "original"], default: "resampled" },
    { id: "site", values: ["e-hentai", "exhentai"], default: "e-hentai" },
  ],
  capabilities: { suggestTags: false, accountStatus: true },
};

const PROBE = `export function getPageUrl() { return { n: arguments.length, args: Array.from(arguments) }; }
export function getGallery() { return { n: arguments.length, args: Array.from(arguments) }; }`;

test("a 1.0 source is called with exactly the 1.0 arity (no settings, absent retry omitted)", async () => {
  const { engine } = await engineFor(PROBE);
  const settings = resolveSettings(MANIFEST);
  for (const [fn, args, n] of [
    ["getPageUrl", [{ id: "g" }, 0], 2],
    ["getPageUrl", [{ id: "g" }, 0, null], 2],
    ["getPageUrl", [{ id: "g" }, 0, { attempt: 1 }], 3],
    ["getGallery", ["g"], 1],
  ] as const) {
    const res = await engine.call(fn, callArgs(MANIFEST, fn, [...args], settings));
    assert.equal(res.ok && (res.value as { n: number }).n, n, `${fn} ${JSON.stringify(args)}`);
  }
});

test("a 1.1 source receives null for an absent retry and the merged settings last", async () => {
  const { engine } = await engineFor(PROBE, { manifest: V11 });
  const settings = resolveSettings(V11, { site: "exhentai" });
  const res = await engine.call("getPageUrl", callArgs(V11, "getPageUrl", [{ id: "g" }, 3], settings));
  assert.deepEqual(res.ok && res.value, {
    n: 4,
    args: [{ id: "g" }, 3, null, { image_quality: "resampled", site: "exhentai" }],
  });
  const gallery = await engine.call("getGallery", callArgs(V11, "getGallery", ["g"], settings));
  assert.deepEqual(gallery.ok && (gallery.value as { args: unknown[] }).args, ["g", settings]);
});

test("settings resolution: defaults, overrides, unknown ids and values", () => {
  assert.deepEqual(resolveSettings(V11), { image_quality: "resampled", site: "e-hentai" });
  assert.deepEqual(resolveSettings(V11, { image_quality: "original" }), { image_quality: "original", site: "e-hentai" });
  assert.throws(() => resolveSettings(V11, { theme: "dark" }), UsageError);
  assert.throws(() => resolveSettings(V11, { site: "nhentai" }), UsageError);
  assert.deepEqual(resolveSettings(MANIFEST), {});
  assert.throws(() => resolveSettings(MANIFEST, { site: "exhentai" }), UsageError);
});

test("contract level detection and account-status gating", () => {
  assert.equal(takesSettings(MANIFEST), false);
  assert.equal(takesSettings(V11), true);
  assert.equal(takesSettings({ ...MANIFEST, contract: "2.0" }), true);
  assert.throws(() => callArgs(MANIFEST, "getAccountStatus", [], {}), UsageError);
  assert.deepEqual(callArgs(V11, "getAccountStatus", [], { site: "e-hentai" }), [{ site: "e-hentai" }]);
});
