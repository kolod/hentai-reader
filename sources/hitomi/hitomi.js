// @ts-check
// Hitomi.la source. Site mechanics: specs/001-source-contract/research.md (R10, R10a).
// Image routing evaluates the site's own gg.js (Constitution II exception); everything else is
// re-implemented from the site's common.js / search.js / results.js.

/** @typedef {import('@contract').SourceManifest} SourceManifest */
/** @typedef {import('@contract').SearchQuery} SearchQuery */
/** @typedef {import('@contract').SearchPage} SearchPage */
/** @typedef {import('@contract').GallerySummary} GallerySummary */
/** @typedef {import('@contract').Gallery} Gallery */
/** @typedef {import('@contract').GalleryType} GalleryType */
/** @typedef {import('@contract').PageImage} PageImage */
/** @typedef {import('@contract').RetryHint} RetryHint */
/** @typedef {import('@contract').Tag} Tag */
/** @typedef {import('@contract').TagSuggestion} TagSuggestion */
/** @typedef {import('@contract').ImageRequest} ImageRequest */

const DOMAIN2 = "gold-usergeneratedcontent.net";
const LTN = `https://ltn.${DOMAIN2}`;
const TAGINDEX = "https://tagindex.hitomi.la";
const SITE = "https://hitomi.la";
const REFERER = `${SITE}/`;
const PAGE_SIZE = 25;
const GG_TTL_MS = 10 * 60 * 1000;
const NODE_SIZE = 464;
/** Served WebP images are downscaled so the long side is at most this (SC-006 finding, R10a). */
const MAX_SERVED_SIDE = 2880;
const B = 16;
const RESULT_CACHE_SIZE = 8;

/** Hitomi language name ↔ ISO 639-1 (Cebuano has no ISO 639-1 code and is not offered). */
const LANGUAGES = {
  indonesian: "id", javanese: "jv", catalan: "ca", czech: "cs", danish: "da", german: "de",
  estonian: "et", english: "en", spanish: "es", esperanto: "eo", french: "fr", hindi: "hi",
  icelandic: "is", italian: "it", latin: "la", hungarian: "hu", dutch: "nl", norwegian: "no",
  polish: "pl", portuguese: "pt", romanian: "ro", albanian: "sq", slovak: "sk", serbian: "sr",
  finnish: "fi", swedish: "sv", tagalog: "tl", vietnamese: "vi", turkish: "tr", greek: "el",
  bulgarian: "bg", mongolian: "mn", russian: "ru", ukrainian: "uk", hebrew: "he", arabic: "ar",
  persian: "fa", thai: "th", burmese: "my", korean: "ko", chinese: "zh", japanese: "ja",
};
/** @type {Record<string, string>} */
const LANGUAGE_BY_ISO = Object.fromEntries(Object.entries(LANGUAGES).map(([name, iso]) => [iso, name]));

/** @type {Record<string, GalleryType>} */
const TYPES = {
  doujinshi: "doujinshi", manga: "manga", artistcg: "artist_cg", gamecg: "game_cg",
  imageset: "image_set", anime: "anime",
};
/** @type {Record<string, string>} */
const TYPE_BY_CONTRACT = Object.fromEntries(Object.entries(TYPES).map(([site, c]) => [c, site]));

/** Contract sort value → Hitomi orderby/orderbykey. */
/** @type {Record<string, { orderby: "date" | "popular", key: string }>} */
const SORTS = {
  latest: { orderby: "date", key: "added" },
  popular_day: { orderby: "popular", key: "today" },
  popular_week: { orderby: "popular", key: "week" },
  popular_month: { orderby: "popular", key: "month" },
  popular_year: { orderby: "popular", key: "year" },
};

/** Query namespace → nozomi area (contract tag namespaces map back to the site's). */
/** @type {Record<string, string>} */
const AREAS = {
  female: "tag", male: "tag", tag: "tag", other: "tag", artist: "artist", group: "group",
  series: "series", parody: "series", character: "character", type: "type",
};

/** @type {SourceManifest} */
export const manifest = {
  id: "hitomi",
  name: "Hitomi.la",
  version: "0.2.0",
  contract: "1.2",
  domains: ["hitomi.la", "*.hitomi.la", DOMAIN2, `*.${DOMAIN2}`],
  politeness: { minIntervalMs: 100, maxConcurrency: 4 },
  login: "none",
  filters: [
    {
      id: "sort",
      kind: "single",
      values: ["latest", "popular_day", "popular_week", "popular_month", "popular_year"],
      default: "latest",
    },
    { id: "language", kind: "multi", values: Object.values(LANGUAGES) },
    { id: "type", kind: "multi", values: Object.values(TYPES) },
  ],
  capabilities: { suggestTags: true },
};

// --- HTTP helpers ------------------------------------------------------------------------------

/**
 * @param {string} url
 * @param {{ range?: string }} [opts]
 */
