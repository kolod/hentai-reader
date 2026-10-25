# Changelog — Windows app

All notable changes to this component. Format: [Keep a Changelog](https://keepachangelog.com/), SemVer tags `windows-vX.Y.Z`.

## [Unreleased]

## [0.3.0] - Unreleased

Requires contract 1.2 (host reports 1.2.0).

### Added

- Reader in the main window (feature 005): one strip of all pages at the column width, slots
  reserved from exact or estimated sizes with scroll anchoring, decoded at display width,
  preloading sized by pages and screen height, on-disk page cache with a size limit.
- Toolbar (auto-hides), page scrubber, go to page, zoom (Ctrl+wheel, Ctrl+Plus/Minus), fit width
  (W, default) or maximum reading width, optional gaps between pages (G; webtoons open without),
  smooth gliding on arrows, Page Up/Down and Space; borderless full screen on F11 everywhere.
- Reading progress per gallery with Continue / Read from start; a thumbnail click opens the reader
  at that page.
- Failed pages retry in place; a stalled image is requested again with a retry hint after 20 s
  (up to twice); rate limits stop the source until Retry, with the waiting time when known.
- Settings: reader (preload, fit width, reading width, gap, cache limit and Clear cache) and one
  section per source with its declared settings and, for E-Hentai, account state, image limit,
  sign-in in an embedded page (WebView2), Paste cookies and Sign out.
- Several languages at once in the search language filter.
- The window opens with its last size, position and maximized state.

### Fixed

- A cancelled source call (e.g. leaving a gallery while it loads) no longer crashes the app.
- Large Hitomi searches no longer fail with "engine memory limit exceeded": response bodies reach
  scripts as native byte arrays.
- No light frame around the content in the dark theme.

## [0.2.1] - Unreleased

### Fixed

- The window title bar follows the app's light or dark mode instead of always being light.
- Only one instance runs per Windows session: starting the app again brings the open window to
  the front (same `HentaiReader.Running` mutex the installer waits on). A language change restarts
  the app without being taken for a second instance.

## [0.2.0] - Unreleased

### Added

- Installer `HentaiReader-Setup-<v>-x64.exe` (Inno Setup 7.1.0, 45 MB): install for the current
  user (default, no administrator rights) or for all users; six installer languages following the
  Windows display language; adult content notice on the welcome page; in-place silent upgrades,
  downgrade refusal, waits for a running app; uninstall keeps `%LOCALAPPDATA%\HentaiReader` and says
  so.
- Portable ZIP of the same verified files (70 MB).
- `windows/build/Build-Installer.ps1`: one command for build, tests, publish, content verification
  (`windows/installer/distribution-rules.json`), installer, manifest check, ZIP and an optional
  install/upgrade/repair/downgrade/uninstall smoke test; CI job `installer`.
- App icon, `THIRD-PARTY-NOTICES.md`, single version source (`<Version>` in
  `windows/Directory.Build.props`, assemblies now report 0.2.0).

### Changed

- Distribution without debug symbols, `createdump.exe` and the unused Windows App SDK AI, ML, Search
  and Widgets components: 461 files / 176 MB instead of 527 files / 233 MB.

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
