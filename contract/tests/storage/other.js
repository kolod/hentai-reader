// Runs first, as a different source, and writes a key that test.js must not see.
export const manifest = {
  "id": "storage-other",
  "name": "storage-other",
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
  await storage.set("shared", "from-other");
}
