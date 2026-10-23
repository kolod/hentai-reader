// @ts-check
// E-Hentai / ExHentai source (contract 1.1). Site mechanics: specs/002-ehentai-source/research.md.
// Listings are read only for their gallery links and forward cursor (layout-independent); card and
// gallery metadata come from the site's JSON API; exact page sizes from each page's viewer page.

/** @typedef {import('@contract').SourceManifest} SourceManifest */
/** @typedef {import('@contract').SettingValues} SettingValues */
/** @typedef {import('@contract').SearchQuery} SearchQuery */
/** @typedef {import('@contract').SearchPage} SearchPage */
/** @typedef {import('@contract').GallerySummary} GallerySummary */
/** @typedef {import('@contract').Gallery} Gallery */
/** @typedef {import('@contract').GalleryType} GalleryType */
/** @typedef {import('@contract').Page} Page */
/** @typedef {import('@contract').PageImage} PageImage */
/** @typedef {import('@contract').RetryHint} RetryHint */
/** @typedef {import('@contract').Tag} Tag */
/** @typedef {import('@contract').TagSuggestion} TagSuggestion */
/** @typedef {import('@contract').AccountStatus} AccountStatus */
/** @typedef {"e" | "x"} Site */

const SITES = {
  e: { base: "https://e-hentai.org", api: "https://api.e-hentai.org/api.php" },
  x: { base: "https://exhentai.org", api: "https://s.exhentai.org/api.php" },
};
const GDATA_BATCH = 25;
const LOGIN_CACHE_MS = 10 * 60 * 1000;

/** Site category → contract type, with the category's bit in the f_cats exclusion mask. */
/** @type {Record<string, { type: GalleryType, bit: number }>} */
const CATEGORIES = {
  "Misc": { type: "misc", bit: 1 },
  "Doujinshi": { type: "doujinshi", bit: 2 },
  "Manga": { type: "manga", bit: 4 },
  "Artist CG": { type: "artist_cg", bit: 8 },
  "Game CG": { type: "game_cg", bit: 16 },
  "Image Set": { type: "image_set", bit: 32 },
  "Cosplay": { type: "cosplay", bit: 64 },
  "Asian Porn": { type: "asian_porn", bit: 128 },
  "Non-H": { type: "non_h", bit: 256 },
  "Western": { type: "western", bit: 512 },
};
/** @type {Record<string, number>} */
const BIT_BY_TYPE = Object.fromEntries(Object.values(CATEGORIES).map((c) => [c.type, c.bit]));

/** Site language tag ↔ ISO 639-1. */
/** @type {Record<string, string>} */
const LANGUAGES = {
  japanese: "ja", english: "en", chinese: "zh", korean: "ko", dutch: "nl", french: "fr",
  german: "de", hungarian: "hu", italian: "it", polish: "pl", portuguese: "pt", russian: "ru",
  spanish: "es", thai: "th", vietnamese: "vi", czech: "cs", indonesian: "id", ukrainian: "uk",
  turkish: "tr", arabic: "ar", greek: "el", finnish: "fi", swedish: "sv", tagalog: "tl",
};
/** @type {Record<string, string>} */
const LANGUAGE_BY_ISO = Object.fromEntries(Object.entries(LANGUAGES).map(([name, iso]) => [iso, name]));

/** Contract sort → toplist id (the site has no weekly toplist). */
/** @type {Record<string, number>} */
const TOPLISTS = { popular_day: 15, popular_month: 13, popular_year: 12 };