async function getBytes(url, opts = {}) {
  /** @type {Record<string, string>} */
  const headers = { referer: REFERER };
  if (opts.range) headers.range = opts.range;
  const res = await http.request({ url: encodeURI(url), headers, responseType: "bytes" });
  checkLimits(res);
  return res;
}

/** @param {string} url */
async function getText(url) {
  const res = await http.request({ url: encodeURI(url), headers: { referer: REFERER } });
  checkLimits(res);
  return res;
}

/** @param {{ status: number, headers: Record<string, string>, url: string }} res */
function checkLimits(res) {
  if (res.status === 429 || res.status === 503) {
    throw new SourceError("RateLimited", `Hitomi rate limit (HTTP ${res.status}) at ${res.url}`, {
      retryAfterSeconds: retryAfter(res.headers["retry-after"]),
    });
  }
}

/** @param {string | undefined} value */
function retryAfter(value) {
  if (!value) return undefined;
  const seconds = Number(value);
  if (Number.isFinite(seconds)) return Math.max(0, Math.round(seconds));
  const at = Date.parse(value);
  return Number.isNaN(at) ? undefined : Math.max(0, Math.round((at - Date.now()) / 1000));
}

/** @param {string} what @param {{ status: number, url: string }} res */
function unexpected(what, res) {
  return new SourceError("SiteChanged", `${what}: unexpected HTTP ${res.status} at ${res.url}`);
}

// --- binary helpers ----------------------------------------------------------------------------

/** @param {string} str */
function utf8(str) {
  /** @type {number[]} */
  const out = [];
  for (let i = 0; i < str.length; i++) {
    let c = str.charCodeAt(i);
    if (c >= 0xd800 && c < 0xdc00 && i + 1 < str.length) {
      c = 0x10000 + ((c & 0x3ff) << 10) + (str.charCodeAt(++i) & 0x3ff);
    }
    if (c < 0x80) out.push(c);
    else if (c < 0x800) out.push(0xc0 | (c >> 6), 0x80 | (c & 63));
    else if (c < 0x10000) out.push(0xe0 | (c >> 12), 0x80 | ((c >> 6) & 63), 0x80 | (c & 63));
    else out.push(0xf0 | (c >> 18), 0x80 | ((c >> 12) & 63), 0x80 | ((c >> 6) & 63), 0x80 | (c & 63));
  }
  return new Uint8Array(out);
}

const SHA256_K = [
  0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
  0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
  0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
  0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
  0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
  0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
  0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
  0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
];

/** Pure-JS SHA-256 (neither QuickJS nor Jint offers crypto). @param {Uint8Array} msg */
function sha256(msg) {
  const len = msg.length;
  const padded = new Uint8Array(((len + 9 + 63) >> 6) << 6);
  padded.set(msg);
  padded[len] = 0x80;
  const bits = len * 8;
  const view = new DataView(padded.buffer);
  view.setUint32(padded.length - 8, Math.floor(bits / 0x100000000));
  view.setUint32(padded.length - 4, bits >>> 0);
  const h = [0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19];
  const w = new Array(64);
  for (let off = 0; off < padded.length; off += 64) {
    for (let i = 0; i < 16; i++) w[i] = view.getUint32(off + i * 4);
    for (let i = 16; i < 64; i++) {
      const a = w[i - 15], b = w[i - 2];
      const s0 = ((a >>> 7) | (a << 25)) ^ ((a >>> 18) | (a << 14)) ^ (a >>> 3);
      const s1 = ((b >>> 17) | (b << 15)) ^ ((b >>> 19) | (b << 13)) ^ (b >>> 10);
      w[i] = (w[i - 16] + s0 + w[i - 7] + s1) | 0;
    }
    let [a, b, c, d, e, f, g, hh] = h;
    for (let i = 0; i < 64; i++) {
      const S1 = ((e >>> 6) | (e << 26)) ^ ((e >>> 11) | (e << 21)) ^ ((e >>> 25) | (e << 7));
      const t1 = (hh + S1 + ((e & f) ^ (~e & g)) + SHA256_K[i] + w[i]) | 0;
      const S0 = ((a >>> 2) | (a << 30)) ^ ((a >>> 13) | (a << 19)) ^ ((a >>> 22) | (a << 10));
      const t2 = (S0 + ((a & b) ^ (a & c) ^ (b & c))) | 0;
      hh = g; g = f; f = e; e = (d + t1) | 0; d = c; c = b; b = a; a = (t1 + t2) | 0;
    }
    h[0] = (h[0] + a) | 0; h[1] = (h[1] + b) | 0; h[2] = (h[2] + c) | 0; h[3] = (h[3] + d) | 0;
    h[4] = (h[4] + e) | 0; h[5] = (h[5] + f) | 0; h[6] = (h[6] + g) | 0; h[7] = (h[7] + hh) | 0;
  }
  const out = new Uint8Array(32);
  const ov = new DataView(out.buffer);
  for (let i = 0; i < 8; i++) ov.setUint32(i * 4, h[i] >>> 0);
  return out;
}

