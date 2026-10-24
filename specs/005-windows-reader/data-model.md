# Data Model: Windows Reader

All files are per Windows user under `%LOCALAPPDATA%\HentaiReader` (`HENTAIREADER_DATA` overrides),
never in the program folder.

## Preferences (`preferences.json`, extended)

| Field | Type | Default | Notes |
|-------|------|---------|-------|
| `Language`, `AdultConfirmed`, `LastSourceId` | existing | | |
| `PreloadPages` | int 3–5 | 4 | FR-008 |
| `ReadingWidth` | int 600–2000 (DIP) | 1000 | FR-011 |
| `CacheLimitMb` | int 256–10240 | 1024 | FR-023 |
| `SourceSettings` | `{ sourceId: { settingId: value } }` | `{}` | only ids/values the manifest declares; others dropped on load (FR-026) |

## Reading progress (`progress.json`)

Key `"{sourceId}|{galleryId}"` →

| Field | Type | Rule |
|-------|------|------|
| `page` | int ≥ 0 | top visible page |
| `offset` | double 0–1 | fraction of that page's height above the viewport top |
| `pageCount` | int > 0 | from the gallery |
| `read` | bool | true once the last page's bottom was in view; stays true |
| `updatedAt` | ISO 8601 | |
| `title` | string | for a future history list |

Written atomically (temp file + replace); saved on leaving the reader, on window close, and 5 s after
scrolling stops.

## Page cache (`cache/pages/`)

- File `{sha256(key)}` with the downloaded bytes; key
  `"{sourceId}|{galleryId}|{pageIndex}|{settingsFingerprint}"`, fingerprint = SHA-256 of the
  source's sorted setting values (so different image quality never mixes).
- `cache/pages/index.json`: `{ key: { file, size, lastUsed } }`, flushed at most every 10 s and on
  exit; rebuilt by scanning when missing or invalid (lastUsed = file time).
- Eviction: least recently used until total ≤ `CacheLimitMb`; Clear empties folder and index.

## Reading session (memory only)

| Field | Notes |
|-------|-------|
| `Source`, `Gallery` (contract `Gallery` JSON), `Settings` | from the gallery page |
| `Pages[i]` | `width`, `height`, `size` (`exact`/`estimated`), `state`, `image?`, `pageImage?` (last `PageImage`, for `RetryHint.previous`), `attempt` |
| `Zoom` | 0.5–3.0, session only |
| `Position` | page + offset (as in progress) |

Page state machine:

```text
Placeholder ─▶ Resolving ─▶ Downloading ─▶ Decoding ─▶ Loaded
     ▲              │             │             │          │ (leaves memory window)
     │              └──────┬──────┴──────┬──────┘          ▼
     │                  Failed(kind)   (cache hit: Placeholder ─▶ Decoding)
     └── Retry ────────────┘          Released (bytes in disk cache, no bitmap)
```

`Resolving` may change `height` (exact size from `PageImage`) — applied before `Decoding`.

## Strip geometry (pure)

- Inputs: page sizes `(w_i, h_i)`, column width `W` (DIP) = `min(viewportWidth, ReadingWidth) ×
  Zoom`.
- `height_i = W × h_i / w_i`; `top_0 = 0`, `top_{i+1} = top_i + height_i`; total = `top_n`.
- `PageAt(y)` by binary search; `ScrollFor(page, offset) = top_page + offset × height_page`.

## Load window (pure)

Given scroll `y`, viewport height `H`, preload `N`:

- visible = pages with `[top_i, top_i + height_i)` ∩ `[y, y + H)` ≠ ∅
- ahead = following pages until count ≥ N **and** covered height ≥ H
- behind = preceding pages until count ≥ 1 **and** covered height ≥ H/2
- order = visible (top→bottom), ahead (near→far), behind (near→far)

## Account (per source)

| Field | Source |
|-------|--------|
| cookies | existing DPAPI `sources/<id>/cookies.bin` |
| `loggedIn`, `imageLimit`, `exhentai` | last `getAccountStatus` result, in memory, refreshed on settings open, after sign-in/out, after a rate limit |