/** @type {SourceManifest} */
export const manifest = {
  id: "ehentai",
  name: "E-Hentai",
  version: "0.1.0",
  contract: "1.1",
  domains: [
    "e-hentai.org", "*.e-hentai.org", "exhentai.org", "*.exhentai.org",
    "ehgt.org", "*.ehgt.org", "*.hath.network",
  ],
  politeness: { minIntervalMs: 500, maxConcurrency: 2 },
  login: "optional",
  filters: [
    { id: "sort", kind: "single", values: ["latest", "popular_day", "popular_month", "popular_year"], default: "latest" },
    { id: "type", kind: "multi", values: Object.values(CATEGORIES).map((c) => c.type) },
    { id: "min_rating", kind: "number", min: 2, max: 5 },
    { id: "pages", kind: "range", min: 1, max: 2000 },
    { id: "language", kind: "single", values: Object.values(LANGUAGES) },
  ],
  settings: [
    { id: "image_quality", values: ["resampled", "original"], default: "resampled" },
    { id: "site", values: ["e-hentai", "exhentai"], default: "e-hentai" },
  ],
  capabilities: { suggestTags: true, accountStatus: true },
};

// --- HTTP and limits ---------------------------------------------------------------------------

/** @param {SettingValues | null | undefined} settings @returns {Site} */
function siteOf(settings) {
  return settings && settings.site === "exhentai" ? "x" : "e";
}

/**
 * @param {string} url
 * @param {{ method?: "GET" | "POST", body?: string, headers?: Record<string, string> }} [opts]
 */
async function request(url, opts = {}) {
  const res = await http.request({ url, method: opts.method, body: opts.body, headers: opts.headers });
  checkLimits(res);
  return res;
}

/** @param {{ status: number, url: string, headers: Record<string, string>, body: string }} res */
function checkLimits(res) {
  if (res.status === 429 || res.status === 509) {
    throw new SourceError("RateLimited", `E-Hentai limit (HTTP ${res.status}) at ${res.url}`, {
      retryAfterSeconds: secondsFromHeader(res.headers["retry-after"]),
    });
  }
  const body = res.body;
  if (/temporarily banned/i.test(body)) {
    throw new SourceError("RateLimited", "E-Hentai temporarily banned this IP address", {
      retryAfterSeconds: banSeconds(body),
    });
  }
  if (/exceeded your image viewing limits/i.test(body)) {
    throw new SourceError("RateLimited", "E-Hentai image viewing limit exceeded");
  }
}

/** @param {string | undefined} value */
function secondsFromHeader(value) {
  const n = Number(value);
  return value && Number.isFinite(n) ? Math.max(0, Math.round(n)) : undefined;
}

/** "The ban expires in 1 hour, 59 minutes and 12 seconds" → seconds. @param {string} text */
function banSeconds(text) {
  const m = /expires in ([^.<]+)/i.exec(text);
  if (!m) return undefined;
  /** @type {Record<string, number>} */
  const units = { day: 86400, hour: 3600, minute: 60, second: 1 };
  let total = 0;
  for (const part of m[1].matchAll(/(\d+)\s*(day|hour|minute|second)s?/gi)) {
    total += Number(part[1]) * units[part[2].toLowerCase()];
  }
  return total || undefined;
}

/** @param {string} what @param {{ status: number, url: string }} res */
function unexpected(what, res) {
  return new SourceError("SiteChanged", `${what}: unexpected HTTP ${res.status} at ${res.url}`);
}

