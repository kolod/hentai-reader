# Research: Windows Library and History

Decisions for [plan.md](plan.md). Probes were run on the development machine (.NET 10.0, Windows 11)
on 2026-10-25.

## R1 — Local database

- **Decision**: SQLite through `Microsoft.Data.Sqlite` 10.0.x (bundled `e_sqlite3`, SQLite 3.53) in
  one file `library.db` in the data folder. Tables: library folders, saved galleries (archives),
  history, reader settings per gallery, downloads with their page state, migration bookkeeping.
  `progress.json` (005) is imported once and then retired (kept read-only as a backup for one
  version).
- **Rationale**: Constitution V requires metadata "in the local database AND duplicated in the
  archive"; 005 deferred the database to this feature (its Complexity Tracking). Library search,
  sorting and filtering over 1 000+ galleries (SC-005) and history with 5 000 entries (SC-006) need
  indexed queries, not whole-file JSON rewrites. SQLite is a single file, needs no service, and the
  Android app can use the same engine and similar schema.
- **Alternatives considered**: more JSON files (rewrites whole files on every progress change,
  no search index); LiteDB (another document format, smaller ecosystem, no Android parity); EF Core
  (heavy for ~8 tables, migrations machinery not needed — plain SQL with a `user_version` schema
  number is enough).
- **Probe**: `Microsoft.Data.Sqlite` 10.0.13 + `SQLitePCLRaw.bundle_e_sqlite3` 2.1.13 adds
  `Microsoft.Data.Sqlite.dll`, `SQLitePCLRaw.*.dll` and `runtimes/win-x64/native/e_sqlite3.dll`;
  the distribution rules and third-party notices must list them (only the win-x64 native file is
  published for the x64 RID).

## R2 — Archive container and writing

- **Decision**: `.cbz` = ZIP with every entry STORED (method 0). Written with
  `System.IO.Compression.ZipArchive` (`CompressionLevel.NoCompression`) when a download completes:
  page images first, then `ComicInfo.xml`, then `hentai-reader.json` as the last entry. Written to
  `<name>.cbz.part` in the target folder and renamed when complete, so a library never lists a
  half-written archive.
- **Probe**: `CreateEntry(name, CompressionLevel.NoCompression)` on .NET 10 writes local header
  method 0, flags 0 (no data descriptor) — a true stored entry.
- **Rationale**: Constitution V (one archive per gallery, STORED, metadata inside); `.cbz` is what
  comic readers recognise (SC-004). The rename makes completion atomic on one volume.
- **Alternatives considered**: `.zip` extension (readers do not associate it with comics; still
  accepted when placed by hand); writing the archive progressively during download (resume would
  need to parse a partial ZIP — the staging folder of R5 is simpler).

## R3 — Metadata inside the archive

- **Decision**: two entries:
  - `hentai-reader.json` — authoritative, versioned (`format: 1`) record: identity, descriptive
    fields, tags by namespace, page list (file name, width, height), saved date, progress, reader
    settings. Schema published in `contract/archive/` so the Android app reads and writes the same
    thing (Constitution V).
  - `ComicInfo.xml` — the de-facto comic metadata (Title, Series, Writer = artists, Tags, Genre,
    LanguageISO, PageCount, Web, Pages with ImageWidth/ImageHeight) for third-party readers; written
    once, never updated.
- **Rationale**: third-party readers understand ComicInfo; our own needs (progress, zoom, gap,
  source identity, namespaced tags) do not fit it cleanly, and a JSON file is easy to update and
  version.
- **Alternatives considered**: only ComicInfo (no place for progress/reader settings without
  abusing fields); only JSON (third-party readers show nothing).

## R4 — Updating progress and reader settings inside a STORED archive

- **Decision**: append-only metadata updates. A new `hentai-reader.json` local entry, a new central
  directory (pointing to the old image entries and the new metadata entry) and a new end record are
  appended to the file; the previous metadata entry and directory become unreferenced bytes. When
  dead bytes would exceed 1 MiB, or the archive needs ZIP64, the archive is rewritten into a
  temporary file (entries copied raw, never recompressed) and atomically replaces the old one. A
  small own ZIP tail writer (CRC-32 table, local header, central directory, end record) in the host
  library; readers use the last *valid* end record, so bytes left by an interrupted append are
  ignored.
- **Rationale**: `ZipArchive` in Update mode rewrites the whole archive (hundreds of MB) for a few
  bytes of progress. Appending is fast and crash-safe: until the new end record is complete, every
  reader still finds the previous end record within the last 64 KiB and sees the previous,
  consistent archive. Updates happen only on leaving the reader / closing the app (FR-009).
- **Alternatives considered**: progress only in the database (violates "duplicated in the archive");
  rewriting the archive on each update (too slow, risky); a side-car file next to the archive
  (not self-contained when the archive is copied alone).

## R5 — Download pipeline and resume