/** @param {DataView} view @param {number} pos */
function uint64(view, pos) {
  return view.getUint32(pos) * 0x100000000 + view.getUint32(pos + 4);
}

/** Big-endian int32 gallery ids. @param {Uint8Array} bytes */
function int32s(bytes) {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const ids = new Array(bytes.byteLength >> 2);
  for (let i = 0; i < ids.length; i++) ids[i] = view.getInt32(i * 4);
  return ids;
}

// --- gg.js and image URLs ----------------------------------------------------------------------

/** @typedef {{ m: (g: number) => number, s: (h: string) => string, b: string }} GG */

/** @type {{ gg: GG, fetchedAt: number } | null} */
let ggCache = null;
/** @type {Promise<GG> | null} */
let ggLoading = null;

/** @param {boolean} force */
function ensureGg(force) {
  if (!force && ggCache && Date.now() - ggCache.fetchedAt < GG_TTL_MS) return Promise.resolve(ggCache.gg);
  if (!force && ggLoading) return ggLoading;
  ggLoading = loadGg(force).finally(() => {
    ggLoading = null;
  });
  return ggLoading;
}

/** @param {boolean} force @returns {Promise<GG>} */
async function loadGg(force) {
  /** @type {{ text: string, fetchedAt: number } | null} */
  let cached = null;
  if (!force) {
    const stored = await storage.get("gg");
    if (stored) {
      const parsed = /** @type {{ text: string, fetchedAt: number }} */ (json.parse(stored));
      if (Date.now() - parsed.fetchedAt < GG_TTL_MS) cached = parsed;
    }
  }
  if (!cached) {
    const res = await getText(`${LTN}/gg.js`);
    if (res.status !== 200) throw unexpected("gg.js", res);
    cached = { text: res.body, fetchedAt: Date.now() };
    await storage.set("gg", JSON.stringify(cached));
  }
  /** @type {GG} */
  let gg;
  try {
    gg = new Function(`"use strict"; var gg;\n${cached.text}\n; return gg;`)();
  } catch (e) {
    throw new SourceError("SiteChanged", `gg.js failed to evaluate: ${e instanceof Error ? e.message : e}`);
  }
  if (!gg || typeof gg.m !== "function" || typeof gg.s !== "function" || typeof gg.b !== "string") {
    throw new SourceError("SiteChanged", "gg.js no longer defines gg.m, gg.s and gg.b");
  }
  ggCache = { gg, fetchedAt: cached.fetchedAt };
  return gg;
}

/** @param {string} hash */
function hashBucket(hash) {
  const m = /^[0-9a-f]{61}([0-9a-f]{2})([0-9a-f])$/.exec(hash);
  if (!m) throw new SourceError("SiteChanged", `unexpected image hash: ${hash}`);
  return parseInt(m[2] + m[1], 16);
}

/** Full-size WebP (common.js url_from_url_from_hash(…, 'webp')). @param {GG} gg @param {string} hash */
function imageUrl(gg, hash) {
  return `https://w${1 + gg.m(hashBucket(hash))}.${DOMAIN2}/${gg.b}${gg.s(hash)}/${hash}.webp`;
}

/** Thumbnail (common.js url_from_url_from_hash(…, dir, 'webp', 'tn')). @param {GG} gg @param {string} hash @param {string} dir */
function thumbnailUrl(gg, hash, dir) {
  const sub = String.fromCharCode(97 + gg.m(hashBucket(hash)));
  return `https://${sub}tn.${DOMAIN2}/${dir}/${hash.slice(-1)}/${hash.slice(-3, -1)}/${hash}.webp`;
}

/** @param {string} url @returns {ImageRequest} */
function image(url) {
  return { url, headers: { referer: REFERER } };
}

// --- gallery metadata --------------------------------------------------------------------------

/**
 * @typedef {{ tag: string, female?: string | number, male?: string | number }} RawTag
 * @typedef {{ name: string, hash: string, width: number, height: number }} RawFile
 * @typedef {{
 *   id: string | number, title: string, japanese_title: string | null, type: string,
 *   language: string | null, date: string, galleryurl: string, tags: RawTag[] | null,
 *   artists: { artist: string }[] | null, groups: { group: string }[] | null,
 *   parodys: { parody: string }[] | null, characters: { character: string }[] | null,
 *   files: RawFile[]
 * }} GalleryInfo
 */

/** @param {string} id @returns {Promise<GalleryInfo | null>} null if the gallery does not exist */
async function fetchInfo(id) {
  const res = await getText(`${LTN}/galleries/${id}.js`);
  if (res.status === 404) return null;
  if (res.status !== 200) throw unexpected(`gallery ${id}`, res);
  const m = /^\s*var\s+galleryinfo\s*=\s*([\s\S]*?);?\s*$/.exec(res.body);
  if (!m) throw new SourceError("SiteChanged", `gallery ${id}: galleryinfo assignment not found`);
  let info;
  try {
    info = /** @type {GalleryInfo} */ (json.parse(m[1]));
  } catch (e) {
    throw new SourceError("SiteChanged", `gallery ${id}: galleryinfo is not JSON`);
  }
  if (!info || !Array.isArray(info.files) || typeof info.title !== "string") {
    throw new SourceError("SiteChanged", `gallery ${id}: galleryinfo lacks title or files`);
  }
  return info;
}