/** @param {string} s */
function decodeEntities(s) {
  return s.replace(/&amp;/g, "&").replace(/&quot;/g, '"').replace(/&#039;/g, "'").replace(/&lt;/g, "<").replace(/&gt;/g, ">");
}

// --- ids and cursors ---------------------------------------------------------------------------

/** @param {Site} site @param {number | string} gid @param {string} token */
function galleryId(site, gid, token) {
  return `${site}/${gid}/${token}`;
}

/** @param {string} id */
function parseGalleryId(id) {
  const m = /^([ex])\/(\d+)\/([0-9a-f]{10})$/.exec(id);
  if (!m) throw new SourceError("InvalidInput", `not an E-Hentai gallery id: ${id}`);
  return { site: /** @type {Site} */ (m[1]), gid: Number(m[2]), token: m[3] };
}

/** @param {string | null} cursor */
function parseCursor(cursor) {
  if (cursor === null) return null;
  const m = /^(n|p):(\d+)$/.exec(cursor);
  if (!m) throw new SourceError("InvalidInput", `invalid cursor: ${cursor}`);
  return { kind: m[1], value: Number(m[2]) };
}

// --- metadata API ------------------------------------------------------------------------------

/**
 * @typedef {{
 *   gid: number, token: string, title: string, title_jpn: string, category: string, thumb: string,
 *   uploader?: string, posted: string, filecount: string, rating: string, tags: string[],
 *   error?: string
 * }} GData
 */

/** @param {Site} site @param {[number, string][]} list @returns {Promise<GData[]>} */
async function gdata(site, list) {
  /** @type {GData[]} */
  const out = [];
  for (let i = 0; i < list.length; i += GDATA_BATCH) {
    const res = await request(SITES[site].api, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ method: "gdata", gidlist: list.slice(i, i + GDATA_BATCH), namespace: 1 }),
    });
    if (res.status !== 200) throw unexpected("gdata", res);
    let parsed;
    try {
      parsed = /** @type {{ gmetadata?: GData[] }} */ (json.parse(res.body));
    } catch (e) {
      throw new SourceError("SiteChanged", "gdata: response is not JSON");
    }
    if (!Array.isArray(parsed.gmetadata)) throw new SourceError("SiteChanged", "gdata: no gmetadata");
    out.push(...parsed.gmetadata);
  }
  return out;
}

/** @param {string} raw @returns {Tag} */
function tag(raw) {
  const colon = raw.indexOf(":");
  return colon < 0 ? { namespace: "other", name: raw } : { namespace: raw.slice(0, colon), name: raw.slice(colon + 1) };
}

/** @param {Tag[]} tags */
function languageOf(tags) {
  for (const t of tags) {
    if (t.namespace === "language" && t.name !== "translated" && t.name !== "rewrite") {
      return LANGUAGES[t.name] ?? null;
    }
  }
  return null;
}

/** @param {Site} site @param {GData} g @returns {GallerySummary} */
function summary(site, g) {
  const tags = (g.tags ?? []).map(tag);
  return {
    id: galleryId(site, g.gid, g.token),
    title: g.title || g.title_jpn || `Gallery ${g.gid}`,
    cover: g.thumb ? { url: g.thumb } : null,
    language: languageOf(tags),
    type: CATEGORIES[g.category]?.type ?? null,
    pageCount: Number(g.filecount),
    artists: tags.filter((t) => t.namespace === "artist").map((t) => t.name),
    tags: tags.filter((t) => t.namespace !== "artist" && t.namespace !== "language").slice(0, 20),
    webUrl: `${SITES[site].base}/g/${g.gid}/${g.token}/`,
  };
}

// --- search ------------------------------------------------------------------------------------

/** @param {SearchQuery} query */
function checkFilters(query) {
  const f = /** @type {Record<string, unknown>} */ (query.filters ?? {});
  for (const id of Object.keys(f)) {
    if (!["sort", "type", "min_rating", "pages", "language"].includes(id)) {
      throw new SourceError("InvalidInput", `unsupported filter: ${id}`);
    }
  }
  const sort = f.sort ?? "latest";
  if (sort !== "latest" && !(typeof sort === "string" && sort in TOPLISTS)) {
    throw new SourceError("InvalidInput", `unsupported sort: ${String(sort)}`);
  }
  if (f.type !== undefined) {
    if (!Array.isArray(f.type) || f.type.some((t) => !(t in BIT_BY_TYPE))) {
      throw new SourceError("InvalidInput", `unsupported type filter: ${JSON.stringify(f.type)}`);
    }
  }
  if (f.min_rating !== undefined && !(Number.isInteger(f.min_rating) && Number(f.min_rating) >= 2 && Number(f.min_rating) <= 5)) {
    throw new SourceError("InvalidInput", `min_rating must be 2–5: ${String(f.min_rating)}`);
  }
  if (f.language !== undefined && !(typeof f.language === "string" && f.language in LANGUAGE_BY_ISO)) {
    throw new SourceError("InvalidInput", `unsupported language: ${String(f.language)}`);
  }
  return /** @type {{ sort: string, type?: string[], min_rating?: number, pages?: { min?: number, max?: number }, language?: string }} */ ({ ...f, sort });
}

