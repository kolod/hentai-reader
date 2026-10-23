// Contract test: text responses are decoded per charset (UTF-8 default); bytes responses are Uint8Array.
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
  "id": "http-text-binary",
  "name": "http-text-binary",
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
  const utf8 = await http.request({ url: "https://a.test/utf8" });
  assert(utf8.status === 200, "status 200");
  assert(utf8.body === "Привіт, 世界", "UTF-8 text decoded");
  assert(utf8.headers["content-type"] === "text/plain; charset=utf-8", "header names are lower-case");
  assert(utf8.url === "https://a.test/utf8", "final url");

  const latin1 = await http.request({ url: "https://a.test/latin1" });
  assert(latin1.body === "café", "charset from content-type is honoured");

  const plain = await http.request({ url: "https://a.test/nocharset" });
  assert(plain.body === "ünï", "UTF-8 is the default charset");

  const blob = await http.request({ url: "https://a.test/blob", responseType: "bytes" });
  assert(blob.body instanceof Uint8Array, "bytes body is a Uint8Array");
  assert(blob.body.length === 4 && blob.body[0] === 0 && blob.body[2] === 0xfe && blob.body[3] === 0xff, "bytes intact");
}
