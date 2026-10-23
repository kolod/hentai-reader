// Contract test: SourceError from the shared prelude: construction, fields, instanceof, kind validation.
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
  "id": "source-error",
  "name": "source-error",
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
  const e = new SourceError("RateLimited", "slow down", { retryAfterSeconds: 5, details: { limit: "images" } });
  assert(e instanceof SourceError && e instanceof Error, "instanceof");
  assert(e.name === "SourceError" && e.kind === "RateLimited" && e.message === "slow down", "fields");
  assert(e.retryAfterSeconds === 5 && e.details.limit === "images", "options");

  for (const kind of ["RateLimited", "LoginRequired", "NotFound", "SiteChanged", "InvalidInput", "Network"]) {
    assert(new SourceError(kind, "x").kind === kind, "kind " + kind);
  }
  let threw = null;
  try {
    new SourceError("Whatever", "x");
  } catch (err) {
    threw = err;
  }
  assert(threw instanceof TypeError, "unknown kind is a TypeError");
}
