# Hentai Reader archive format 1

Cross-platform contract (Constitution V): the Windows app writes it in 006, the Android app reads
and writes the same files later. The JSON Schema is published as
[`hentai-reader.schema.json`](hentai-reader.schema.json) and validated by the host tests.

## Container

- One file per gallery, extension `.cbz`, ZIP format, **every entry STORED** (method 0); no
  encryption; ZIP64 allowed for archives over 4 GiB.
- Entry order when written by the app: page images, `ComicInfo.xml`, `hentai-reader.json`.
  Readers MUST NOT rely on order; they use the names below.
- The central directory may be preceded by unreferenced bytes (older metadata versions left by
  append-only updates). Readers MUST use the last end-of-central-directory record.

## Page entries

- Names `NNNN.<ext>` at the archive root, zero-padded to at least 4 digits, numbered from 0001 in
  reading order; `<ext>` is the real format: `webp`, `jpg`, `png`, `gif`, `avif`.
- Bytes exactly as delivered by the source (no recompression, no conversion).

## `ComicInfo.xml`

Written once for third-party readers (ComicInfo 2.0 subset): `Title`, `Series` (= title),
`Writer` (artists, comma-separated), `Penciller` (groups), `Genre` (type), `Tags` (all tags as
`namespace:name`, comma-separated), `LanguageISO`, `PageCount`, `Web` (gallery URL),
`Manga` = `YesAndRightToLeft` only if the source says so, else omitted, and `Pages/Page` with
`Image`, `ImageWidth`, `ImageHeight` (cover page `Type="FrontCover"`). Not updated afterwards.

## `hentai-reader.json`

```json
{
  "format": 1,
  "app": "Hentai Reader 0.4.0 (Windows)",
  "source": { "id": "hitomi", "name": "Hitomi.la", "galleryId": "1234567", "webUrl": "https://…" },
  "title": "…",
  "titleOriginal": null,
  "language": "en",
  "type": "manga",
  "uploader": null,
  "uploadedAt": "2026-10-20T12:00:00Z",
  "tags": [{ "namespace": "female", "name": "glasses" }],
  "savedAt": "2026-10-25T09:00:00Z",
  "pages": [{ "file": "0001.webp", "width": 1280, "height": 1810 }],
  "progress": { "page": 12, "offset": 0.4, "finished": false, "updatedAt": "2026-10-25T09:30:00Z" },
  "reader": { "zoom": 1.0, "fitWidth": true, "gaps": false, "updatedAt": "2026-10-25T09:30:00Z" }
}
```

Rules:

- `format` is an integer; readers accept any file whose `format` they know and ignore unknown
  fields; a writer that does not know a newer `format` MUST NOT rewrite the file.
- `pages` lists every page entry in reading order with its exact pixel size; `width`/`height` > 0.
- `progress.page` is 0-based and `< pages.length`; `offset` in [0, 1].
- `reader.zoom` in [0.5, 3]; `fitWidth`/`gaps` booleans. Android maps them to its own reader.
- All times ISO 8601 UTC. `progress` and `reader` blocks each carry their own `updatedAt`; when two
  copies disagree, the newer block wins.
- Text is UTF-8 without BOM.

## Updating

Only `hentai-reader.json` is ever updated, by appending a new entry, a new central directory and a
new end record (append-only, see "Updating"). Writers compact the file when unreferenced bytes exceed 1 MiB.
Third-party tools that rewrite the archive normally keep it valid (they drop the dead bytes).
