# Data Model: Windows App Shell and Source Host

Contract data shapes are in `contract/host-api.d.ts`; this file covers the app's own state.

## Host (`HentaiReader.Host`)

| Entity | Fields | Rules |
|--------|--------|-------|
| **SourceBundle** | id, script text, manifest, availability (`Available` / `Unavailable(reason)`) | Loaded from the app's bundled `sources/<id>/<id>.js`; unavailable if the manifest fails validation or needs a newer contract |
| **SourceSession** | bundle, engine (worker thread), storage, cookie jar, scheduler, settings (defaults in 003), state (`Idle` / `Busy` / `Limited(retryAfter?)` / `Broken`) | One per source while the app runs; `Limited` after `RateLimited` — no requests until the reader retries (FR-013); `Broken` after a limit hit or engine fault → recreated on next call |
| **Storage** | key → string | Persisted per source at `%LOCALAPPDATA%\HentaiReader\sources\<id>\storage.json`; contract limits |
| **CookieJar** | cookies (name, value, domain, hostOnly, path, expires) | Persisted per source, encrypted with DPAPI (current user) at `…\sources\<id>\cookies.bin`; never logged |
| **CallResult** | ok + value, or error (type, kind/name, message, retryAfterSeconds, details) | Mirrors the runner's classification |

### SourceSession state transitions

```text
Idle ──call──> Busy ──ok/error──> Idle
Busy ──RateLimited──> Limited ──reader retries──> Idle
Busy ──time/memory limit or engine fault──> Broken ──next call──> (new engine) Busy
```

## App (`HentaiReader.App`)

| Entity | Fields | Rules |
|--------|--------|-------|
| **Preferences** | language tag (`""` = system), adultConfirmed, lastSourceId | `%LOCALAPPDATA%\HentaiReader\preferences.json`; language applied before resources load |
| **SearchState** | sourceId, query text, filter values, items, nextCursor, status (`Idle` / `Loading` / `Error(kind)` / `End`) | New query/filter → cancel running call, clear items; append only for the current query |
| **GalleryViewState** | gallery, thumbnail load states per page | Thumbnails load on demand (virtualised grid) |
| **ImageCacheEntry** | key (url + crop-free), decoded image, byte size | In-memory LRU ≈ 150 MB; sprite decoded once and shared by its cells |

## Validation rules

- Filter values offered = manifest's declared filters only; values from the declared lists/ranges.
- Language preference ∈ {"", en-US, de-DE, fr-FR, es-ES, ja-JP, uk-UA}.
- No source request before `adultConfirmed = true` (FR-021).
