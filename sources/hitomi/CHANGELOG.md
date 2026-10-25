# Changelog — Hitomi.la source

All notable changes to this component. Format: [Keep a Changelog](https://keepachangelog.com/), SemVer tags `sources-hitomi-vX.Y.Z`.

## [Unreleased]

## [0.2.0] - Unreleased

Requires contract 1.2.

### Added

- Several languages at once: the `language` filter is `multi` (contract 1.2). The site has no
  "any of" search, so the feed in several languages reads the all-language list in Range chunks
  and keeps galleries from any requested language list (site order kept, exact total); with tags
  or words the language lists filter the result. A single code is still accepted.

### Fixed

- With the 1.1+ calling convention an absent retry arrives as `null`; it no longer forces a gg.js
  refresh on every page.

## [0.1.0] - Unreleased

Requires contract 1.0.

### Added

- Front feed (latest) and popular feeds (today, week, month, year), paged with HTTP Range reads.
- Tag search (`female:`, `male:`, `tag:`/`other:`, `artist:`, `group:`, `series:`/`parody:`,
  `character:`, `type:`, `language:`), exclusions with `-`, free-text search via the site's
  galleries index, language and gallery-type filters.
- Tag suggestions from the site's tag index.
- Gallery metadata with per-page sizes and thumbnails; WebP page image requests computed with the
  site's own `gg.js` (cached 10 min, refreshed on retry).

### Notes

- Pages whose originals exceed 2880 px on the long side are served downscaled by the site; their
  size is reported as `estimated` (≤ 1 px error). Live check (SC-006), 2026-10-23: 3 runs × 60
  pages, all within tolerance; all image requests loaded.
