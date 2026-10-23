# Changelog — E-Hentai source

All notable changes to this component. Format: [Keep a Changelog](https://keepachangelog.com/), SemVer tags `sources-ehentai-vX.Y.Z`.

## [Unreleased]

## [0.1.0] - Unreleased

Requires contract 1.1.

### Added

- Front page and search (site syntax, categories, numeric minimum rating, page range, language),
  forward cursors; toplists for popular day / month / year; tag suggestions.
- Card metadata from the site API: one listing request + one API request per result page.
- Galleries with every page's size estimated from its thumbnail cell (`estimated`) and a cropped
  sprite thumbnail; content warnings acknowledged automatically.
- Page images with the exact served size from the viewer page, retry through another server
  (`nl`), and original images for logged-in users (`image_quality: original`).
- `RateLimited` on HTTP 429/509, the limit placeholder image, the limit page and temporary IP bans
  (with the remaining ban time).
- Account status (logged in, ExHentai access; limit figures when the site shows them).
- ExHentai through the `site` setting; galleries keep the site they came from.

### Notes

- The site's minimum-rating filter uses rounded stars; results are re-checked against the exact
  rating, so a page can hold fewer than 25 items.
- Large galleries cost one request per 20 pages for `getGallery` (politeness 500 ms).
- Since 2026 E-Hentai uses mostly IP-based image limits and shows no quota figures unless a quota
  applies; `imageLimit` is then absent.
- Live check (SC-003), 2026-10-23: 10 galleries × 3 pages, 30/30 — estimates within 2 % aspect,
  exact sizes equal to the served images.
- Fixtures are rebuilt from only the page parts the script reads (`redact.mjs`); account-derived
  URL parameters of the ExHentai access check are redacted too. Logged-in and no-access cases use
  separate fixture pools.
