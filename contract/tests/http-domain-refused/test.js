// Contract test: requests to hosts not matched by manifest.domains are refused before reaching the network.
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
  "id": "http-domain-refused",
  "name": "http-domain-refused",
  "version": "1.0.0",
  "contract": "1.0",
  "domains": [
    "a.test",
    "*.img.test"
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
  await expectError("InvalidInput", () => http.request({ url: "https://not-declared.test/" }));
  await expectError("InvalidInput", () => http.request({ url: "https://a.test.evil.test/" }));
  await expectError("InvalidInput", () => http.request({ url: "https://img.test/" }));
}
