# Implementation Plan: Windows App Shell and Source Host

**Branch**: `003-windows-host` | **Date**: 2026-10-23 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/003-windows-host/spec.md`

## Summary

A UI-free C# host library runs source scripts in Jint with the **same `host-glue.js`** as the
runner (moved to `contract/`), implementing only the small private bridge (HTTP with cookie jar and
politeness, AngleSharp HTML, persisted storage, sleep, log). A headless xUnit parity suite replays
the contract tests and every recorded source case and requires results deep-equal to the runner's
snapshots. On top, a WinUI 3 app (unpackaged, self-contained, portable) offers the source list,
search with manifest-declared filters and endless results, a gallery screen with sprite-cropped
thumbnails, an adult-content gate, and six UI languages with ICU plural rules. Research:
[research.md](research.md).

## Technical Context

**Language/Version**: C# 14 on .NET 10 (LTS); JavaScript sources unchanged

**Primary Dependencies**: Jint 4 (script engine), AngleSharp (HTML), Microsoft.WindowsAppSDK
(WinUI 3), CommunityToolkit.Mvvm; tests: xUnit

**Storage**: JSON files under `%LOCALAPPDATA%\HentaiReader\` (preferences, per-source storage),
DPAPI-encrypted per-source cookie file; images in memory only

**Testing**: `dotnet test` (unit + parity + resource keys); runner suites unchanged

**Target Platform**: Windows 10 1903+ / Windows 11, x64

**Project Type**: desktop app + class library + test project

**Performance Goals**: source list < 2 s from start (SC-002); results ≤ 5 s (SC-003); input
response < 100 ms during loads, smooth scrolling of 500 results (SC-004)

**Constraints**: scripts/network/decoding off the UI thread (Constitution IV); politeness and
cookie scoping for scripts **and** images (VI); no source request before adult confirmation;
CLR interop disabled in the engine

**Scale/Scope**: ~25 host classes, 3–4 pages, ~120 string keys × 6 languages, ~70 parity tests

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Constitution v1.0.3.

| # | Principle | Status | Evidence |
|---|-----------|--------|----------|
| I | Native UI, shared source logic | ✅ | WinUI 3 (1.0.3); no site logic in C# — the app only calls source functions |
| II | Thin, sandboxed scripts | ✅ | Jint with CLR interop off; only contract globals via shared `host-glue.js`; AngleSharp parses HTML; host implements contract 1.1 |
| III | Verified updates | ✅ (partial) | Built-in copy of all scripts shipped (fallback required by III); remote updates are feature 007 |
| IV | Reading smoothness | ✅ | Engine on worker threads, async I/O, images decoded at display size off the UI thread |
| V | Open storage | ➖ | No archives yet (005) |
| VI | Respect for sources | ✅ | Per-source scheduler shared by scripts and images; `Limited` state stops requests after `RateLimited`; cookies domain-scoped, local, DPAPI-protected |
| VII | Testability | ✅ | Headless parity suite = same contract tests and source cases as the runner; Windows CI job |
| VIII | Simplicity | ✅ | One extra dependency beyond the constitution's stack (CommunityToolkit.Mvvm); shared glue avoids a second implementation of the contract globals |
| IX | Localisation | ✅ | `.resw` × 6, English keys as truth, key-parity test, ICU plurals via the OS (platform mechanism), language override in settings, vocabulary keys (contracts/l10n-keys.md) |
| X | Version control | ✅ | `/windows` layout; `.gitignore` already covers .NET; tags `windows-vX.Y.Z` from the first release; glue move lands with both hosts in one merge |

**Gate result**: PASS (pre-research and post-design). No Complexity Tracking entries.

## Project Structure

### Documentation (this feature)

```text
specs/003-windows-host/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── host-bridge.md         # protocol behind contract/host-glue.js (all hosts)
│   ├── parity-suite.md        # what dotnet test proves
│   └── l10n-keys.md           # string key conventions shared with Android
├── checklists/requirements.md
└── tasks.md
```

### Source Code (repository root)

```text
contract/
└── host-glue.js                     # moved from tools/runner/src/glue.js (runner path updated)

windows/
├── HentaiReader.slnx
├── Directory.Build.props            # net10.0, nullable, warnings as errors, LangVersion
├── CHANGELOG.md                     # exists
├── src/
│   ├── HentaiReader.Host/           # class library (no UI)
│   │   ├── Engine/                  # SourceEngine (Jint, worker thread, pump), Bridge, CallArgs
│   │   ├── Http/                    # HttpHost, CookieJar, Scheduler, LiveTransport
│   │   ├── Html/                    # HtmlHost (AngleSharp, handles, pre-check)
│   │   ├── Storage/                 # FileStorage, MemoryStorage
│   │   ├── Fixtures/                # OfflineTransport (pools, echo, delayMs)
│   │   ├── Sources/                 # SourceBundle, SourceCatalog, SourceSession
│   │   └── Contract/                # embedded prelude.js + host-glue.js (linked from /contract)
│   └── HentaiReader.App/            # WinUI 3, unpackaged, self-contained
│       ├── App.xaml(.cs)            # language override, adult gate, DI
│       ├── MainWindow.xaml          # NavigationView + Frame
│       ├── Pages/                   # SourcePage, GalleryPage, SettingsPage
│       ├── ViewModels/
│       ├── Controls/                # SpriteThumbnail, ResultCard, FilterPanel
│       ├── Images/                  # ImageLoader (session HTTP, LRU)
│       ├── Localization/            # Strings (ResourceLoader + ICU plurals), Plural (icu.dll)
│       └── Strings/<tag>/Resources.resw   # en-US, de-DE, fr-FR, es-ES, ja-JP, uk-UA
└── tests/
    └── HentaiReader.Host.Tests/     # unit, Contract.*, Case.*, Resources.*

.github/workflows/ci.yml             # + windows job
```

**Structure Decision**: Host library separate from the WinUI app so parity runs headless; the
bundled scripts, contract prelude, shared glue and fixtures are linked from their repository
locations at build time (no copies in `/windows`).
