# Changelog — Host API contract

All notable changes to this component. Format: [Keep a Changelog](https://keepachangelog.com/), SemVer tags `contract-vX.Y.Z`.

## [Unreleased]

## [1.2.0] - Unreleased

Backward compatible: sources declaring `1.0` or `1.1` run unchanged; 1.2 uses the 1.1 calling
convention.

### Added

- Optional `manifest.loginPage { url, cookies, shareWith? }` for interactive sign-in in a host's
  embedded browser, with the rule which captured cookies a host stores (only the source's domains,
  plus the sign-in cookies for each `shareWith` domain). Validated by `runner check` and on load in
  every host; conformance sample `broken-login-page`.
- The `language` filter may be declared `kind: "multi"`: `SearchQuery.filters.language` is then a
  list of codes and matches galleries in any of them. `single` stays valid; hosts already render
  both kinds generically.

## [1.1.1] - Unreleased

### Changed

- `SearchQuery.text`: documented convention that every source understands
  `<namespace>:<name>` with `_` for spaces (hosts build tag searches this way). Hitomi 0.1.0 and
  E-Hentai 0.1.0 already comply; no shape change.
- `host-glue.js` (moved from the runner) is shared by every host; not contract surface.

## [1.1.0] - Unreleased

Backward compatible: sources declaring `contract: "1.0"` run unchanged.

### Added

- Source settings: `manifest.settings` from a vocabulary (`image_quality`: `resampled` |
  `original`; `site`: `e-hentai` | `exhentai`); 1.1 sources receive the merged values as the last
  argument of every function, and `null` for an absent retry. 1.0 sources keep the 1.0 arity.
- Optional account status: `capabilities.accountStatus` + `getAccountStatus(settings)` →
  `AccountStatus` (`loggedIn`, optional `imageLimit`, optional `exhentai`).
- `ImageRequest.crop` for sprite-sheet thumbnails.
- `GalleryType` `asian_porn`.
- Normative rule: hosts fetch every `ImageRequest` with the source's cookie jar and through its
  politeness limiter.
- `tests/settings-passing`; `conformance/settings` (1.1 reference sample).

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
