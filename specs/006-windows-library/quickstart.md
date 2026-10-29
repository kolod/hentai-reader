# Quickstart: validating 006

Prerequisites: Windows 11 x64, .NET 10 SDK, the repo built
(`dotnet build windows/HentaiReader.slnx -c Debug -p:Platform=x64`). Run the app with a scratch
data folder: `$env:HENTAIREADER_DATA = "$env:TEMP\hr-006"` (contains `library.db`, `covers\`,
preferences). Library folders used below: `$env:TEMP\hr-lib1`, `$env:TEMP\hr-lib2`.

## 1. Automated

```powershell
dotnet test --project windows/tests/HentaiReader.Host.Tests
```

Covers: database schema and migration from `progress.json`; archive writer (stored entries,
names, ComicInfo, JSON schema validation); append-only metadata update incl. crash in the middle
(truncated tail → previous archive still valid) and compaction; foreign archive identity and size
measurement; download queue state machine with a fake source (cache reuse, rate limit → limited,
resume after "restart", cancel leaves no files); library scan (changed/unchanged files, unreadable
file, unavailable folder); cache move with two roots, failure and resume.

## 2. Save and read offline (US1, SC-001, SC-002)

1. Open a 40-page Hitomi gallery, read a few pages (they land in the page cache), go back.
2. **Save to library** → progress on the gallery screen and in Downloads; finished in seconds for
   cached pages; the archive appears in the target folder only when complete.
3. Inspect the archive: `Expand-Archive` lists `0001.webp…`, `ComicInfo.xml`, `hentai-reader.json`;
   a stored-method check (e.g. `[IO.Compression.ZipFile]::OpenRead(...)` entries with
   `CompressedLength == Length`) passes for all entries.
4. Disconnect the network, open Library → open the gallery → first page within 1 s, read to the end.

## 3. Limits and resume (US5, SC-003)

- Debug build with `HENTAIREADER_FAULT=image509`: saving an E-Hentai gallery stops as Limited with
  the reason; Resume after clearing the variable continues.
- Start a 100-page save, close the app at ~50 %, start again → it continues at the next page; the
  request log shows no page fetched twice.
- Cancel a running save → no `.cbz` / `.cbz.part` and no staging folder remain.

## 4. History and per-gallery zoom (US2, SC-006)

- Read three galleries partly; zoom one to 130 % with reading width; restart → History lists them
  newest first; opening the zoomed one restores page and 130 %.
- Remove one entry; Clear history → empty state.

## 5. Library management and folders (US3, US4, SC-005, SC-008)

- Copy an archive saved by the app and one plain comic `.cbz` into `hr-lib2` while the app runs and
  `hr-lib2` is a library folder → both appear within 5 s; the foreign one with its file name.
- Search, filter and sort; Delete → the file is in the Recycle Bin.
- Remove `hr-lib1` with *Move galleries* → files now in `hr-lib2`, still listed and readable.
- Rename `hr-lib2` in Explorer → its galleries show as unavailable; rename back → available.

## 6. Start page, default query, cache move (US6, US7, SC-007, SC-009)

- Start page = Library → app opens on Library.
- On Hitomi.la search `female:glasses` with English, press the star → filled; restart, open
  Hitomi.la → that search runs; Ctrl+click → cleared.
- With ~500 MB cached, change the cache folder while reading → no failed pages; progress in
  Settings; old folder empty at the end; close the app mid-way → the move resumes.

## 7. Layout and languages (SC-010)

- de-DE and ja-JP at 1024 px: History, Library, Downloads, Settings → Library, dialogs — no clipped
  text.