/** @param {GalleryInfo} info @returns {Tag[]} */
function allTags(info) {
  /** @type {Tag[]} */
  const tags = [];
  for (const a of info.artists ?? []) tags.push({ namespace: "artist", name: a.artist });
  for (const g of info.groups ?? []) tags.push({ namespace: "group", name: g.group });
  for (const p of info.parodys ?? []) tags.push({ namespace: "parody", name: p.parody });
  for (const c of info.characters ?? []) tags.push({ namespace: "character", name: c.character });
  for (const t of info.tags ?? []) {
    const ns = Number(t.female) === 1 ? "female" : Number(t.male) === 1 ? "male" : "other";
    tags.push({ namespace: ns, name: t.tag });
  }
  return tags;
}

/** "2026-10-23 12:00:00-05" → "2026-10-23T12:00:00-05:00" (no Date.parse of site formats). @param {string | null | undefined} date */
function isoDate(date) {
  if (!date) return null;
  const m = /^(\d{4}-\d{2}-\d{2})[ T](\d{2}:\d{2}(?::\d{2})?)(?:\.\d+)?\s*(Z|[+-]\d{2}(?::?\d{2})?)?$/.exec(date.trim());
  if (!m) return null;
  const time = m[2].length === 5 ? `${m[2]}:00` : m[2];
  let zone = m[3] ?? "Z";
  if (zone !== "Z") zone = /^[+-]\d{2}$/.test(zone) ? `${zone}:00` : zone.replace(/^([+-]\d{2})(\d{2})$/, "$1:$2");
  return `${m[1]}T${time}${zone}`;
}

/**
 * Size of the WebP actually served. Originals up to MAX_SERVED_SIDE are served as-is (exact);
 * larger ones are downscaled to a long side of exactly MAX_SERVED_SIDE, and the short side can
 * differ from proportional rounding by 1 px, so it is an estimate (Constitution IV).
 * @param {number} width @param {number} height
 * @returns {{ width: number, height: number, size: "exact" | "estimated" }}
 */
function servedSize(width, height) {
  const longest = Math.max(width, height);
  if (longest <= MAX_SERVED_SIDE) return { width, height, size: "exact" };
  const k = MAX_SERVED_SIDE / longest;
  return {
    width: width >= height ? MAX_SERVED_SIDE : Math.round(width * k),
    height: height > width ? MAX_SERVED_SIDE : Math.round(height * k),
    size: "estimated",
  };
}

/** @param {GalleryInfo} info @param {GG} gg @returns {GallerySummary} */
function summary(info, gg) {
  const tags = allTags(info);
  return {
    id: String(info.id),
    title: info.title,
    cover: info.files.length ? image(thumbnailUrl(gg, info.files[0].hash, "webpbigtn")) : null,
    language: info.language ? (/** @type {Record<string, string>} */ (LANGUAGES)[info.language] ?? null) : null,
    type: TYPES[info.type] ?? null,
    pageCount: info.files.length,
    artists: (info.artists ?? []).map((a) => a.artist),
    tags: tags.filter((t) => t.namespace !== "artist").slice(0, 20),
    webUrl: `${SITE}${info.galleryurl}`,
  };
}

// --- search ------------------------------------------------------------------------------------

/**
 * @typedef {{ area: string, tag: string, language?: string }} Term
 * @typedef {{ language: string, orderby: "date" | "popular", key: string }} State
 * Several languages: the site keeps one list per language and has no "any of" search, so the
 * query runs over all languages and keeps galleries found in any of the per-language indexes
 * (`anyLanguage`), which preserves the site's order (date or popularity).
 */

