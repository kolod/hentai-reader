// Contract test: Range requests reach the server and partial responses expose content-range.
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
  "id": "http-range",
  "name": "http-range",
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
  const first = await http.request({ url: "https://a.test/blob", headers: { Range: "bytes=0-3" }, responseType: "bytes" });
  assert(first.status === 206, "206 Partial Content");
  assert(first.headers["content-range"] === "bytes 0-3/8", "content-range exposed");
  assert(first.body.length === 4 && first.body[0] === 10, "first range");

  const second = await http.request({ url: "https://a.test/blob", headers: { range: "bytes=4-7" }, responseType: "bytes" });
  assert(second.body[0] === 14 && second.body[3] === 17, "second range");
}
