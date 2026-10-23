// @ts-check
// Smallest valid source: the conformance reference and the template for new sources.
// Copy this folder to sources/<id>/, rename the file to <id>.js and change manifest.id.

/** @typedef {import('@contract').SourceManifest} SourceManifest */
/** @typedef {import('@contract').SearchQuery} SearchQuery */
/** @typedef {import('@contract').SearchPage} SearchPage */
/** @typedef {import('@contract').GallerySummary} GallerySummary */
/** @typedef {import('@contract').Gallery} Gallery */
/** @typedef {import('@contract').PageImage} PageImage */
/** @typedef {import('@contract').RetryHint} RetryHint */

const BASE = "https://example.test";

/** @type {SourceManifest} */
export const manifest = {
  id: "minimal",
  name: "Minimal",
  version: "0.1.0",
  contract: "1.0",
  domains: ["example.test", "mirror.example.test"],
  politeness: { minIntervalMs: 0, maxConcurrency: 1 },
  login: "none",
  filters: [{ id: "sort", kind: "single", values: ["latest"], default: "latest" }],
  capabilities: { suggestTags: false },
};

/**
 * @param {string} url
 * @returns {Promise<string>}
 */
async function getHtml(url) {
  const res = await http.request({ url });
  if (res.status === 404) throw new SourceError("NotFound", `not found: ${url}`);
  if (res.status !== 200) throw new SourceError("SiteChanged", `unexpected status ${res.status} for ${url}`);
  return res.body;
}

/**
 * @param {SearchQuery} query
 * @param {string | null} cursor
 * @returns {Promise<SearchPage>}
 */
export async function search(query, cursor) {
  for (const id of Object.keys(query.filters)) {
    if (id !== "sort") throw new SourceError("InvalidInput", `unsupported filter: ${id}`);
  }
  const page = cursor === null ? 1 : Number(cursor);
  if (!Number.isInteger(page) || page < 1) throw new SourceError("InvalidInput", `bad cursor: ${cursor}`);
  const doc = await getHtml(`${BASE}/list?page=${page}`);

  /** @type {GallerySummary[]} */
  const items = html.select(doc, "li.g").map((li) => {
    const id = li.attr("data-id");
    const link = html.select(li, "a")[0];
    if (!id || !link) throw new SourceError("SiteChanged", "list item without id or link");
    const cover = html.select(li, "img")[0]?.attr("src");
    return {
      id,
      title: link.text(),
      cover: cover ? { url: `${BASE}${cover}` } : null,
      language: "en",
      type: "manga",
      pageCount: Number(li.attr("data-pages")),
      artists: [],
      tags: [],
      webUrl: `${BASE}/g/${id}`,
    };
  });
  const hasNext = html.select(doc, "a.next").length > 0;
  return { items, nextCursor: hasNext ? String(page + 1) : null };
}

/**
 * @param {string} id
 * @returns {Promise<Gallery>}
 */
export async function getGallery(id) {
  const doc = await getHtml(`${BASE}/g/${encodeURIComponent(id)}`);
  const title = html.select(doc, "h1")[0]?.text();
  const pages = html.select(doc, "img.page");
  if (!title || pages.length === 0) throw new SourceError("SiteChanged", "gallery page without title or pages");
  return {
    id,
    title,
    titleOriginal: null,
    cover: { url: `${BASE}${pages[0].attr("src")}` },
    language: "en",
    type: "manga",
    pageCount: pages.length,
    artists: [],
    tags: html.select(doc, "a.tag").map((a) => ({ namespace: a.attr("data-ns"), name: a.text() })),
    uploadedAt: null,
    rating: null,
    uploader: null,
    webUrl: `${BASE}/g/${id}`,
    pages: pages.map((img, index) => ({
      index,
      width: Number(img.attr("width")),
      height: Number(img.attr("height")),
      size: /** @type {const} */ ("exact"),
    })),
    sourceData: { files: pages.map((img) => img.attr("src")) },
  };
}

/**
 * @param {Gallery} gallery
 * @param {number} index
 * @param {RetryHint} [retry]
 * @returns {Promise<PageImage>}
 */
export async function getPageUrl(gallery, index, retry) {
  const files = /** @type {{ files: string[] }} */ (gallery.sourceData).files;
  if (!Number.isInteger(index) || index < 0 || index >= files.length) {
    throw new SourceError("InvalidInput", `page index out of range: ${index}`);
  }
  const mirror = retry ? "https://mirror.example.test" : BASE;
  return { url: `${mirror}${files[index]}`, headers: { referer: `${BASE}/` }, format: "webp" };
}