/**
 * @param {Site} site
 * @param {SearchQuery} query
 * @param {ReturnType<typeof checkFilters>} f
 * @param {number | null} next
 */
function listingUrl(site, query, f, next) {
  /** @type {string[]} */
  const params = [];
  let text = (query.text ?? "").trim();
  if (f.language) text = `${text} language:${LANGUAGE_BY_ISO[f.language]}$`.trim();
  if (text) params.push(`f_search=${encodeURIComponent(text)}`);
  if (f.type && f.type.length) {
    params.push(`f_cats=${1023 - f.type.reduce((sum, t) => sum + BIT_BY_TYPE[t], 0)}`);
  }
  if (f.min_rating) params.push(`f_srdd=${f.min_rating}`);
  if (f.pages && f.pages.min) params.push(`f_spf=${f.pages.min}`);
  if (f.pages && f.pages.max) params.push(`f_spt=${f.pages.max}`);
  if (next !== null) params.push(`next=${next}`);
  return `${SITES[site].base}/${params.length ? `?${params.join("&")}` : ""}`;
}

/** Gallery references in a listing, in order, from the gallery table only. @param {string} doc */
function listingGalleries(doc) {
  /** @type {[number, string][]} */
  const out = [];
  const seen = new Set();
  for (const a of html.select(doc, ".itg a[href]")) {
    const m = /\/g\/(\d+)\/([0-9a-f]{10})\//.exec(a.attr("href") ?? "");
    if (!m || seen.has(m[1])) continue;
    seen.add(m[1]);
    out.push([Number(m[1]), m[2]]);
  }
  return out;
}

/** @param {Site} site @param {string} doc */
function requireListing(site, doc) {
  if (html.select(doc, ".itg").length > 0) return;
  if (site === "x") throw new SourceError("LoginRequired", "ExHentai access is missing for this login");
  throw new SourceError("SiteChanged", "listing page without a gallery table");
}

/**
 * @param {SearchQuery} query
 * @param {string | null} cursor
 * @param {SettingValues} settings
 * @returns {Promise<SearchPage>}
 */
export async function search(query, cursor, settings) {
  const site = siteOf(settings);
  const f = checkFilters(query);
  const c = parseCursor(cursor);

  if (f.sort !== "latest") {
    const others = Object.keys(query.filters ?? {}).filter((k) => k !== "sort");
    if ((query.text ?? "").trim() || others.length) {
      throw new SourceError("InvalidInput", "popular lists cannot be combined with text or other filters");
    }
    if (c && c.kind !== "p") throw new SourceError("InvalidInput", `cursor ${cursor} does not belong to a popular list`);
    const page = c ? c.value : 0;
    const res = await request(`${SITES[site].base}/toplist.php?tl=${TOPLISTS[f.sort]}${page ? `&p=${page}` : ""}`);
    if (res.status !== 200) throw unexpected("toplist", res);
    requireListing(site, res.body);
    const refs = listingGalleries(res.body);
    const hasNext = new RegExp(`[?&](?:amp;)?p=${page + 1}\\b`).test(res.body);
    const items = (await gdata(site, refs)).filter((g) => !g.error).map((g) => summary(site, g));
    return { items, nextCursor: hasNext && refs.length ? `p:${page + 1}` : null };
  }

  if (c && c.kind !== "n") throw new SourceError("InvalidInput", `cursor ${cursor} does not belong to a search`);
  const res = await request(listingUrl(site, query, f, c ? c.value : null));
  if (res.status !== 200) throw unexpected("listing", res);
  if (/No hits found/.test(res.body)) return { items: [], nextCursor: null };
  requireListing(site, res.body);
  const refs = listingGalleries(res.body);
  const next = html.select(res.body, "a#unext")[0]?.attr("href") ?? "";
  const m = /[?&]next=(\d+)/.exec(decodeEntities(next));
  // The site's minimum-rating filter works on rounded stars (3.93 passes "4"); the contract's
  // min_rating is numeric, so results are re-checked against the exact rating.
  const min = f.min_rating ?? 0;
  const items = refs.length
    ? (await gdata(site, refs)).filter((g) => !g.error && Number(g.rating) >= min).map((g) => summary(site, g))
    : [];
  return { items, nextCursor: m ? `n:${m[1]}` : null };
}