/** @param {SearchQuery} query */
function parseQuery(query) {
  const filters = /** @type {Record<string, unknown>} */ (query.filters ?? {});
  for (const id of Object.keys(filters)) {
    if (!["sort", "language", "type"].includes(id)) throw new SourceError("InvalidInput", `unsupported filter: ${id}`);
  }
  const sort = filters.sort ?? "latest";
  const order = typeof sort === "string" ? SORTS[sort] : undefined;
  if (!order) throw new SourceError("InvalidInput", `unsupported sort: ${String(sort)}`);
  /** @type {State} */
  const state = { language: "all", orderby: order.orderby, key: order.key };
  /** @type {string[]} */
  let anyLanguage = [];
  if (filters.language !== undefined) {
    // A list since 0.2.0 (contract 1.2 multi filter); a single code is still accepted.
    const codes = Array.isArray(filters.language) ? filters.language : [filters.language];
    const names = codes.map((c) => (typeof c === "string" ? LANGUAGE_BY_ISO[c] : undefined));
    if (names.some((n) => !n)) throw new SourceError("InvalidInput", `unsupported language: ${JSON.stringify(filters.language)}`);
    const unique = [...new Set(/** @type {string[]} */ (names))];
    if (unique.length === 1) state.language = unique[0];
    else anyLanguage = unique;
  }
  /** @type {Term[]} */
  const types = [];
  if (filters.type !== undefined) {
    if (!Array.isArray(filters.type)) throw new SourceError("InvalidInput", "filter type must be a list");
    for (const t of filters.type) {
      const site = TYPE_BY_CONTRACT[t];
      if (!site) throw new SourceError("InvalidInput", `unsupported type: ${String(t)}`);
      types.push({ area: "type", tag: site });
    }
  }

  /** @type {Term[]} */ const positive = [];
  /** @type {Term[]} */ const negative = [];
  /** @type {string[]} */ const words = [];
  /** @type {string[]} */ const negativeWords = [];
  for (const raw of (query.text ?? "").toLowerCase().trim().split(/\s+/)) {
    if (!raw) continue;
    const neg = raw.startsWith("-");
    const term = (neg ? raw.slice(1) : raw).replace(/_/g, " ");
    if (!term) continue;
    const colon = term.indexOf(":");
    if (colon < 0) {
      (neg ? negativeWords : words).push(term);
      continue;
    }
    const ns = term.slice(0, colon);
    const value = term.slice(colon + 1);
    if (!value) throw new SourceError("InvalidInput", `empty value in term: ${raw}`);
    if (ns === "language") {
      if (!(value in LANGUAGES)) throw new SourceError("InvalidInput", `unknown language: ${value}`);
      if (neg) negative.push({ area: "all", tag: "index", language: value });
      else {
        state.language = value;
        anyLanguage = [];   // the query's own language: term wins over the filter
      }
      continue;
    }
    const area = AREAS[ns];
    if (!area) throw new SourceError("InvalidInput", `unsupported namespace: ${ns}`);
    const tag = ns === "female" || ns === "male" ? `${ns}:${value}` : value;
    (neg ? negative : positive).push({ area, tag });
  }
  return { state, positive, negative, words, negativeWords, types, anyLanguage };
}

/** @param {Term} term @param {State} state */
function nozomiUrl(term, state) {
  const lang = term.language ?? state.language;
  if (state.orderby === "popular") {
    if (term.area === "all") return `${LTN}/n/popular/${state.key}-${lang}.nozomi`;
    return `${LTN}/n/${term.area}/popular/${state.key}/${term.tag}-${lang}.nozomi`;
  }
  if (term.area === "all") return `${LTN}/n/${term.tag}-${lang}.nozomi`;
  return `${LTN}/n/${term.area}/${term.tag}-${lang}.nozomi`;
}

/**
 * Whole nozomi as a sorted typed array, for membership tests by binary search: a plain array or a
 * Set of a million ids costs far more engine memory than the 4 bytes per id kept here.
 * @param {string} url
 */
async function nozomiSet(url) {
  const res = await getBytes(url);
  if (res.status === 404) return new Int32Array(0);
  if (res.status !== 200) throw unexpected("nozomi", res);
  const view = new DataView(res.body.buffer, res.body.byteOffset, res.body.byteLength);
  const ids = new Int32Array(res.body.byteLength >> 2);
  for (let i = 0; i < ids.length; i++) ids[i] = view.getInt32(i * 4);
  return ids.sort();
}

/** @param {Int32Array} sorted @param {number} id */
function contains(sorted, id) {
  let lo = 0;
  let hi = sorted.length - 1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    const v = sorted[mid];
    if (v === id) return true;
    if (v < id) lo = mid + 1;
    else hi = mid - 1;
  }
  return false;
}

/** Whole id list of a nozomi (missing list = no galleries). @param {string} url @returns {Promise<number[]>} */
async function nozomiAll(url) {
  const res = await getBytes(url);
  if (res.status === 404) return [];
  if (res.status !== 200) throw unexpected("nozomi", res);
  return int32s(res.body);
}

/** One page of a nozomi via Range. @param {string} url @param {number} offset @param {number} [count] */
async function nozomiPage(url, offset, count = PAGE_SIZE) {
  const res = await getBytes(url, { range: `bytes=${offset * 4}-${(offset + count) * 4 - 1}` });
  if (res.status === 404 || res.status === 416) return { ids: [], total: offset };
  if (res.status === 200) {
    const all = int32s(res.body);
    return { ids: all.slice(offset, offset + count), total: all.length };
  }
  if (res.status !== 206) throw unexpected("nozomi range", res);
  const m = /\/(\d+)\s*$/.exec(res.headers["content-range"] ?? "");
  if (!m) throw new SourceError("SiteChanged", "nozomi range response without total size");
  return { ids: int32s(res.body), total: Number(m[1]) >> 2 };
}

