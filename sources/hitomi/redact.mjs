// Fixture redaction for the Hitomi source (run by source-runner, never by the apps).
// Recorded responses would otherwise carry titles, tags and names of real galleries. Every text
// field is replaced with a neutral placeholder except the few tags and artists the test cases
// query; ids, page counts, sizes and file hashes are kept so the tests stay meaningful.
// Must be idempotent: `source-runner check` re-applies it to prove the fixtures are redacted.

/** `namespace:name` of tags the test cases search for or assert on. */
const KEEP_TAGS = new Set([
  "female:glasses",
  "female:sunglasses",
  "male:muscle",
  "female:apron",
  "female:robot girl",
]);
const KEEP_ARTISTS = new Set(["shindol"]);

export function redact(url, text) {
  if (/\/galleries\/\d+\.js$/.test(url)) return redactGallery(text);
  if (url.startsWith("https://tagindex.hitomi.la/")) return redactSuggestions(text);
  return text;
}

function redactGallery(text) {
  const m = /^\s*var\s+galleryinfo\s*=\s*([\s\S]*?);?\s*$/.exec(text);
  if (!m) return text;
  const g = JSON.parse(m[1]);
  const id = String(g.id);
  const named = (list, key, keep) =>
    list ? list.map((item, i) => ({ [key]: keep?.has(item[key]) ? item[key] : `${key} ${i + 1}` })) : null;
  const tags = (g.tags ?? []).map((t, i) => {
    const ns = Number(t.female) === 1 ? "female" : Number(t.male) === 1 ? "male" : "tag";
    const keep = KEEP_TAGS.has(`${ns}:${t.tag}`);
    return {
      tag: keep ? t.tag : `redacted ${i + 1}`,
      female: Number(t.female) === 1 ? "1" : "",
      male: Number(t.male) === 1 ? "1" : "",
    };
  });
  const files = (g.files ?? []).map((f, i) => ({
    name: `${String(i + 1).padStart(3, "0")}${/\.[a-z0-9]+$/i.exec(f.name ?? "")?.[0] ?? ""}`,
    hash: f.hash,
    width: f.width,
    height: f.height,
    hasavif: f.hasavif ? 1 : 0,
  }));
  const out = {
    id,
    title: `Gallery ${id}`,
    japanese_title: null,
    type: g.type,
    language: g.language ?? null,
    language_localname: g.language_localname ?? null,
    date: g.date,
    galleryurl: `/galleries/${id}.html`,
    artists: named(g.artists, "artist", KEEP_ARTISTS),
    groups: named(g.groups, "group"),
    parodys: named(g.parodys, "parody"),
    characters: named(g.characters, "character"),
    tags,
    files,
  };
  return `var galleryinfo = ${JSON.stringify(out)}`;
}

function redactSuggestions(text) {
  const list = JSON.parse(text);
  if (!Array.isArray(list)) return text;
  return JSON.stringify(
    list.map((s, i) => (KEEP_TAGS.has(`${s[2]}:${s[0]}`) ? [s[0], s[1], s[2]] : [`redacted ${i + 1}`, s[1], s[2]])),
  );
}
