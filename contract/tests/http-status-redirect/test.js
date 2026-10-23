// Contract test: non-2xx statuses are returned without throwing; redirects are followed; 303 switches to GET.
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
  "id": "http-status-redirect",
  "name": "http-status-redirect",
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
  const missing = await http.request({ url: "https://a.test/missing" });
  assert(missing.status === 404 && missing.body === "nope", "404 returned with body");

  const broken = await http.request({ url: "https://a.test/broken" });
  assert(broken.status === 500 && broken.body === "", "500 returned, empty body");

  const moved = await http.request({ url: "https://a.test/old" });
  assert(moved.status === 200 && moved.body === "new", "302 followed");
  assert(moved.url === "https://a.test/new", "final URL reported");

  const posted = await http.request({ method: "POST", url: "https://a.test/form", body: "x=1" });
  assert(posted.status === 200 && posted.body === "done", "303 followed with GET");
}
