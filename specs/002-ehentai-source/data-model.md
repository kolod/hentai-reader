# Data Model: E-Hentai / ExHentai Source

Contract additions are typed in [contracts/contract-1.1.md](contracts/contract-1.1.md); this file
covers E-Hentai-specific identifiers, mappings, validation and state.

## Identifiers

| Thing | Format | Notes |
|-------|--------|-------|
| Gallery id | `e/<gid>/<token>` or `x/<gid>/<token>` | site prefix fixes where it is fetched (FR-018); `gid` digits, `token` 10 hex |
| Search cursor | `n:<gid>` | next listing page (`&next=<gid>`) |
| Toplist cursor | `p:<n>` | toplist page number, `n ≥ 1` |
| Page viewer ref | `{ token, page }` per page in `Gallery.sourceData.pages` | `/s/<token>/<gid>-<page>` |
| Retry token | `nl` key `<digits>-<digits>` | `PageImage.retryToken` |

Validation: anything not matching these formats → `InvalidInput`.

## Mappings

| Site | Contract |
|------|----------|
| Doujinshi, Manga, Artist CG, Game CG, Western, Non-H, Image Set, Cosplay, Asian Porn, Misc | `doujinshi`, `manga`, `artist_cg`, `game_cg`, `western`, `non_h`, `image_set`, `cosplay`, `asian_porn`, `misc` |
| `rating` "4.53" | `rating: 4.53` |
| `posted` unix seconds | `uploadedAt` ISO 8601 UTC |
| `filecount` | `pageCount` (number) |
| tag `ns:name` / `name` | `{namespace: ns, name}` / `{namespace: "other", name}` |
| `language:<name>` tag (not `translated`/`rewrite`) | ISO 639-1, else `null` |
| `title` / `title_jpn` | `title` / `titleOriginal` (`null` if empty) |
| `uploader` | `uploader` |
| `thumb` | `cover` (`ImageRequest`) |

Category bits for `f_cats`: research R3.

## Manifest (essentials)

- `id: "ehentai"`, `name: "E-Hentai"`, `contract: "1.1"`, `login: "optional"`.
- `domains`: `e-hentai.org`, `*.e-hentai.org`, `exhentai.org`, `*.exhentai.org`, `ehgt.org`,
  `*.ehgt.org`, `*.hath.network`.
- `politeness: {minIntervalMs: 500, maxConcurrency: 2}`.
- `filters`: `sort` [`latest`, `popular_day`, `popular_month`, `popular_year`], `type` (10 values),
  `min_rating` 2–5, `pages` 1–2000, `language` (site languages as ISO codes).
- `settings`: `image_quality` default `resampled`; `site` default `e-hentai`.
- `capabilities: {suggestTags: true, accountStatus: true}`.

## Page sizes (Constitution IV)

| Source of size | `size` | When |
|----------------|--------|------|
| Thumbnail cell `W×H` from the gallery page | `estimated` | `getGallery` |
| Viewer page `W x H` (resampled) or `Download original W x H` | exact, in `PageImage.width/height` | `getPageUrl` |

## State

| State | Where | Lifetime |
|-------|-------|----------|
| User's settings values | host | persistent per source |
| Login cookies | host cookie jar | persistent, domain-scoped |
| `nw` content-warning cookie | host cookie jar (site sets it on `?nw=always`) | as set by site |
| Nothing else | — | the script keeps no state of its own |

## Error mapping

| Signal | Kind |
|--------|------|
| 429, 509, `509.gif` image, "exceeded your image viewing limits", temporary IP ban (with time) | `RateLimited` (+ `retryAfterSeconds` when stated) |
| Original requested without login; ExHentai without access | `LoginRequired` |
| Gallery not available / `gdata` error entry | `NotFound` |
| Bad id, cursor, filter, popular sort with text/filters | `InvalidInput` |
| Expected elements missing | `SiteChanged` |
