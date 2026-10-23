// Contract test: POST with string and byte bodies; request headers are sent lower-cased; GET bodies are refused.
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
  "id": "http-post",
  "name": "http-post",
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
  const a = await http.request({
    method: "POST",
    url: "https://a.test/form",
    headers: { "Content-Type": "application/x-www-form-urlencoded", "X-Test": "yes" },
    body: "a=1&b=%D0%B6",
  });
  const ea = json.parse(a.body);
  assert(ea.method === "POST", "method POST");
  assert(ea.bodySha256 === "c973ee733973558f1ef4afce0dc1baaee846b409aa2e1834aec32ebd726aa70a", "string body sent as UTF-8");
  assert(ea.headers["x-test"] === "yes", "custom header sent, lower-cased");
  assert(ea.headers["content-type"] === "application/x-www-form-urlencoded", "content-type sent");

  const b = await http.request({ method: "POST", url: "https://a.test/bytes", body: new Uint8Array([1, 2, 3, 250]) });
  assert(json.parse(b.body).bodySha256 === "bacdef9c76b37eb70caaf81ea41ecddd56d625b72e453de5eabbd59745279847", "byte body sent unchanged");

  await expectError("InvalidInput", () => http.request({ url: "https://a.test/form", body: "x" }));
  await expectError("InvalidInput", () => http.request({ method: "PUT", url: "https://a.test/form" }));
  await expectError("InvalidInput", () => http.request({ url: "/relative" }));
}
