# Implementation Plan: Windows Library and History

**Branch**: `006-windows-library` | **Date**: 2026-10-25 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/006-windows-library/spec.md`

## Summary

Galleries can be saved to a library and read offline in the 005 reader; every gallery opened is
kept in a history with its progress and its own zoom, width mode and gaps. A local SQLite database
(the one Constitution V asks for and 005 deferred) holds library folders, saved archives, history,
progress, per-gallery reader settings and downloads; `progress.json` is imported once. Saving runs
a per-source download queue through the existing `PageImageLoader` (politeness, retry hints, stall
timeout, limits stop it), reuses the page cache, stages pages on the target volume and assembles one
`.cbz` (all entries STORED) with `ComicInfo.xml` for other readers and `hentai-reader.json` (archive
format 1, schema in `contract/archive/`). Progress and reader settings are written back into the
archive by append-only updates, which are fast and crash-safe. Library folders are scanned and
watched; archives placed by hand are picked up, foreign comic archives identified by content. The
reader gains an archive page provider. Settings add start page, library folders, cache folder move
in the background, and per-source default queries behind a star button. Research:
[research.md](research.md).

## Technical Context

**Language/Version**: C# 14 / .NET 10, WinUI 3 (Windows App SDK 2.5.1)

**Primary Dependencies**: existing (Jint, AngleSharp, CommunityToolkit.Mvvm, WebView2);
**new**: `Microsoft.Data.Sqlite` 10.0.x (with `SQLitePCLRaw.bundle_e_sqlite3`); framework only:
`System.IO.Compression`, `Microsoft.VisualBasic.FileIO` (Recycle Bin), Windows App SDK
`FolderPicker`

**Storage**: `library.db` (SQLite, `user_version` migrations) + `preferences.json` additions +
`covers\` thumbnails in the data folder; `.cbz` archives in user-chosen library folders with
staging under `<folder>\.hentai-reader\partial\`; page cache folder configurable

**Testing**: xUnit v3 in `HentaiReader.Host.Tests` (database, archive writer/updater incl. crash
cases, scanner, download queue with fake transport, cache move); JSON Schema validation of written
metadata; UI Automation checks with the screen marker; manual offline reading and third-party reader
check

**Target Platform**: Windows 10 1903+ / 11, x64

**Project Type**: desktop app (WinUI) + host library; cross-platform archive format document

**Performance Goals**: offline first page ≤ 1 s (SC-001); cached 100-page save ≤ 10 s (SC-002);
library of 1 000 ≤ 3 s, history of 5 000 ≤ 1 s (SC-005, SC-006); hand-placed archive visible ≤ 5 s
(SC-008)

**Constraints**: no file, database or network work on the UI thread (IV); downloads obey the
source's politeness and stop on limits (VI); nothing written before the adult confirmation; no site
knowledge in C# (I) — downloads only call the contract functions; archives self-contained and
identical across platforms (V)

**Scale/Scope**: ~15 new host-library files (database, archive, library, downloads), ~10 app files
(3 pages, view models, settings sections, providers), ~80 string keys × 6, one schema + document in
`contract/archive/`, distribution rules for the SQLite files

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Constitution v1.1.0.

| # | Principle | Status | Evidence |
|---|-----------|--------|----------|
| I | Native UI, shared source logic | ✅ | WinUI screens; downloads use only `getGallery` / `getPageUrl` through the session — no site URLs in C# |
| II | Thin, sandboxed scripts; one contract | ✅ | No host API change; scripts untouched |
| III | Verified updates | ➖ | Not affected |
| IV | Reading smoothness | ✅ | Archive pages have exact sizes from metadata or measured headers before decoding; bytes read off the UI thread; same strip and planner |
| V | Open storage | ✅ | One `.cbz` per gallery, STORED, `ComicInfo.xml` + `hentai-reader.json` inside; metadata in the local database **and** the archive; format 1 documented and schema-published for Android; WebP kept as delivered. Closes 005's justified deviation (progress now in the database) |
| VI | Respect for sources | ✅ | Downloads through `PageImageLoader` and the source limiter, one worker per source, limits stop the download until the user resumes, page cache reused to avoid refetching |
| VII | Testability | ✅ | Database, archive writer/updater, scanner, queue and cache move are headless host-library code with unit tests; metadata validated against the published schema |
| VIII | Simplicity | ⚠️ justified | One new dependency (SQLite) — required by V and by the query needs; plain SQL, no ORM; own ZIP tail writer limited to appending one entry (Complexity Tracking) |
| IX | Localisation | ✅ | ~80 keys × 6 with plurals (pages, galleries, downloads), relative times by culture; de/ja layout pass |
| X | Version control | ✅ | Tags `windows-v0.4.0` and `contract-v1.2.1` (archive format document, no API change); CHANGELOGs; no archives, databases or images committed (tests build archives in temp folders) |

## Project Structure

### Documentation (this feature)

```text
specs/006-windows-library/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── archive-format.md      # .cbz layout, ComicInfo subset, hentai-reader.json format 1, updates
│   └── library-ui.md          # navigation, History, Library, Downloads, star, settings
├── checklists/requirements.md
└── tasks.md                   # /speckit-tasks
```

### Source Code (repository root)

```text
contract/archive/
├── README.md                          # format 1 (from contracts/archive-format.md)
└── hentai-reader.schema.json          # JSON Schema of hentai-reader.json
windows/src/HentaiReader.Host/
├── Data/LibraryDatabase.cs            # connection, user_version migrations, progress.json import
├── Data/*Repository.cs                # folders, archives, history, progress, reader settings, downloads
├── Archive/CbzWriter.cs               # assemble stored archive from staged pages + metadata
├── Archive/CbzReader.cs               # open, list pages, read entry bytes, content id
├── Archive/MetadataUpdater.cs         # append-only update of hentai-reader.json, compaction, journal
├── Archive/ArchiveMetadata.cs         # format 1 model, ComicInfo writer
├── Archive/ImageFormat.cs             # magic-number sniffing → extension
├── Library/LibraryScanner.cs          # scan + change detection, foreign archives, availability
├── Library/LibraryFolders.cs          # add/remove/target, move archives
├── Downloads/DownloadQueue.cs         # per-source workers, states, staging, resume, cancel
├── Storage/PageCache.cs               # + second root during a move, CacheMover
└── Reading/IPageProvider.cs           # online (PageImageLoader+cache) and archive providers
windows/src/HentaiReader.App/
├── Pages/HistoryPage.xaml(.cs)
├── Pages/LibraryPage.xaml(.cs)
├── Pages/DownloadsPage.xaml(.cs)
├── Pages/GalleryPage / ReaderPage     # Save to library / In library, per-gallery reader settings
├── Pages/SourcePage                   # star button, default query on open
├── Pages/SettingsPage                 # start page, library folders, cache folder, default searches
├── Reader/ReaderSession.cs            # uses IPageProvider
├── Services/AppServices.cs            # database, library, downloads, watcher lifetime
├── MainWindow.xaml(.cs)               # History/Library/Downloads items, start page
└── Strings/*/Resources.resw
windows/installer/distribution-rules.json   # + Microsoft.Data.Sqlite, SQLitePCLRaw, e_sqlite3.dll
windows/THIRD-PARTY-NOTICES.md              # + SQLite (public domain), SQLitePCLRaw (Apache-2.0)
windows/tests/HentaiReader.Host.Tests/
└── Data/*, Archive/*, Library/*, Downloads/*, Storage/CacheMoveTests.cs
```

**Structure Decision**: everything testable without UI (database, archive format, scanner, queue,
cache move, page providers) lives in `HentaiReader.Host` under new `Data/`, `Archive/`, `Library/`,
`Downloads/` folders, as in 005; the app gets pages, view models and platform glue (pickers, Recycle
Bin, file watcher wiring). The archive format is a cross-platform contract, so its schema lives in
`contract/archive/` next to the host API contract.

## Post-design Constitution Re-check

Design keeps all rows. V is now fully met (database + archive copy of metadata). The only
justified item is VIII: one dependency and a small ZIP tail writer, both tracked below. ✅

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| New dependency `Microsoft.Data.Sqlite` (+ native `e_sqlite3.dll`) (VIII) | Constitution V requires a local database; library search/sort/filter over 1 000+ archives and 5 000 history entries need indexes | JSON files rewrite whole files per progress change and have no query support; other embedded stores lack Android parity |
| Own append-only ZIP tail writer for metadata updates (VIII) | `ZipArchive` Update mode rewrites the whole (hundreds of MB) archive for a few bytes of progress | Full rewrites are slow and unsafe on crash; a side-car file breaks self-containment (V) |
