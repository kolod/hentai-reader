# Data Model: Android App at Parity with Windows

The Android app keeps **the same data as Windows**: the same tables, preferences fields and archive
metadata. This file lists only where Android stores them and what it adds.
Reference: [006 data-model](../006-windows-library/data-model.md),
[005 data-model](../005-windows-reader/data-model.md), archive format 1 in
[contract/archive/README.md](../../contract/archive/README.md).

Nothing is written before the adult confirmation (FR-035).

## Locations

| Data | Android location | Windows counterpart |
|------|------------------|---------------------|
| `preferences.json` | `noBackupFilesDir/preferences.json` | `%LOCALAPPDATA%\HentaiReader\preferences.json` |
| `library.db` | `noBackupFilesDir/library.db` | same name in the data folder |
| Source storage | `noBackupFilesDir/storage/<sourceId>.json` | `storage\<sourceId>.json` |
| Cookies | `noBackupFilesDir/cookies.bin` (AES-GCM, Keystore key `hr_cookies`) | DPAPI-protected file |
| Covers | `noBackupFilesDir/covers/` | `covers\` |
| Download staging | `noBackupFilesDir/partial/<downloadId>/` | `<library folder>\.hentai-reader\partial\` |
| Page cache | `cacheDir/pages/` (default) or `externalCacheDirs[n]/pages/` | `cache\pages\` or the chosen folder |
| Library archives | user-chosen document trees | user-chosen folders |

`noBackupFilesDir` is excluded from backups; together with `allowBackup="false"` nothing leaves the
device (FR-036).

## Database `library.db`: same schema, two additions

Every table of 006 (`library_folder`, `archive`, `history`, `progress`, `reader_settings`,
`download`, `download_page`) with the same columns, types and rules. Changes:

### `library_folder` (Android)

| Column | Type | Rules |
|--------|------|-------|
| path | text unique | **the document-tree URI** (`content://…/tree/…`), persisted permission held |
| display_path | text | **new**: human-readable location for Settings ("Internal storage › Documents › Hentai Reader") |
| is_target, added_at | as Windows | |

Availability is computed: the permission is still held **and** the tree answers a child query. The
reason is kept in memory as `ok`, `missing` (card removed or folder deleted) or `no_access`
(permission revoked), and shown with an action (FR-028, edge cases).

Unlike Windows, no default folder row exists. The first save opens the picker; until then the
table may be empty (FR-028).

### `archive` (Android)

`rel_path` is the document ID path below the tree; `size` and `modified_at` come from
`DocumentsContract` columns. Otherwise identical. The gallery identity rule
(`source_id, gallery_id` or `local` + content ID over the central directory) is unchanged, so
a library copied between devices keeps its history and progress.

### `download` (Android)

`error_kind` gains two host-side reasons (not contract error kinds):

| Value | Meaning | Leaves the state when |
|-------|---------|----------------------|
| `waiting_wifi` | "Downloads on Wi-Fi only" is on and the network is metered | an unmetered network becomes available (automatic) |
| `stopped_by_system` | the system ended the background service (time limit, force stop) | next app start (automatic, `paused` ones stay paused) |

State transitions are those of 006, plus:

```text
queued/downloading ──(metered & wifi-only)──▶ queued (error_kind=waiting_wifi) ──(unmetered)──▶ downloading
downloading ──(service timeout / process killed)──▶ queued (error_kind=stopped_by_system) ──(app start)──▶ downloading
```

## Preferences `preferences.json`: same fields, Android additions

All 005 and 006 fields, with the same names and meaning (`Language`, `AdultConfirmed`,
`LastSourceId`, `PreloadPages`, `FitWidth`, `ReadingWidth`, `PageGap`, `CacheLimitMb`,
`SourceSettings`, `StartPage`, `DefaultQueries`, `CacheFolder`, `CacheMoveFrom`,
`NetworkCovers`). The window field (`Window`) does not exist on Android.

| Field | Android default | Windows default | Notes |
|-------|-----------------|-----------------|-------|
| `PreloadPages` | 3 | 4 | FR-019 |
| `CacheLimitMb` | 512 | 1024 | FR-020; range 128–10240 |
| `ReadingWidth` | 1000 dp, applies only when the screen is wider | 1000 DIP | FR-011 |
| `CacheFolder` | `null` = internal, or `card:<volume uuid>` | `null` or a path | the card is identified by its volume UUID, so a different card is not mistaken for it |
| `WifiOnlyDownloads` | `false` | — | **new**, FR-023 |
| `KeepScreenOn` | `true` | — | **new**, FR-014 |
| `NotificationAsked` | `false` | — | **new**, set once the permission prompt was shown (API 33+) |

Unknown fields from a Windows `preferences.json` are ignored. The file is never copied between
platforms; only archives are.

## Page cache

The same key (`sourceId|galleryId|pageIndex|settingsFingerprint`), file naming (`sha256(key)`),
`index.json` and LRU eviction as 005. Added rule: before writing, if free space on the cache volume
is below `min(1 GB, 10 % of the volume)`, the write is skipped, `CacheReserveNotice` is raised once
per session, and reading continues from the network (FR-020, SC-011).

## Memory-only state

### Image budget

| Field | Rule |
|-------|------|
| `budgetBytes` | clamp(`memoryClass` × 0.5 MiB, 64 MiB, 192 MiB) at start; × 0.5 after `TRIM_MEMORY_RUNNING_LOW`, restored on `UI_HIDDEN` → visible |
| `entries` | decoded page bitmaps or tiles: key (page, tile, scale), bytes, distance from view |
| `maxEntryBytes` | `budgetBytes / 8`; a larger decode is done at a lower scale or as tiles |

Release order: (1) farthest behind the view, (2) farthest ahead beyond the minimum preload of one
page plus one screen, (3) the rest ahead. Visible entries are never released. The rules are in
[contracts/resource-budgets.md](contracts/resource-budgets.md).

### Source engine pool

One engine per source session, created on first use and closed after 2 minutes idle or on
`TRIM_MEMORY_RUNNING_LOW`. Cookie jar, storage and settings are persistent, so a closed engine
reopens without user-visible change.

### Saved screen state (`SavedStateHandle`)

| Screen | Saved | Restored after process death as |
|--------|-------|---------------------------------|
| Search | source ID, text, filter values, cursor chain, first visible index | search run again up to the saved cursor depth, then scrolled to the index |
| Gallery | source ID, gallery ID | loaded again (`getGallery`) |
| Reader | source ID, gallery ID or archive ID, page, offset, zoom, fit width, gaps | opened at that place (SC-010) |
| History / Library / Downloads / Settings | list scroll index, filters, sort, search text | the same |

The navigation back stack itself is saved by the navigation library.

## Archive metadata

Unchanged: `hentai-reader.json` format 1 and `ComicInfo.xml` exactly as in
[contract/archive/README.md](../../contract/archive/README.md). Android fills the existing
free-text `app` field as `"Hentai Reader 0.1.0 (Android)"` (Windows: `"… (Windows)"`); no schema
change.
On a conflict between the database and an archive changed on another device, the newer `updatedAt`
wins per block, as on Windows.
