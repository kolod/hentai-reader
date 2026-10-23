// Contract test: per-source key-value storage: get/set/delete, limits, isolation from other sources.
// Runs inside the host's own engine; run() throws on failure.

function assert(cond, message) {
  if (!cond) throw new Error("assertion failed: " + message);
}

async function expectError(kind, fn) {
  try {
    await fn();
  } catch (e) {
    if (e instanceof SourceError && e.kind === kind) return;
    throw new Error("expected SourceError(" + kind + "), got " + (e && e.name) + ": " + (e && e.message));
  }
  throw new Error("expected SourceError(" + kind + "), got success");
}

export const manifest = {
  "id": "storage-test",
  "name": "storage-test",
  "version": "1.0.0",
  "contract": "1.0",
  "domains": [
    "a.test"
  ],
  "politeness": {
    "minIntervalMs": 0,
    "maxConcurrency": 4
  },
  "login": "none",
  "filters": [],
  "capabilities": {
    "suggestTags": false
  }
};

export async function run() {
  assert((await storage.get("shared")) === null, "storage is isolated between sources");
  assert((await storage.get("missing")) === null, "missing key is null");

  await storage.set("k", "v1");
  assert((await storage.get("k")) === "v1", "set then get");
  await storage.set("k", "v2");
  assert((await storage.get("k")) === "v2", "overwrite");
  await storage.set("k", null);
  assert((await storage.get("k")) === null, "null deletes");

  await storage.set("unicode", "ключ ✓");
  assert((await storage.get("unicode")) === "ключ ✓", "unicode values");

  let long = "";
  for (let i = 0; i < 129; i++) long += "x";
  await expectError("InvalidInput", () => storage.set(long, "v"));
  await expectError("InvalidInput", () => storage.get(""));

  let big = "0123456789abcdef";
  while (big.length <= 1024 * 1024) big += big;
  await expectError("InvalidInput", () => storage.set("big", big));
}