/**
 * @param {string} prefix
 * @param {SettingValues} settings
 * @returns {Promise<TagSuggestion[]>}
 */
export async function suggestTags(prefix, settings) {
  const text = prefix.trim();
  if (text.length < 2) return [];
  const res = await request(SITES[siteOf(settings)].api, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ method: "tagsuggest", text }),
  });
  if (res.status !== 200) throw unexpected("tagsuggest", res);
  const parsed = /** @type {{ tags?: Record<string, { ns?: string, tn: string, mns?: string, mtn?: string }> | [] }} */ (json.parse(res.body));
  /** @type {TagSuggestion[]} */
  const out = [];
  const seen = new Set();
  for (const t of Object.values(parsed.tags ?? {})) {
    const namespace = t.mtn ? t.mns ?? null : t.ns ?? null;
    const name = t.mtn ?? t.tn;
    const key = `${namespace}:${name}`;
    if (seen.has(key)) continue;
    seen.add(key);
    out.push({ namespace, name, count: null });
  }
  return out;
}

// --- gallery -----------------------------------------------------------------------------------

/** @param {Site} site @param {number} gid @param {string} token @param {number} p */
async function galleryPage(site, gid, token, p) {
  const url = `${SITES[site].base}/g/${gid}/${token}/${p ? `?p=${p}` : ""}`;
  let res = await request(url);
  if (res.status === 404 || /Gallery Not Available|This gallery has been removed|Key missing, or incorrect key provided/i.test(res.body)) {
    throw new SourceError("NotFound", `gallery ${gid} is not available`);
  }
  if (res.status === 200 && /nw=always/.test(res.body) && html.select(res.body, "#gdt").length === 0) {
    // Content warning: acknowledge it for the user (the site remembers it in a cookie).
    res = await request(`${url}${p ? "&" : "?"}nw=always`);
  }
  if (res.status !== 200) throw unexpected("gallery page", res);
  if (html.select(res.body, "#gdt").length === 0) {
    if (site === "x" && !/<html/i.test(res.body)) throw new SourceError("LoginRequired", "ExHentai access is missing for this login");
    throw new SourceError("SiteChanged", `gallery ${gid}: thumbnail grid not found`);
  }
  return res.body;
}

/**
 * Thumbnail cells: link (viewer token, page number) and sprite cell (size, offset). Links and
 * styled cells appear in the same order in every thumbnail mode.
 * @param {string} doc @param {number} gid
 */
