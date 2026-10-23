// @ts-check
/** @typedef {import("@contract").Gallery} Gallery */
/** @typedef {import("@contract").PageImage} PageImage */
/** @typedef {import("@contract").SearchPage} SearchPage */
/** @typedef {import("@contract").SearchQuery} SearchQuery */
/** @typedef {import("@contract").RetryHint} RetryHint */
// Defect: getGallery returns a page without height.
/** @type {import("@contract").SourceManifest} */
export const manifest = {
  id: "broken-wrong-shape", name: "Broken", version: "0.1.0", contract: "1.0",
  domains: ["example.test"], politeness: { minIntervalMs: 0, maxConcurrency: 1 },
  login: "none", filters: [], capabilities: { suggestTags: false },
};
/**
 * @param {SearchQuery} query
 * @param {string | null} cursor
 * @returns {Promise<SearchPage>}
 */
export async function search(query, cursor) {
  return { items: [], nextCursor: null };
}
/**
 * @param {string} id
 * @returns {Promise<Gallery>}
 */
export async function getGallery(id) {
  return {
    id, title: "T", titleOriginal: null, cover: null, language: null, type: null, pageCount: 1,
    artists: [], tags: [], uploadedAt: null, rating: null, uploader: null, webUrl: "https://example.test/",
    pages: [{ index: 0, width: 100, size: "exact" }],
  };
}
/**
 * @param {Gallery} gallery
 * @param {number} index
 * @param {RetryHint} [retry]
 * @returns {Promise<PageImage>}
 */
export async function getPageUrl(gallery, index, retry) {
  return { url: "https://example.test/1.webp" };
}