// galleriesindex B-tree (search.js decode_node / B_search / get_galleryids_from_data)

/** @type {string | null} */
let indexVersion = null;

async function galleriesIndexVersion() {
  if (indexVersion) return indexVersion;
  const res = await getText(`${LTN}/galleriesindex/version`);
  if (res.status !== 200 || !/^\d+\s*$/.test(res.body)) throw unexpected("galleriesindex version", res);
  indexVersion = res.body.trim();
  return indexVersion;
}

/** @param {Uint8Array} bytes */
function decodeNode(bytes) {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  let pos = 0;
  const keyCount = view.getInt32(pos);
  pos += 4;
  /** @type {Uint8Array[]} */
  const keys = [];
  for (let i = 0; i < keyCount; i++) {
    const size = view.getInt32(pos);
    if (!size || size > 32) throw new SourceError("SiteChanged", "galleriesindex: bad key size");
    pos += 4;
    keys.push(bytes.subarray(pos, pos + size));
    pos += size;
  }
  const dataCount = view.getInt32(pos);
  pos += 4;
  /** @type {[number, number][]} */
  const datas = [];
  for (let i = 0; i < dataCount; i++) {
    datas.push([uint64(view, pos), view.getInt32(pos + 8)]);
    pos += 12;
  }
  /** @type {number[]} */
  const subnodes = [];
  for (let i = 0; i < B + 1; i++) {
    subnodes.push(uint64(view, pos));
    pos += 8;
  }
  return { keys, datas, subnodes };
}

/** @param {Uint8Array} a @param {Uint8Array} b */
function compareBytes(a, b) {
  const n = Math.min(a.length, b.length);
  for (let i = 0; i < n; i++) {
    if (a[i] !== b[i]) return a[i] < b[i] ? -1 : 1;
  }
  return 0;
}

/** Gallery ids for one free-text word. @param {string} word @returns {Promise<number[]>} */
async function searchWord(word) {
  const version = await galleriesIndexVersion();
  const key = sha256(utf8(word)).subarray(0, 4);
  const indexUrl = `${LTN}/galleriesindex/galleries.${version}.index`;
  let address = 0;
  for (let depth = 0; depth < 64; depth++) {
    const res = await getBytes(indexUrl, { range: `bytes=${address}-${address + NODE_SIZE - 1}` });
    if (res.status !== 206 && res.status !== 200) throw unexpected("galleriesindex node", res);
    const node = decodeNode(res.body);
    if (node.keys.length === 0) return [];
    let i = 0;
    let cmp = -1;
    for (; i < node.keys.length; i++) {
      cmp = compareBytes(key, node.keys[i]);
      if (cmp <= 0) break;
    }
    if (cmp === 0) return galleryIdsFromData(version, node.datas[i]);
    if (node.subnodes.every((a) => a === 0) || node.subnodes[i] === 0) return [];
    address = node.subnodes[i];
  }
  throw new SourceError("SiteChanged", "galleriesindex: tree too deep");
}

/** @param {string} version @param {[number, number]} data */
async function galleryIdsFromData(version, [offset, length]) {
  if (length <= 0 || length > 100000000) throw new SourceError("SiteChanged", "galleriesindex: bad data length");
  const res = await getBytes(`${LTN}/galleriesindex/galleries.${version}.data`, {
    range: `bytes=${offset}-${offset + length - 1}`,
  });
  if (res.status !== 206 && res.status !== 200) throw unexpected("galleriesindex data", res);
  const view = new DataView(res.body.buffer, res.body.byteOffset, res.body.byteLength);
  const count = view.getInt32(0);
  if (count <= 0 || res.body.byteLength !== count * 4 + 4) throw new SourceError("SiteChanged", "galleriesindex: bad data block");
  return int32s(res.body.subarray(4));
}

/** @type {Map<string, number[]>} */
const resultCache = new Map();