function thumbnailCells(doc, gid) {
  const links = html.select(doc, "#gdt a[href]").filter((a) => /\/s\/[0-9a-f]+\/\d+-\d+/.test(a.attr("href") ?? ""));
  const cells = html.select(doc, "#gdt div[style*='background']");
  if (links.length !== cells.length) {
    throw new SourceError("SiteChanged", `gallery ${gid}: ${links.length} page links but ${cells.length} thumbnails`);
  }
  return links.map((a, i) => {
    const href = /\/s\/([0-9a-f]+)\/(\d+)-(\d+)/.exec(a.attr("href") ?? "");
    const style = cells[i].attr("style") ?? "";
    const size = /width:\s*(\d+)px;\s*height:\s*(\d+)px/.exec(style);
    const bg = /url\(([^)]+)\)\s*(-?\d+)(?:px)?\s+(-?\d+)(?:px)?/.exec(style);
    if (!href || Number(href[2]) !== gid) throw new SourceError("SiteChanged", `gallery ${gid}: bad page link`);
    const title = cells[i].attr("title") ?? "";
    return {
      token: href[1],
      page: Number(href[3]),
      width: size ? Number(size[1]) : 0,
      height: size ? Number(size[2]) : 0,
      sprite: bg ? bg[1].replace(/^['"]|['"]$/g, "") : null,
      x: bg ? -Number(bg[2]) : 0,
      y: bg ? -Number(bg[3]) : 0,
      fileName: /^Page \d+:\s*(.+)$/.exec(title)?.[1],
    };
  });
}

/** Highest `?p=` in the thumbnail pager (0 when the gallery fits on one page). @param {string} doc */
function lastThumbnailPage(doc) {
  let last = 0;
  for (const a of html.select(doc, ".ptt a[href]")) {
    const m = /[?&]p=(\d+)/.exec(decodeEntities(a.attr("href") ?? ""));
    if (m) last = Math.max(last, Number(m[1]));
  }
  return last;
}

/**
 * @param {string} id
 * @param {SettingValues} settings
 * @returns {Promise<Gallery>}
 */
export async function getGallery(id, settings) {
  const { site, gid, token } = parseGalleryId(id);
  const [g] = await gdata(site, [[gid, token]]);
  if (!g || g.error) throw new SourceError("NotFound", `gallery ${gid}: ${g ? g.error : "no metadata"}`);
  const pageCount = Number(g.filecount);

  const first = await galleryPage(site, gid, token, 0);
  const cells = thumbnailCells(first, gid);
  const last = lastThumbnailPage(first);
  for (let p = 1; p <= last && cells.length < pageCount; p++) {
    cells.push(...thumbnailCells(await galleryPage(site, gid, token, p), gid));
  }
  cells.sort((a, b) => a.page - b.page);
  if (cells.length < pageCount) {
    throw new SourceError("SiteChanged", `gallery ${gid}: found ${cells.length} of ${pageCount} pages`);
  }
  const pages = cells.slice(0, pageCount);

  const sized = pages.filter((c) => c.width > 0 && c.height > 0).map((c) => c.height / c.width).sort((a, b) => a - b);
  const medianAspect = sized.length ? sized[sized.length >> 1] : 1.42;
  const tags = (g.tags ?? []).map(tag);
  return {
    ...summary(site, g),
    titleOriginal: g.title_jpn || null,
    tags,
    uploadedAt: new Date(Number(g.posted) * 1000).toISOString(),
    rating: Number(g.rating),
    uploader: g.uploader ?? null,
    pages: pages.map((c, index) => {
      const width = c.width > 0 && c.height > 0 ? c.width : 200;
      const height = c.width > 0 && c.height > 0 ? c.height : Math.round(200 * medianAspect);
      /** @type {Page} */
      const page = { index, width, height, size: "estimated" };
      if (c.sprite) page.thumbnail = { url: c.sprite, crop: { x: c.x, y: c.y, width: c.width, height: c.height } };
      if (c.fileName) page.fileName = c.fileName;
      return page;
    }),
    sourceData: { pages: pages.map((c) => [c.token, c.page]) },
  };
}

// --- pages -------------------------------------------------------------------------------------

/** @param {string} url */
function formatOf(url) {
  const ext = /\.([a-z0-9]+)(?:$|[?#])/i.exec(url)?.[1]?.toLowerCase();
  if (ext === "webp" || ext === "avif" || ext === "png" || ext === "gif") return ext;
  if (ext === "jpg" || ext === "jpeg") return "jpeg";
  return undefined;
}

/**
 * @param {Gallery} gallery
 * @param {number} index
 * @param {RetryHint | null} retry
 * @param {SettingValues} settings
 * @returns {Promise<PageImage>}
 */
export async function getPageUrl(gallery, index, retry, settings) {
  const { site, gid } = parseGalleryId(gallery.id);
  const refs = /** @type {{ pages?: [string, number][] }} */ (gallery.sourceData ?? {}).pages;
  if (!Array.isArray(refs)) throw new SourceError("InvalidInput", "gallery.sourceData.pages missing — pass a gallery from getGallery");
  if (!Number.isInteger(index) || index < 0 || index >= refs.length) {
    throw new SourceError("InvalidInput", `page index out of range: ${index}`);
  }
  const original = settings && settings.image_quality === "original";
  if (original && !(await loggedIn())) {
    throw new SourceError("LoginRequired", "original images require an E-Hentai login");
  }
  const [token, page] = refs[index];
  const nl = retry && retry.previous && retry.previous.retryToken;
  const res = await request(`${SITES[site].base}/s/${token}/${gid}-${page}${nl ? `?nl=${encodeURIComponent(nl)}` : ""}`);
  if (res.status !== 200) throw unexpected("viewer page", res);

  const img = html.select(res.body, "img#img")[0];
  const src = img?.attr("src");
  if (!src) throw new SourceError("SiteChanged", `gallery ${gid} page ${page}: image not found`);
  if (/\/509\.(gif|webp|png)$/.test(src)) throw new SourceError("RateLimited", "E-Hentai image viewing limit reached");
  const retryToken = /nl\('([^']+)'\)/.exec(res.body)?.[1];

  if (original) {
    const link = /<a href="([^"]+\/fullimg\/[^"]+)">Download original (\d+) x (\d+)/.exec(res.body);
    if (!link) throw new SourceError("LoginRequired", "the site offers no original download for this login");
    const url = decodeEntities(link[1]);
    return withToken({ url, width: Number(link[2]), height: Number(link[3]), format: formatOf(url) }, retryToken);
  }
  const size = /<div>[^<]*? :: (\d+) x (\d+) :: [^<]*<\/div>/.exec(res.body);
  if (!size) throw new SourceError("SiteChanged", `gallery ${gid} page ${page}: image size not found`);
  return withToken({ url: src, width: Number(size[1]), height: Number(size[2]), format: formatOf(src) }, retryToken);
}

/** @param {PageImage} image @param {string | undefined} token @returns {PageImage} */
function withToken(image, token) {
  if (!image.format) delete image.format;
  return token ? { ...image, retryToken: token } : image;
}

// --- account -----------------------------------------------------------------------------------

/** @type {{ value: boolean, at: number } | null} */
let loginCache = null;

async function loggedIn() {
  if (loginCache && Date.now() - loginCache.at < LOGIN_CACHE_MS) return loginCache.value;
  return (await homePage()).loggedIn;
}

/**
 * Anonymous visitors are redirected to the login page; members get the home page with its
 * "Image Limits" box.
 */
async function homePage() {
  const res = await request("https://e-hentai.org/home.php");
  const isLoggedIn = res.status === 200 && !/bounce_login/.test(res.url) && /Image Limits/.test(res.body);
  loginCache = { value: isLoggedIn, at: Date.now() };
  return { loggedIn: isLoggedIn, body: res.body };
}

/**
 * @param {SettingValues} settings
 * @returns {Promise<AccountStatus>}
 */
export async function getAccountStatus(settings) {
  const home = await homePage();
  if (!home.loggedIn) return { loggedIn: false };
  // Since 2026 limits are mostly IP-based and the box shows figures only when a quota applies,
  // so imageLimit is reported only when the site states numbers.
  /** @type {AccountStatus} */
  const status = { loggedIn: true };
  const limit = /currently at <strong>([\d,]+)<\/strong> towards (?:your|a) limit of <strong>([\d,]+)<\/strong>/.exec(home.body);
  if (limit) {
    status.imageLimit = { used: Number(limit[1].replace(/,/g, "")), max: Number(limit[2].replace(/,/g, "")), resetsAt: null };
  }
  const ex = await http.request({ url: "https://exhentai.org/" });
  status.exhentai = ex.status === 200 && html.select(ex.body, ".itg").length > 0;
  return status;
}