- **Decision**: a download queue in the host library: one worker per source (FR-004), each page
  through `PageImageLoader` (005: getPageUrl + retry hint + 20 s stall timeout), pages first looked
  up in the online `PageCache` (FR-002). Fetched pages go to a staging folder
  `<target>\.hentai-reader\partial\<download id>\` (same volume as the target, hidden) as
  `<index>.<ext>`; the database records which pages are done. On completion the archive is assembled
  from the staging files (R2) and the staging folder removed; cancel removes it (FR-006). Rate
  limit / sign-in errors set the download to Limited and stop that source's worker (FR-003).
- **Rationale**: staging files make resume trivial (a page is done when its file exists and the
  database says so) and keep the final archive write a fast sequential copy.
- **Alternatives considered**: keeping pages only in the page cache (LRU may evict them before the
  download ends); one worker for all sources (one slow source would block the other).

## R6 — Page files in the archive

- **Decision**: entries named `0001.webp`, `0002.jpg`, … — zero-padded to at least 4 digits (more
  when a gallery has ≥ 10 000 pages) so lexical order is reading order (FR-010); the extension comes
  from the image bytes (magic numbers: WebP, JPEG, PNG, GIF, AVIF), not from the URL; bytes are
  stored exactly as delivered.
- **Rationale**: readers sort by name; sources sometimes serve a different format than the URL
  suggests.

## R7 — Library folders, scanning and identity

- **Decision**: at start and when a folder is added, each library folder is scanned (top level and
  sub-folders) for `*.cbz` / `*.zip`; a `FileSystemWatcher` per folder picks up later changes
  (SC-008: within 5 s, debounced). The database indexes archives by path + size + modified time,
  so unchanged files are not reopened. Identity: `source + gallery id` from `hentai-reader.json`; for
  foreign archives a content id = SHA-256 of the central directory listing (names, sizes, CRCs), which
  survives renames and moves. Unreachable folders keep their rows, marked unavailable (FR-017).
- **Rationale**: scanning only changed files keeps 1 000 archives fast (SC-005); identity by
  metadata/content keeps history across renames (edge case).
- **Alternatives considered**: hashing whole archives (minutes for large libraries); path identity
  (breaks on rename/move).

## R8 — Reading from an archive in the 005 reader

- **Decision**: the reader gets its pages from a page provider: the online provider (005:
  `PageImageLoader` + `PageCache`) or the archive provider (random access to stored entries, bytes
  read off the UI thread). Page sizes come from the metadata; for foreign archives they are measured
  once from the image headers (`BitmapDecoder` reads dimensions without decoding pixels) when the
  archive is first opened or scanned and stored in the database, so slots are reserved before
  images load (Constitution IV).
- **Rationale**: one reader, two sources of bytes; nothing else in the strip, layout or planner
  changes (FR-012).

## R9 — Recycle Bin, folder picker

- **Decision**: delete via the shell's `SHFileOperationW` with `FOF_ALLOWUNDO` (P/Invoke; the
  `Microsoft.VisualBasic.FileIO` recycle option needs the Windows Forms desktop framework, which the
  WinUI app does not reference); choose folders with the Windows App SDK `FolderPicker`
  (`Microsoft.Windows.Storage.Pickers`, works unpackaged with the window id).
- **Alternatives considered**: `Microsoft.VisualBasic.FileIO` (pulls in Windows Forms); the UWP
  `Windows.Storage.Pickers` picker (needs `InitializeWithWindow` interop in unpackaged apps).

## R10 — Moving the page cache

- **Decision**: `PageCache` gets an optional second root during a move: lookups check the new root
  then the old one; new pages go to the new root; a background mover moves files one by one
  (`File.Move`, which copies across volumes) and updates the index; the move (from, to, done) is
  recorded in preferences and resumed on start. A failure (no space, folder gone) stops the mover,
  moves nothing back that is already safe, keeps reading from both roots and offers to cancel,
  which returns to the old folder (FR-025).
- **Rationale**: reading never waits for the move (SC-007).

## R11 — Covers for history and library

- **Decision**: library covers are decoded from the archive's first page at thumbnail size; history
  covers are stored once as small JPEG thumbnails in `covers\` in the data folder when the gallery
  is first opened (encoded from the already loaded cover), so History needs no requests.
- **Amendment (2026-10-29)**: a reader opened past the first page loads that page once for the
  cover (saved copy, page cache, or one download). An entry still without a cover (older entries,
  deleted cover file) gets one in History, one visible row at a time: from the saved copy's first
  page, else the cached first page. Only with the opt-in setting "Load covers from the network"
  (`Preferences.NetworkCovers`, off by default) History asks the source: `getGallery`, then its
  `cover` image (the first page when the source gives none), through the source session and its
  politeness limits; never while the source is limited.
- **Rationale**: "covers come from the archive or the image cache, not new requests" (spec
  assumption); History must open in 1 s with 5 000 entries (SC-006). Network covers stay an
  explicit choice because E-Hentai counts image views and rate-limits bursts.

## R12 — Start page and default queries

- **Decision**: preferences gain `StartPage` (`last-source` | `source:<id>` | `history` | `library`)
  and `DefaultQueries` (source id → query text + filter values, the same JSON the search view model
  already builds). The star compares the current query/filters with the stored one (normalised JSON
  equality).
- **Rationale**: small settings, no reason for the database.

## R13 — Per-gallery reader settings

- **Decision**: zoom, width mode and gaps per gallery identity, stored in the database (history row
  and library row share the identity) and in the archive metadata for saved galleries; applied when
  the reader opens, saved when it closes (FR-020).
