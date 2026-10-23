// Contract test: a 1.1 source receives its settings — the user's values merged over the manifest
// defaults, and only the ids the manifest declares.
// Runs inside the host's own engine; run() throws on failure.

function assert(cond, message) {
  if (!cond) throw new Error("assertion failed: " + message);
}

export const manifest = {
  "id": "settings-passing",
  "name": "settings-passing",
  "version": "1.0.0",
  "contract": "1.1",
  "domains": ["a.test"],
  "politeness": { "minIntervalMs": 0, "maxConcurrency": 4 },
  "login": "none",
  "filters": [],
  "settings": [
    { "id": "image_quality", "values": ["resampled", "original"], "default": "resampled" },
    { "id": "site", "values": ["e-hentai", "exhentai"], "default": "e-hentai" }
  ],
  "capabilities": { "suggestTags": false }
};

export async function run(settings) {
  assert(arguments.length === 1, "exactly one argument (the settings)");
  assert(settings !== null && typeof settings === "object", "settings is an object");
  const keys = Object.keys(settings).sort().join(",");
  assert(keys === "image_quality,site", "only declared ids are passed: " + keys);
  assert(settings.site === "exhentai", "user value overrides the default");
  assert(settings.image_quality === "resampled", "default used where the user chose nothing");
}
