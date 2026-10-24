# Changelog — Windows app

All notable changes to this component. Format: [Keep a Changelog](https://keepachangelog.com/), SemVer tags `windows-vX.Y.Z`.

## [Unreleased]

## [0.1.0] - Unreleased

### Added

- Contract host (contract 1.1.1) on Jint 4 and AngleSharp: `http.request` with per-source cookie
  jar, domain allow-list, redirects and politeness limiter shared with image requests;
  `html.select` with the portable selector subset; `storage`, `sleep`, `console`; settings calling
  convention; memory, time and cancellation limits. Shares `contract/host-glue.js` with the runner.
- Parity: passes the contract test suite and replays every recorded Hitomi, E-Hentai and
  conformance case with results identical to the runner.
- WinUI 3 shell: source list, search with source-declared filters and endless results, gallery
  details with metadata, clickable tags and page thumbnails (sprite crop).
- UI in English, German, French, Spanish, Japanese and Ukrainian with CLDR plural forms;
  in-app language switch.
- Adult content gate before any data is stored or any request is sent.
- Portable self-contained x64 build (about 233 MB unpacked, 527 files).

### Fixed (during development)

- Byte-range requests are sent without `Accept-Encoding`; otherwise Hitomi answers a range of the
  gzip file and most result ids were lost.
- Result and thumbnail grids render through item templates (cards were created but not shown),
  and a page request made while another is loading shares it, so endless scrolling keeps going.
- Politeness interval measured with a high-resolution clock (the coarse tick could shorten gaps).