/** Ordered result ids for a multi-term query (results.js semantics, R10a). @param {ReturnType<typeof parseQuery>} q */
async function resultIds(q) {
  const cacheKey = JSON.stringify(q);
  const cached = resultCache.get(cacheKey);
  if (cached) return cached;
  /** @type {State} */
  const unordered = { language: q.state.language, orderby: "date", key: "added" };
  const positive = [...q.positive];

  /** @type {number[]} */
  let ids;
  if (positive.length) {
    ids = await nozomiAll(nozomiUrl(/** @type {Term} */ (positive.shift()), q.state));
  } else if (q.words.length && q.state.orderby === "date" && q.state.language === "all") {
    ids = await searchWord(/** @type {string} */ (q.words[0]));
    q = { ...q, words: q.words.slice(1) };
  } else {
    ids = await nozomiAll(nozomiUrl({ area: "all", tag: "index" }, q.state));
  }

  /** @param {number[]} other */
  const keep = (other) => {
    const set = new Set(other);
    ids = ids.filter((id) => set.has(id));
  };
  /** @param {number[]} other */
  const drop = (other) => {
    const set = new Set(other);
    ids = ids.filter((id) => !set.has(id));
  };

  const lists = await Promise.all([
    Promise.all(positive.map((t) => nozomiAll(nozomiUrl(t, unordered)))),
    Promise.all(q.words.map(searchWord)),
    Promise.all(q.types.map((t) => nozomiAll(nozomiUrl(t, unordered)))),
    Promise.all(q.negative.map((t) => nozomiAll(nozomiUrl(t, unordered)))),
    Promise.all(q.negativeWords.map(searchWord)),
    Promise.all(q.anyLanguage.map((language) => nozomiAll(nozomiUrl({ area: "all", tag: "index", language }, unordered)))),
  ]);
  const [pos, words, types, neg, negWords, languages] = lists;
  for (const list of pos) keep(list);
  for (const list of words) keep(list);
  if (types.length) keep(types.flat());
  if (languages.length) keep(languages.flat());
  for (const list of neg) drop(list);
  for (const list of negWords) drop(list);

  if (resultCache.size >= RESULT_CACHE_SIZE) resultCache.delete(/** @type {string} */ (resultCache.keys().next().value));
  resultCache.set(cacheKey, ids);
  return ids;
}

/** @param {string | null} cursor */
function parseCursor(cursor) {
  if (cursor === null) return 0;
  const m = /^o:(\d+)$/.exec(cursor);
  if (!m) throw new SourceError("InvalidInput", `invalid cursor: ${cursor}`);
  return Number(m[1]);
}

/**
 * Feed (no tags or words) in any of several languages: the site's all-language list is read in
 * Range chunks and filtered by the per-language lists (and type / excluded lists), so the order is
 * the site's and only the small lists are held whole. Cursor `b:<position in the all-language list>`.
 * @param {ReturnType<typeof parseQuery>} q
 * @param {string | null} cursor
 * @returns {Promise<SearchPage>}
 */
async function searchAnyLanguage(q, cursor) {
  let position = 0;
  if (cursor !== null) {
    const m = /^b:(\d+)$/.exec(cursor);
    if (!m) throw new SourceError("InvalidInput", `invalid cursor: ${cursor}`);
    position = Number(m[1]);
  }
  /** @type {State} */
  const unordered = { language: "all", orderby: "date", key: "added" };
  const [languages, types, negative] = await Promise.all([
    Promise.all(q.anyLanguage.map((language) => nozomiSet(nozomiUrl({ area: "all", tag: "index", language }, unordered)))),
    Promise.all(q.types.map((t) => nozomiSet(nozomiUrl(t, unordered)))),
    Promise.all(q.negative.map((t) => nozomiSet(nozomiUrl(t, unordered)))),
  ]);
  const matches = (/** @type {number} */ id) =>
    languages.some((l) => contains(l, id)) &&
    (types.length === 0 || types.some((t) => contains(t, id))) &&
    !negative.some((n) => contains(n, id));
  // Languages do not overlap, so for the date feed the total is exact.
  const languageTotal = languages.reduce((sum, l) => sum + l.length, 0);
  const exactTotal = q.state.orderby === "date" && types.length === 0 && negative.length === 0;

  const base = nozomiUrl({ area: "all", tag: "index" }, q.state);
  /** @type {number[]} */
  const ids = [];
  let baseTotal = Infinity;
  let chunk = 1000;
  while (ids.length < PAGE_SIZE && position < baseTotal) {
    const page = await nozomiPage(base, position, chunk);
    baseTotal = page.total;
    if (page.ids.length === 0) break;
    let used = 0;
    for (const id of page.ids) {
      used++;
      if (matches(id) && ids.push(id) === PAGE_SIZE) break;
    }
    position += used;
    // Next read sized by how often ids matched so far (languages' share of the feed).
    const density = Math.max(languageTotal / Math.max(1, baseTotal), 0.0005);
    chunk = Math.min(50000, Math.max(200, Math.ceil(((PAGE_SIZE - ids.length) / density) * 1.5)));
  }

  const gg = await ensureGg(false);
  const infos = await Promise.all(ids.map((id) => fetchInfo(String(id))));
  const items = /** @type {GalleryInfo[]} */ (infos.filter((i) => i !== null)).map((info) => summary(info, gg));
  return {
    items,
    nextCursor: position < baseTotal && ids.length === PAGE_SIZE ? `b:${position}` : null,
    ...(exactTotal ? { total: languageTotal } : {}),
  };
}

/**
 * @param {SearchQuery} query
 * @param {string | null} cursor
 * @returns {Promise<SearchPage>}
 */
