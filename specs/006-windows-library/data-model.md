# Data Model: Windows Library and History

Storage: `library.db` (SQLite, schema version in `PRAGMA user_version`, starts at 1) in the data
folder; small settings in `preferences.json`; per-gallery metadata also inside each archive
([contracts/archive-format.md](contracts/archive-format.md)). Nothing is created before the adult
confirmation (FR-028).

## Gallery identity

`GalleryKey = (source_id, gallery_id)` for galleries from a source; `("local", content_id)` for
archives without this app's metadata, where `content_id` = first 32 hex chars of SHA-256 over the
archive's central directory listing (`name \0 size \0 crc32 \n`, sorted by name). Used by history,
reader settings, library rows and downloads.

## Tables

### `library_folder`

| Column | Type | Rules |
|--------|------|-------|
| id | integer PK | |
| path | text unique | absolute, normalised, no trailing separator |
| is_target | integer 0/1 | exactly one row = 1 (FR-015) |
| added_at | text (ISO 8601 UTC) | |

At least one folder always exists; default `Documents\Hentai Reader`, created on first use.
Availability is computed (directory exists), not stored.

### `archive` (saved gallery)

| Column | Type | Rules |
|--------|------|-------|
| id | integer PK | |
| folder_id | integer FK → library_folder | cascade on folder removal "keep files" |
| rel_path | text | path inside the folder; unique per folder |
| size, modified_at | integer, text | change detection for rescans (R7) |
| source_id, gallery_id | text | identity (`local` + content id for foreign archives) |
| title, title_original | text | |
| language, type | text null | contract vocabulary |
| tags_json | text | `[{namespace, name}]` |
| page_count | integer | ≥ 0 |
| pages_json | text | `[{file, width, height}]` in reading order |
| saved_at | text | from metadata, else file time |
| state | text | `ok` · `unreadable` · `unavailable` (folder missing) |
| format | text | `hentai-reader-1` · `foreign` |
| metadata_dirty | integer 0/1 | progress/settings not yet written into the archive (FR-009) |

Indexes: `(source_id, gallery_id)`, `title COLLATE NOCASE`, `saved_at`.

### `history`

| Column | Type | Rules |
|--------|------|-------|
| source_id, gallery_id | text, PK together | |
| title | text | |
| cover_file | text null | name in `covers\` (R11) |
| page_count | integer | |
| last_read_at | text | ordering (newest first) |

### `progress` (replaces `progress.json`)

| Column | Type | Rules |
|--------|------|-------|
| source_id, gallery_id | PK | |
| page | integer | 0-based, < page_count |
| offset | real | 0–1 inside the page |
| page_count | integer | |
| finished | integer 0/1 | set when the last page was fully in view (005) |
| updated_at | text | |

Shared by online and library reading of the same gallery.

### `reader_settings`

| Column | Type | Rules |
|--------|------|-------|
| source_id, gallery_id | PK | |
| zoom | real | 0.5–3.0 |
| fit_width | integer 0/1 | width mode (005 FR-011) |
| gaps | integer 0/1 | |
| updated_at | text | |

### `download`

| Column | Type | Rules |
|--------|------|-------|
| id | integer PK | also the staging folder name |
| source_id, gallery_id | text | |
| gallery_json | text | the `Gallery` from `getGallery` at queue time (pages, sizes, tags) |
| settings_fingerprint | text | source settings at queue time (image quality) |
| folder_id | integer FK | target folder |
| state | text | see transitions |
| pages_done | integer | count of staged pages |
| error_kind, error_message | text null | why it stopped |
| retry_after | text null | from a rate limit |
| queued_at, updated_at | text | |

### `download_page`

| Column | Type | Rules |
|--------|------|-------|
| download_id, page | PK | |
| file | text | staged file name `<page>.<ext>` |
| width, height | integer | exact size from `PageImage` or the image itself |

## Download state transitions

```text
queued ──(worker free)──▶ downloading ──(all pages)──▶ assembling ──▶ done
   ▲                         │  │  │                                   (row kept 7 days for the list,
   │                         │  │  └─(rate limit / sign-in)──▶ limited     then deleted)
   │                         │  └─(page failed after retries)──▶ failed
   │                         └─(pause)──▶ paused
   └──────────(resume / retry from paused, limited, failed)─────────────┘
any state except done ──(cancel)──▶ cancelled (staging deleted, row deleted)
app start: downloading/assembling → queued (resume at the next missing page)
```

## Preferences additions (`preferences.json`)

| Field | Type | Default |
|-------|------|---------|
| StartPage | string | `last-source` (`source:<id>`, `history`, `library`) |
| DefaultQueries | map sourceId → `{ text, filters }` | empty |
| CacheFolder | string null | null = `%LOCALAPPDATA%\HentaiReader\cache\pages` |
| CacheMove | `{ from, to }` null | null; set while a move runs (R10) |

## In-archive metadata

`hentai-reader.json` (format 1) — see [contracts/archive-format.md](contracts/archive-format.md):
identity, descriptive fields, `pages[{file,width,height}]`, `savedAt`, `progress{page, offset,
finished, updatedAt}`, `reader{zoom, fitWidth, gaps}`. On conflict between the database and an
archive edited elsewhere, the newer `updatedAt` wins (per block).

## Migration

On first start of 0.4.0 (after the adult confirmation): create `library.db`, import every entry of
`progress.json` into `progress` and `history` (title from the progress record, no cover), rename
`progress.json` to `progress.json.imported`.
