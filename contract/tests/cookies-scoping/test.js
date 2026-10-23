// Contract test: cookies go only to their own domain, Set-Cookie updates the jar but stays hidden, script cookies merge.
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
  "id": "cookies-scoping",
  "name": "cookies-scoping",
  "version": "1.0.0",
  "contract": "1.0",
  "domains": [
    "a.test",
    "c.test"
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
  const set = await http.request({ url: "https://a.test/set" });
  assert(set.headers["set-cookie"] === undefined, "set-cookie is not exposed to scripts");

  const a = json.parse((await http.request({ url: "https://a.test/echo", headers: { Cookie: "nw=1" } })).body);
  const cookie = a.headers.cookie || "";
  assert(cookie.indexOf("session=s1") >= 0, "seeded cookie sent to its domain: " + cookie);
  assert(cookie.indexOf("fresh=f1") >= 0, "Set-Cookie stored in the jar: " + cookie);
  assert(cookie.indexOf("nw=1") >= 0, "script cookie merged: " + cookie);

  const c = json.parse((await http.request({ url: "https://c.test/echo" })).body);
  assert(c.headers.cookie === undefined, "no cookies sent to another domain");
}
