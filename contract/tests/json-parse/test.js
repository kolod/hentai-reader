// Contract test: json.parse has JSON.parse semantics.
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
  "id": "json-parse",
  "name": "json-parse",
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
  const v = json.parse('{"a":[1,2,{"b":null}],"s":"ж","n":-1.5e3,"t":true}');
  assert(v.a[2].b === null && v.s === "ж" && v.n === -1500 && v.t === true, "parsed values");
  let threw = false;
  try {
    json.parse("{bad");
  } catch (e) {
    threw = true;
  }
  assert(threw, "invalid JSON throws");
}