export async function search(query, cursor) {
  const q = parseQuery(query);
  if (q.anyLanguage.length > 0 && q.positive.length === 0 && q.words.length === 0 && q.negativeWords.length === 0) {
    return await searchAnyLanguage(q, cursor);
  }
  const offset = parseCursor(cursor);
  const simple =
    q.words.length === 0 && q.negativeWords.length === 0 && q.negative.length === 0 && q.types.length === 0 &&
    q.anyLanguage.length === 0;

  /** @type {number[]} */
  let ids;
  /** @type {number} */
  let total;
  if (simple && q.positive.length <= 1) {
    const term = q.positive[0] ?? { area: "all", tag: "index" };
    const page = await nozomiPage(nozomiUrl(term, q.state), offset);
    ids = page.ids;
    total = page.total;
  } else {
    const all = await resultIds(q);
    ids = all.slice(offset, offset + PAGE_SIZE);
    total = all.length;
  }

  const gg = await ensureGg(false);
  const infos = await Promise.all(ids.map((id) => fetchInfo(String(id))));
  const items = /** @type {GalleryInfo[]} */ (infos.filter((i) => i !== null)).map((info) => summary(info, gg));
  const next = offset + PAGE_SIZE;
  return { items, nextCursor: next < total ? `o:${next}` : null, total };
}

/**
 * @param {string} prefix e.g. "female:gla" or "glasses"
 * @returns {Promise<TagSuggestion[]>}
 */
export async function suggestTags(prefix) {
  const query = prefix.toLowerCase().trim().replace(/_/g, " ");
  const colon = query.indexOf(":");
  const ns = colon >= 0 ? query.slice(0, colon) : "";
  const term = colon >= 0 ? query.slice(colon + 1) : query;
  if (!term) return [];
  let field = "global";
  if (ns) {
    const site = ns === "parody" ? "series" : ns === "other" ? "tag" : ns;
    if (!(ns in AREAS) && ns !== "language") throw new SourceError("InvalidInput", `unsupported namespace: ${ns}`);
    field = site;
  }
  /** @type {Record<string, string>} */
  const escapes = { " ": "_", "/": "slash", ".": "dot" };
  const path = term.split("").map((c) => escapes[c] ?? c).join("/");
  const res = await getText(`${TAGINDEX}/${field}/${path}.json`);
  if (res.status === 404) return [];
  if (res.status !== 200) throw unexpected("tag suggestions", res);
  const raw = json.parse(res.body);
  if (!Array.isArray(raw)) throw new SourceError("SiteChanged", "tag suggestions are not a list");
  return raw.map((s) => {
    if (!Array.isArray(s) || typeof s[0] !== "string") throw new SourceError("SiteChanged", "bad tag suggestion");
    const site = typeof s[2] === "string" ? s[2] : null;
    return {
      namespace: site === "series" ? "parody" : site === "tag" ? "other" : site,
      name: s[0],
      count: typeof s[1] === "number" ? s[1] : null,
    };
  });
}

// --- gallery and pages -------------------------------------------------------------------------

/**
 * @param {string} id
 * @returns {Promise<Gallery>}
 */
export async function getGallery(id) {
  if (!/^\d+$/.test(id)) throw new SourceError("InvalidInput", `Hitomi gallery ids are numeric: ${id}`);
  const info = await fetchInfo(id);
  if (!info) throw new SourceError("NotFound", `gallery ${id} does not exist`);
  if (info.files.length === 0) throw new SourceError("SiteChanged", `gallery ${id} has no files`);
  const gg = await ensureGg(false);
  const base = summary(info, gg);
  return {
    ...base,
    titleOriginal: info.japanese_title && info.japanese_title !== info.title ? info.japanese_title : null,
    tags: allTags(info),
    uploadedAt: isoDate(info.date),
    rating: null,
    uploader: null,
    pages: info.files.map((f, index) => {
      if (!(f.width > 0) || !(f.height > 0)) throw new SourceError("SiteChanged", `gallery ${id}: page ${index} without size`);
      return {
        index,
        ...servedSize(f.width, f.height),
        thumbnail: image(thumbnailUrl(gg, f.hash, "webpsmalltn")),
        fileName: f.name,
      };
    }),
    sourceData: { hashes: info.files.map((f) => f.hash) },
  };
}

/**
 * @param {Gallery} gallery
 * @param {number} index
 * @param {RetryHint | null} [retry] null when absent (contract 1.1+)
 * @returns {Promise<PageImage>}
 */
export async function getPageUrl(gallery, index, retry) {
  const data = /** @type {{ hashes?: unknown }} */ (gallery.sourceData ?? {});
  const hashes = Array.isArray(data.hashes) ? data.hashes : null;
  if (!hashes) throw new SourceError("InvalidInput", "gallery.sourceData.hashes missing — pass a gallery from getGallery");
  if (!Number.isInteger(index) || index < 0 || index >= hashes.length) {
    throw new SourceError("InvalidInput", `page index out of range: ${index}`);
  }
  const gg = await ensureGg(retry != null);
  return { ...image(imageUrl(gg, String(hashes[index]))), format: "webp" };
}
