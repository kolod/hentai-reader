// @ts-check
// Contract 1.1 reference: source settings, account status and sprite-cropped thumbnails.
// Makes no requests — it only shows the shapes and the calling convention.

/** @typedef {import('@contract').SettingValues} SettingValues */
/** @typedef {import('@contract').SearchQuery} SearchQuery */
/** @typedef {import('@contract').SearchPage} SearchPage */
/** @typedef {import('@contract').Gallery} Gallery */
/** @typedef {import('@contract').PageImage} PageImage */
/** @typedef {import('@contract').RetryHint} RetryHint */
/** @typedef {import('@contract').AccountStatus} AccountStatus */

const BASE = "https://example.test";

/** @type {import('@contract').SourceManifest} */
export const manifest = {
  id: "settings",
  name: "Settings",
  version: "0.1.0",
  contract: "1.1",
  domains: ["example.test", "*.example.test"],
  politeness: { minIntervalMs: 0, maxConcurrency: 1 },
  login: "optional",
  filters: [],
  settings: [
    { id: "image_quality", values: ["resampled", "original"], default: "resampled" },
    { id: "site", values: ["e-hentai", "exhentai"], default: "e-hentai" },
  ],
  capabilities: { suggestTags: false, accountStatus: true },
};

/**
 * @param {SearchQuery} query
 * @param {string | null} cursor
 * @param {SettingValues} settings
 * @returns {Promise<SearchPage>}
 */
export async function search(query, cursor, settings) {
  const id = settings.site === "exhentai" ? "x/1" : "e/1";
  return {
    items: [{ id, title: "One", cover: null, language: null, type: "asian_porn", pageCount: 2, artists: [], tags: [], webUrl: `${BASE}/g/1` }],
    nextCursor: null,
  };
}

/**
 * @param {string} id
 * @param {SettingValues} settings
 * @returns {Promise<Gallery>}
 */
export async function getGallery(id, settings) {
  const sprite = `${BASE}/sprite.webp`;
  return {
    id, title: "One", titleOriginal: null, cover: null, language: null, type: "asian_porn", pageCount: 2,
    artists: [], tags: [], uploadedAt: null, rating: null, uploader: null, webUrl: `${BASE}/g/1`,
    pages: [0, 1].map((index) => ({
      index,
      width: 200,
      height: 300,
      size: /** @type {const} */ ("estimated"),
      thumbnail: { url: sprite, crop: { x: index * 200, y: 0, width: 200, height: 300 } },
    })),
  };
}

/**
 * @param {Gallery} gallery
 * @param {number} index
 * @param {RetryHint | null} retry
 * @param {SettingValues} settings
 * @returns {Promise<PageImage>}
 */
export async function getPageUrl(gallery, index, retry, settings) {
  if (retry !== null) throw new SourceError("InvalidInput", "a 1.1 source receives null when there is no retry");
  const original = settings.image_quality === "original";
  return {
    url: `${BASE}/${original ? "original" : "resampled"}/${index}.webp`,
    width: original ? 2400 : 1280,
    height: original ? 3600 : 1920,
  };
}

/**
 * @param {SettingValues} settings
 * @returns {Promise<AccountStatus>}
 */
export async function getAccountStatus(settings) {
  return { loggedIn: false, exhentai: settings.site === "exhentai" ? false : null };
}
