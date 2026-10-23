# Data Model: Source Contract, Script Runner & Hitomi Source

Field-level types are defined in [contracts/host-api.d.ts](contracts/host-api.d.ts); this document
adds relationships, validation rules and state. Validation rules are enforced by the generated
JSON Schema (R5) unless marked *(runtime)*.

## Relationships

```text
SourceManifest 1 ──< FilterDefinition
Source ──search──> SearchPage 1 ──< GallerySummary ──getGallery(id)──> Gallery
Gallery 1 ──< Page          Gallery + index ──getPageUrl──> PageImage
PageImage ──(on failure)──> RetryHint ──getPageUrl──> PageImage
Fixture pool 1 ──< FixtureEntry        Source 1 ──< TestCase 1 ── 0..1 Snapshot
```

## Entities

### Contract
- `version`: SemVer `MAJOR.MINOR.PATCH`, stored in `contract/package.json`, tagged `contract-vX.Y.Z`.
- Compatibility *(runtime)*: host supporting `H.h` accepts manifest `contract: "M.m"` iff
  `M == H` and `m <= h` (FR-002).

### SourceManifest
- `id` matches `^[a-z0-9-]{2,32}$`; never changes once published.
- `version` is SemVer; must equal the latest `sources-<id>-vX.Y.Z` tag on release *(CI)*.
- `domains` non-empty; each is a host name or `*.` + host name; no schemes, ports or paths.
- `politeness.minIntervalMs ≥ 0`, `politeness.maxConcurrency ≥ 1`.
- `filters`: each `id` at most once; `sort.default ∈ sort.values`; for `number`/`range`,
  `min ≤ max`.
- `capabilities.suggestTags == true` ⇔ module exports `suggestTags` *(runtime, conformance)*.

### SearchQuery
- `filters` keys ⊆ ids declared in the manifest; every value ∈ declared values / within range
  *(runtime, source-side → `InvalidInput`)*.
- `text == "" && filters == {}` ⇒ front feed.

### SearchPage
- `nextCursor`: opaque string or `null`; `null` ⇔ last page.
- `items` may be empty only when `nextCursor == null`.
- Items' `id`s unique within the page.

### GallerySummary / Gallery
- `id` non-empty string; `(manifest.id, id)` is the global key used by later features (DB, CBZ).
- `pageCount ≥ 1` (zero pages ⇒ source raises `SiteChanged`).
- `Gallery.pages.length == pageCount`; `pages[i].index == i`.
- `tags` in a summary: ≤ 20.
- `rating ∈ [0, 5]` or `null`; `uploadedAt` ISO 8601 or `null`.
- `sourceData` must survive a JSON round-trip unchanged *(runtime: runner round-trips it before
  passing to `getPageUrl`)*.

### Page
- `width > 0`, `height > 0`, integers.
- `size ∈ {exact, estimated}`. Hitomi: `exact` up to 2880 px on the long side, `estimated`
  (≤ 1 px error) above, because the site downscales those (FR-029, research R10a).

### ImageRequest / PageImage
- `url` absolute `https?://`; its host matched by `manifest.domains` *(runtime)*.
- `headers` names lower-case.
- `PageImage.width/height`, if present, > 0 and treated as exact.

### RetryHint
- `attempt ≥ 1`; `previous` is the exact `PageImage` returned last time (incl. `retryToken`).

### SourceError
- `kind ∈ {RateLimited, LoginRequired, NotFound, SiteChanged, InvalidInput, Network}`;
  `Network` raised by hosts only. `retryAfterSeconds ≥ 0` when present.
- Host classification *(runtime)*: non-`SourceError` exception ⇒ `ScriptError`; exceeded
  limits (60 s, 128 MiB) ⇒ `ScriptError` with details `{ limit }`.

### FixtureEntry
- Unique key: `method` + `url` + `range` + `bodySha256`.
- `response.body` path exists; content type not `image/*`; no `set-cookie` header stored.

### TestCase / Snapshot
- `call.fn` ∈ exported functions; `expectError` xor snapshot.
- `@<case>` references must point to an existing case without cycles.

## State

### Hitomi script state (inside the engine)
| State | Where | Lifetime |
|-------|-------|----------|
| `gg.js` text + fetch time | `storage` key `gg` | 10 min TTL; dropped on any `RetryHint` |
| Multi-term result id lists | module-level `Map` keyed by normalised query | while context lives (R7) |
| `galleriesindex`/tag index version | module-level | while context lives |

### Runner host state (per run)
| State | Lifetime |
|-------|----------|
| Per-source storage | in memory; seeded from `--storage` or case `storage`; discarded after run |
| Cookie jar | in memory; seeded from `--cookies` (live/record only) |
| Request log | in memory; used by `contract-test` assertions and `--verbose` |
| Frozen clock | offline: fixture `recordedAt`; live/record: real time |
