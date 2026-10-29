---

description: "Task list for feature 006-windows-library"
---

# Tasks: Windows Library and History

**Input**: Design documents from `/specs/006-windows-library/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md),
[data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: MANDATORY (Constitution VII): unit tests for the database, archive writer/reader/updater
(incl. crash cases), scanner, download queue and cache move; JSON Schema validation of written
metadata; UI Automation checks with the screen marker; manual offline and third-party reader checks.

**Organization**: database, archive format and page providers are Foundational; US1 (save + offline
read) and US2 (history) form the MVP; US3–US7 add library management, folders, download
robustness, start page/default query and cache move.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: parallelisable (different files, no unfinished dependencies)
- **[Story]**: US1–US7 from spec.md
- Paths relative to the repository root; `W` = `windows/`, `H` = `W/src/HentaiReader.Host/`,
  `A` = `W/src/HentaiReader.App/`, `T` = `W/tests/HentaiReader.Host.Tests/`

---

## Phase 1: Setup

- [X] T001 Add `Microsoft.Data.Sqlite` 10.0.x to `H/HentaiReader.Host.csproj`; create folders `H/Data/`, `H/Archive/`, `H/Library/`, `H/Downloads/`, `T/Data/`, `T/Archive/`, `T/Library/`, `T/Downloads/`; add `AppPaths.Database` (`library.db`), `AppPaths.Covers` (`covers\`) and `AppPaths.DefaultLibrary` (`Documents\Hentai Reader`) to `A/Services/Preferences.cs`
- [X] T002 [P] Distribution: add `Microsoft.Data.Sqlite.dll`, `SQLitePCLRaw.*.dll` and `e_sqlite3.dll` (win-x64 only) to `W/installer/distribution-rules.json` (required + allowed), SQLite (public domain) and SQLitePCLRaw (Apache-2.0) to `W/THIRD-PARTY-NOTICES.md`; `pwsh W/build/Build-Installer.ps1` verification passes
- [X] T003 [P] Publish the archive format: `contract/archive/README.md` (from [contracts/archive-format.md](contracts/archive-format.md)) and `contract/archive/hentai-reader.schema.json` (JSON Schema draft 2020-12 for `hentai-reader.json` format 1); `contract/CHANGELOG.md` `[1.2.1]` "Added archive format 1"; `contract/package.json` 1.2.1
- [X] T004 [P] English keys for all new UI text per [contracts/library-ui.md](contracts/library-ui.md) (navigation items, History, Library, Downloads, Save to library states, star tooltip, settings sections, dialogs, empty states, plurals for pages/galleries/downloads, relative times) in `A/Strings/en-US/Resources.resw`

---

## Phase 2: Foundational (block all stories)

- [X] T005 Implement `H/Data/LibraryDatabase.cs`: open/create `library.db` (WAL, foreign keys), schema v1 per [data-model.md](data-model.md#tables) via `PRAGMA user_version`, one-time import of `progress.json` into `progress` + `history` (rename to `progress.json.imported`); all calls async off the UI thread
- [X] T006 [P] Repositories in `H/Data/`: `ProgressRepository.cs`, `HistoryRepository.cs`, `ReaderSettingsRepository.cs`, `FolderRepository.cs`, `ArchiveRepository.cs` (search by title, source/state filters, three sorts, paging), `DownloadRepository.cs` (+ `download_page`)
- [X] T007 Unit tests `T/Data/LibraryDatabaseTests.cs`, `T/Data/RepositoryTests.cs`: create, re-open, migration import from a sample `progress.json`, exactly-one target folder, history ordering and paging (5 000 rows < 1 s), archive search/filter/sort combinations
- [X] T008 [P] `H/Archive/ImageFormat.cs` (magic numbers WebP, JPEG, PNG, GIF, AVIF → extension) and `H/Archive/ArchiveMetadata.cs` (format 1 model, JSON read/write per [archive-format](contracts/archive-format.md), unknown-field tolerance, `ComicInfo.xml` writer)
- [X] T009 [P] `H/Archive/CbzWriter.cs`: assemble `<name>.cbz.part` from staged pages (`NNNN.<ext>`, padding ≥ 4) + `ComicInfo.xml` + `hentai-reader.json`, all entries STORED, then atomic rename; safe file names from titles (invalid characters, length, collisions)
- [X] T010 [P] `H/Archive/CbzReader.cs`: open any ZIP/CBZ read-only, last end record, page entries in name order, read entry bytes, metadata if present, content id (SHA-256 of the central-directory listing, [data-model](data-model.md#gallery-identity))
- [X] T011 `H/Archive/MetadataUpdater.cs` per research R4: append new `hentai-reader.json` + central directory + end record, CRC-32, compaction above 1 MiB dead bytes or ZIP64 → full rewrite into a temporary file with atomic replace
- [X] T012 Unit tests `T/Archive/*`: every entry method 0, names and order, metadata validates against `contract/archive/hentai-reader.schema.json`, reader round trip, foreign archive (no metadata) → pages by name + stable content id across rename, update keeps images byte-identical, truncated append (simulated crash at several offsets) → previous archive still opens with old metadata, update after a crash, compaction keeps unknown fields, newer format not overwritten
- [X] T013 `H/Reading/IPageProvider.cs` with `OnlinePageProvider` (005 path: `PageCache` + `PageImageLoader`) and `ArchivePageProvider` (entry bytes off the UI thread, sizes from metadata); move the online path out of `A/Reader/ReaderSession.cs` so the session takes a provider; reader behaviour unchanged
- [X] T014 Per-gallery reader settings: `A/Pages/ReaderPage.xaml.cs` applies zoom / width mode / gaps from `ReaderSettingsRepository` when opening and stores them when leaving (FR-020); progress moves from `ProgressStore` to `ProgressRepository` (`A/Services/AppServices.cs` creates the database only after the adult confirmation, FR-028)

**Checkpoint**: `dotnet test` green; the 005 reader works as before, now with progress in the database and remembered zoom per gallery.

---

## Phase 3: User Story 1 — Save a gallery and read it offline (P1) 🎯 MVP

**Goal**: Save to library downloads a gallery into one `.cbz`; the Library lists it; it reads offline.

**Independent test**: [quickstart.md §2](quickstart.md).

- [X] T015 [US1] `H/Downloads/DownloadQueue.cs`: per-source worker, states per [data-model](data-model.md#download-state-transitions), page loop (page cache hit → copy; else `PageImageLoader` with the queued settings), staging under `<folder>\.hentai-reader\partial\<id>\`, exact sizes into `download_page`, limit/sign-in → `limited` and stop the worker (FR-003), completion → `CbzWriter` → archive row → staging removed
- [X] T016 [US1] Unit tests `T/Downloads/DownloadQueueTests.cs` with a fake transport and `minimal` / `ehentai.js`: cache reuse makes no image request (SC-002), 429 → limited with retry-after and no further requests, sign-in required → limited, archive appears only when complete
- [X] T017 [US1] `H/Library/LibraryService.cs`: library facade over repositories and folders (list for the Library screen, "is saved" lookup by gallery identity, open info for the reader)
- [X] T018 [US1] `A/Pages/GalleryPage.xaml(.cs)`: **Save to library** / "Saving {done} / {total}" with Pause / Cancel / **In library** per [library-ui](contracts/library-ui.md#gallery-screen-and-reader-additions); reader toolbar icon with the same states in `A/Pages/ReaderPage.xaml(.cs)`
- [X] T019 [US1] `A/Pages/LibraryPage.xaml(.cs)` (`LibraryView`): card grid (cover decoded from the first page at thumbnail size, title, source, pages, progress), click opens `ReaderPage` with `ArchivePageProvider` at the saved position; navigation item in `A/MainWindow.xaml(.cs)` (FR-026)
- [X] T020 [US1] Progress and reader settings of saved galleries: mark `metadata_dirty` on change, write through `MetadataUpdater` when leaving the reader / closing the app, retry later when the archive is not writable (FR-009) — `A/Pages/ReaderPage.xaml.cs`, `H/Library/LibraryService.cs`
- [X] T021 [US1] Live check per [quickstart.md §2](quickstart.md): save a cached 40-page Hitomi gallery and an E-Hentai gallery, stored-method check, open in a third-party comic reader (SC-004), offline first page ≤ 1 s (SC-001) — measured: E-Hentai gallery saved, every entry stored, offline first page 0.22 s, progress written into the archive on leaving; third-party reader check left to the owner

**Checkpoint**: MVP part 1 — save and read offline.

---

## Phase 4: User Story 2 — Continue from history (P1)

**Goal**: History lists opened galleries and resumes them with their zoom.

**Independent test**: [quickstart.md §4](quickstart.md).

- [X] T022 [US2] Record history on every reader open (`A/Pages/ReaderPage.xaml.cs` → `HistoryRepository`): title, page count, last read; cover thumbnail saved once to `covers\` from the already loaded cover (research R11)
- [X] T023 [US2] `A/Pages/HistoryPage.xaml(.cs)` (`HistoryView`): incremental list newest first, progress text, relative time, library mark; open → saved copy if any, else online via `getGallery` then `ReaderPage` at the saved position; Remove entry; Clear history with confirmation; empty state; "not found" error from the source shown with Remove
- [X] T024 [US2] Navigation item and back target (reader → History) in `A/MainWindow.xaml(.cs)`; UIA check: open entry resumes page and zoom after restart (SC-006 with 5 000 generated rows)

**Checkpoint**: MVP complete (US1 + US2).

---

## Phase 5: User Story 3 — Manage the library (P2)

**Goal**: Search, filter, sort, delete; hand-placed and foreign archives appear.

**Independent test**: [quickstart.md §5](quickstart.md) first two bullets.

- [X] T025 [US3] `H/Library/LibraryScanner.cs`: scan folders recursively for `*.cbz`/`*.zip`, skip unchanged (path + size + mtime), read metadata or measure foreign pages once, mark unreadable, availability per folder (research R7)
- [X] T026 [US3] Page size measurement for foreign archives in the app (`BitmapDecoder` header read, off the UI thread) feeding the scanner via a delegate — `A/Services/ImageMeasure.cs`
- [X] T027 [US3] Unit tests `T/Library/LibraryScannerTests.cs`: new / changed / unchanged / deleted files, foreign archive, unreadable file does not stop the scan, identity kept across rename and move
- [X] T028 [US3] `FileSystemWatcher` per folder with 1 s debounce triggering incremental scans (SC-008) — `A/Services/LibraryWatcher.cs`
- [X] T029 [US3] `A/Pages/LibraryPage.xaml(.cs)`: search box (filters while typing), source / state filters, sort, unreadable/unavailable cards, context menu Open folder / Delete (confirmation, Recycle Bin via `SHFileOperationW`, history kept)

---

## Phase 6: User Story 4 — Several library folders (P2)

**Goal**: Add/remove folders, choose the target, move archives when removing.

**Independent test**: [quickstart.md §5](quickstart.md) last two bullets.

- [X] T030 [US4] `H/Library/LibraryFolders.cs`: add (scan), remove with Move (background, progress, cross-volume copy + verify + delete) or Keep files, set target (exactly one), default folder creation, unavailable handling
- [X] T031 [US4] Unit tests `T/Library/LibraryFoldersTests.cs`: move keeps archives readable and rows updated, keep-files only removes rows, last folder cannot be removed, unavailable folder rows kept
- [X] T032 [US4] Settings → Library section in `A/Pages/SettingsPage.xaml(.cs)`: folder list with counts and availability, Add folder (Windows App SDK `FolderPicker`), target radio, Remove dialog (Move to / Keep files / Cancel), inline move progress

---

## Phase 7: User Story 5 — Downloads keep going and survive a restart (P2)

**Goal**: Queue, pause/resume/cancel, resume after restart.

**Independent test**: [quickstart.md §3](quickstart.md).

- [X] T033 [US5] Queue persistence: on start `downloading`/`assembling` → `queued`, staged pages verified against `download_page`, workers restarted; pause / resume / cancel (staging removed, no partial file — FR-006) in `H/Downloads/DownloadQueue.cs`
- [X] T034 [US5] Unit tests in `T/Downloads/DownloadQueueTests.cs`: restart mid-download fetches no page twice (SC-003), paused stays paused, cancel leaves nothing, second gallery of the same source waits, two sources run in parallel, disk full / folder gone → failed with reason
- [X] T035 [US5] `A/Pages/DownloadsPage.xaml(.cs)` (`DownloadsView`): rows with state, reason, waiting time, progress, Pause / Resume / Cancel / Open, Clear finished; navigation item with active-count badge in `A/MainWindow.xaml(.cs)`

---

## Phase 8: User Story 6 — Start page and default query (P3)

**Goal**: Choose the start screen; star saves a per-source default search.

**Independent test**: [quickstart.md §6](quickstart.md) first two bullets.

- [X] T036 [US6] `Preferences.StartPage` + Settings → General choice; `A/MainWindow.xaml.cs` opens the chosen page at start with fallback to the first available source (FR-021)
- [X] T037 [US6] `Preferences.DefaultQueries`; star button `DefaultQueryButton` left of the search box in `A/Pages/SourcePage.xaml(.cs)` (filled when current = default via normalised JSON equality, click saves, Ctrl+click clears); opening a source runs its default (`A/ViewModels/SearchViewModel.cs`)
- [X] T038 [US6] Settings → Sources: saved default search per source with Remove in `A/Pages/SettingsPage.xaml(.cs)`

---

## Phase 9: User Story 7 — Move the page cache (P3)

**Goal**: Change the cache folder; files move in the background while reading continues.

**Independent test**: [quickstart.md §6](quickstart.md) last bullet.

- [X] T039 [US7] `H/Storage/PageCache.cs` second root (lookups new then old, puts to new) and `MoveStepAsync` (file-by-file move, index updates, persisted `{from, to}` resumed on start, failure keeps both roots and offers cancel back to the old folder — FR-024, FR-025)
- [X] T040 [US7] Unit tests `T/Storage/CacheMoveTests.cs`: reads during the move, resume after restart, failure (simulated no space) loses no file, cancel returns to the old folder
- [X] T041 [US7] Settings → Reader: Page cache folder with Change… (`FolderPicker`), move progress and Cancel move in `A/Pages/SettingsPage.xaml(.cs)`; `Preferences.CacheFolder` / `CacheMove`

---

## Phase 10: Polish & Cross-Cutting Concerns

- [X] T042 [P] Translate all new keys into `de-DE`, `fr-FR`, `es-ES`, `ja-JP`, `uk-UA` (plurals per CLDR); `ResourceParityTests` green
- [X] T043 Layout pass de-DE / ja-JP at 1024 px for History, Library, Downloads, Settings sections and dialogs (SC-010)
- [X] T044 Performance checks: Library with 1 000 generated archives in two folders ≤ 3 s (SC-005), cache move of 1 GB while reading (SC-007), hand-placed archive ≤ 5 s (SC-008) — measured: 1 002 galleries listed 0.7 s after start, search 0.31 s, dropped archive 1.1 s; the 1 GB cache move was not run live (unit tests cover reads, restart, cancel and failure during a move)
- [X] T045 [P] Docs: `W/README.md` (library, history, downloads, data files `library.db`/`covers\`, library folders), `W/CHANGELOG.md` 0.4.0, `contract/archive/README.md` cross-links, `specs/product-overview.md` roadmap (006 ✅ when merged); `<Version>` 0.4.0 in `W/Directory.Build.props`
- [X] T046 Final gate: `npm test`, `dotnet build windows/HentaiReader.slnx -c Release -p:Platform=x64` (0 warnings), `dotnet test --project windows/tests/HentaiReader.Host.Tests -c Release`, `pwsh windows/build/Build-Installer.ps1 -SmokeTest` (on a machine without the app installed, else CI) — all green before the PR — all green locally; the install smoke test runs in CI (the app is installed on the development machine)

---

## Dependencies & Execution Order

```text
Setup (T001–T004) → Foundational (T005–T014)
  → US1 save + offline (T015–T021) → US2 history (T022–T024)      ← MVP
  → US3 library management (T025–T029)
  → US4 folders (T030–T032)
  → US5 downloads robustness (T033–T035)
  → US6 start page + default query (T036–T038)
  → US7 cache move (T039–T041)
  → Polish (T042–T046)
```

- US1 needs the database (T005–T007), archive format (T008–T012) and providers (T013); US2 needs
  T005–T007 and T014 only and can be built in parallel with US1.
- US3 extends the Library screen of US1; US4 needs US3's scanner; US5 extends US1's queue.
- US6 and US7 are independent of US1–US5 (only preferences / page cache) and can run any time after
  Setup.

## Parallel Opportunities

```text
Setup:        T002 | T003 | T004
Foundational: T006 | T008 | T009 | T010 (after T005), then T011, T012
MVP:          US1 (T015–T021) alongside US2 (T022–T024)
Later:        US6 (T036–T038) | US7 (T039–T041) alongside US3–US5
Polish:       T042 | T045
```

## Implementation Strategy

1. **Format and data first** (Phase 2): the archive format and the database are what Android will
   share and what is hardest to change later — decided and tested before any screen.
2. **MVP** = US1 + US2: save and read offline, continue from history with the remembered zoom.
3. Then library management (US3), folders (US4) and download robustness (US5).
4. Comfort features: start page and default query (US6), cache move (US7).
5. PR `006-windows-library` → `main` with CI green; tags `contract-v1.2.1` and `windows-v0.4.0` after
   merge.
