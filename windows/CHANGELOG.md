# Changelog — Windows app

All notable changes to this component. Format: [Keep a Changelog](https://keepachangelog.com/), SemVer tags `windows-vX.Y.Z`.

## [Unreleased]

## [0.4.0] - Unreleased

Requires contract 1.2 (host reports 1.2.1).

### Added

- Save to library (feature 006): per-source download queue through the source (page cache reused,
  retry hints, stall timeout, rate limits and sign-in requirements stop it), pause / resume / cancel,
  continues after a restart; Downloads screen with a count on the navigation item.
- One `.cbz` per saved gallery: entries stored, `ComicInfo.xml`, `hentai-reader.json` (archive format
  1); progress and reader settings written back by append-only updates (crash-safe, compacted).
- Library screen: covers from the archives, search by title, source and read filters, three sorts,
  open folder, delete to the Recycle Bin; several library folders with a target for new saves and
  move-or-keep on removal; hand-placed and foreign comic archives picked up within seconds.
- History screen: newest first with cover, progress and time; continues at the saved page with the
  gallery's own zoom, width mode and gaps (remembered per gallery, also online).
- Start page setting; default search per source (star button, Ctrl+click clears, listed in Settings);
  page cache folder with a background move that continues after a restart.
- Local database `library.db` (SQLite); the 0.3 `progress.json` is imported once.
- Moving a library folder handles duplicates: identical pages (same page fingerprint) drop the
  incoming copy to the Recycle Bin without asking; different pages under the same name or gallery
  ask: keep both, replace, or keep the existing one (with "the same for the rest").
- History covers for entries without one: from the saved copy or the page cache, and with the new
  setting "Load covers from the network" (off by default) through the source, one visible row at a
  time. A reader opened past the first page loads that page once for the cover.
- Reader: Left/Right glide across a page wider than the window, Shift+wheel and the tilt wheel too;
  zoom keeps the point under the pointer in place across as well.
- "Open folder" and "Show in folder" bring an Explorer window already showing the folder to the
  front (and select the file) instead of opening another.
- `Build-Installer.ps1 -Install`: installs the new build over the installed copy and starts it.

### Changed

- Downloads in parallel up to the source's declared request limit.
- Escape only navigates: back a level within the current section, and on its first page it closes
  the app without confirmation. Full screen is F11's alone and is restored at the next start.
- The reader's toolbar comes back only when the pointer really moves near the edge.

### Fixed

- A gallery opened past the first page drifted a few pages towards the start (the built-in scroll
  anchoring and the reader's own correction both applied).
- Long webtoons stopped showing pages (grey placeholders) until the app was left for a while: pages
  were decoded wider than the images themselves; stalled decodes no longer block later pages.
- Moving a library folder failed with "UNIQUE constraint failed" when the folder watcher had already
  listed the moved file.
- The History list showed empty rows.
- The maximized window state was not restored at start.
- Escape did nothing after leaving the reader until the window was clicked (focus was lost).
- Closing the search filters without a change reloaded the results.

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
