# Implementation Plan: Windows Reader

**Branch**: `005-windows-reader` | **Date**: 2026-10-24 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/005-windows-reader/spec.md`

## Summary

A seamless vertical-strip reader shown in the same main window as browsing (Source → Gallery →
Reader in one frame, back restores everything). Pages form an `ItemsRepeater` strip with a custom
layout over pure geometry: each page exactly the column width, height from its own proportions,
estimated sizes refined from `getPageUrl` before pages become visible, and scroll anchoring so
corrections above the view never move it. A load planner keeps everything visible plus at least N
pages and one screen ahead decoded (so a portrait monitor with many landscape pages in view works),
releases the rest, and backs it with an LRU disk cache. Progress is saved per gallery; failures retry
in place; HTTP 429/509 on images now also stops the source. Settings gain reader options, stored
per-source settings and an account block; E-Hentai sign-in works by pasting cookies or through an
embedded login page described by a new optional manifest field (`loginPage`, contract 1.2). F11
gives borderless full screen everywhere. Research: [research.md](research.md).

## Technical Context

**Language/Version**: C# 14 / .NET 10, WinUI 3 (Windows App SDK 2.5.1); JavaScript source scripts;
TypeScript runner (Node 24, TS 6)

**Primary Dependencies**: existing (Jint, AngleSharp, CommunityToolkit.Mvvm); `WebView2` from the
Windows App SDK (no new package; WebView2 runtime from Windows)

**Storage**: JSON files in the per-user data folder (`preferences.json` extended, new
`progress.json`), file-based page cache with `index.json`; cookies unchanged (DPAPI)

**Testing**: xUnit v3 (pure reader logic, cache, progress, login cookie selection, image limit
handling), runner contract/conformance tests for 1.2, UI Automation scripts + screenshots, live
reading checks

**Target Platform**: Windows 10 1903+ / 11, x64

**Project Type**: desktop app (WinUI) + host library + runner/contract (contract 1.2)

**Performance Goals**: first page ≤ 2 s (SC-001); 95 % pages ready before visible (SC-002, SC-011);
back to search < 0.5 s (SC-004); input < 100 ms and memory < 1 GB on 500 pages (SC-008)

**Constraints**: no network, decoding or script work on the UI thread (IV); politeness and limit
stop for script and image requests (VI); no site knowledge in C# (I) → `loginPage` in the manifest;
cookies local, domain-scoped, never logged (VI, X)

**Scale/Scope**: ~12 new app files (page, layout, controls, services), ~6 host-library files, ~70
string keys × 6 languages, contract + runner + one source manifest change

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Constitution v1.0.3.

| # | Principle | Status | Evidence |
|---|-----------|--------|----------|
| I | Native UI, shared source logic | ✅ | WinUI only; login URL and cookie names come from the manifest (`loginPage`), not C# |
| II | Thin, sandboxed scripts; one contract | ✅ | Contract 1.2 (MINOR) in `.d.ts`, implemented by runner (validation) and Windows host in the same merge; scripts still only use host APIs |
| III | Verified updates | ➖ | Not affected |
| IV | Reading smoothness | ✅ | Seamless strip, slots from exact/estimated sizes refined before visibility (option A), decode at display width, preload ≥ N pages and ≥ 1 screen, all I/O and decoding off the UI thread |
| V | Open storage | ⚠️ justified | Progress in a JSON file, not yet a database; the library feature introduces the DB and imports it (Complexity Tracking). No archives yet |
| VI | Respect for sources | ✅ | Per-source limiter for images, image 429/509 → stop until Retry, cancellation of out-of-window requests, cache prevents refetching; cookies only for declared domains |
| VII | Testability | ✅ | Pure geometry/planner/cache/progress in the host library with unit tests; contract change with conformance sample; UI scripts |
| VIII | Simplicity | ✅ | No new packages; JSON files instead of a DB for two small maps; reuse of `ImageLoader` fetch path and `CookieJar` |
| IX | Localisation | ✅ | All new strings × 6 with plurals (page counts, sizes); layout review in de/ja |
| X | Version control | ✅ | Tags `contract-v1.2.0`, `sources-ehentai-v0.2.0`, `windows-v0.3.0`; CHANGELOGs; contract and both hosts in one merge |

## Project Structure

### Documentation (this feature)

```text
specs/005-windows-reader/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── contract-1.2-login-page.md   # manifest.loginPage, validation, E-Hentai values
│   └── reader-ui.md                 # navigation, keys, toolbar, slot states, settings
├── checklists/requirements.md
└── tasks.md                         # /speckit-tasks
```

### Source Code (repository root)

```text
contract/
├── host-api.d.ts                    # + LoginPage, SourceManifest.loginPage (1.2)
├── package.json / CHANGELOG.md / README.md   # 1.2.0
└── conformance/broken-login-page/   # url outside domains
tools/runner/src/
├── commands/check.ts / source.ts    # loginPage rules, host supports 1.2
sources/ehentai/ehentai.js           # contract "1.2", loginPage; version 0.2.0
windows/src/HentaiReader.Host/
├── HostInfo.cs                      # ContractVersion 1.2.0
├── Sources/ManifestRules.cs         # loginPage validation (shared with load)
├── Http/HttpHost.cs                 # image 429/509 → RateLimited (session Limited)
├── Reading/StripGeometry.cs         # widths, prefix sums, lookup, zoom anchoring
├── Reading/LoadPlanner.cs           # visible / ahead / behind window
├── Reading/LoginCookies.cs          # select + share captured cookies by loginPage
└── Storage/PageCache.cs, ProgressStore.cs
windows/src/HentaiReader.App/
├── Pages/ReaderPage.xaml(.cs)       # strip, toolbar, scrubber, keys, banner
├── Reader/StripLayout.cs            # VirtualizingLayout over StripGeometry
├── Reader/PageSlot.cs               # placeholder / image / error+retry
├── Reader/ReaderSession.cs          # page states, planner loop, cache, decoding
├── Services/FullScreen.cs           # F11 presenter switch and restore
├── Services/SourceSettingsStore.cs  # per-source settings in preferences
├── Dialogs/LoginDialog.cs           # WebView2 sign-in
├── Dialogs/PasteCookiesDialog.cs
├── Pages/GalleryPage.xaml(.cs)      # cached; Read / Continue / progress
├── Pages/SettingsPage.xaml(.cs)     # Reader + Sources sections, account block
├── MainWindow.xaml(.cs)             # window-level F11, back inputs, pane hiding
└── Strings/*/Resources.resw         # new keys × 6
windows/tests/HentaiReader.Host.Tests/
└── Reading/*, Storage/*, Http/ImageLimitTests.cs, Sources/ManifestRulesTests.cs
```

**Structure Decision**: logic that can be tested without UI (geometry, planner, cache, progress,
cookie selection, manifest rules) goes into `HentaiReader.Host` under `Reading/` and `Storage/`,
next to the existing host code, so the headless test project covers it; the app holds only views,
the session loop and platform glue. The contract change lands with runner and Windows host together.

## Post-design Constitution Re-check

Design keeps all rows: the only site-specific data (login URL, cookie names, shared domain) moved
into the manifest; image limits follow the constitution's HTTP rule; Principle V deviation stays
limited to the progress file and is scheduled for the library feature. ✅

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| Reading progress in `progress.json` instead of a local database (Principle V) | Online reading needs progress now; the database belongs to the library feature, which designs its schema with CBZ metadata | Adding a database now for one key→value map would fix a schema before the library requirements exist |
