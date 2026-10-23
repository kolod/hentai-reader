# Changelog — Host API contract

All notable changes to this component. Format: [Keep a Changelog](https://keepachangelog.com/), SemVer tags `contract-vX.Y.Z`.

## [Unreleased]

## [1.0.0] - Unreleased

### Added

- `host-api.d.ts`: host capabilities (`http.request`, `html.select`, `json.parse`, `storage`,
  `sleep`, `console`), `SourceError` kinds, source module exports (`manifest`, `search`,
  `getGallery`, `getPageUrl`, optional `suggestTags`) and all data shapes.
- `prelude.js`: shared `SourceError` definition evaluated by every host.
- Well-known filter vocabulary (`sort`, `language`, `type`, `min_rating`, `pages`).
- Per-page `size: "exact" | "estimated"` and `PageImage.width/height` refinement.
- `tests/`: portable host contract suite (13 tests).
- `conformance/`: minimal reference source and four single-defect samples.
