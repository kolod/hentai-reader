# Changelog — Windows app

All notable changes to this component. Format: [Keep a Changelog](https://keepachangelog.com/), SemVer tags `windows-vX.Y.Z`.

## [Unreleased]

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
