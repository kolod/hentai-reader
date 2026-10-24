# Product Overview — Hentai Reader

Scope of the whole product, used as input for individual feature specs (`NNN-short-name/`)
and, first of all, for the host API / source contract. Items marked **[C]** constrain the
contract.

Status: draft, 2026-10-23.

## Sources

| Source    | Access                                 | Notes                                        |
|-----------|----------------------------------------|----------------------------------------------|
| Hitomi.la | Anonymous                              | Runs the site's `gg.js` in the engine        |
| E-Hentai  | Anonymous or logged in (cookies)       | Login raises image limits                    |
| ExHentai  | Logged in only (cookies, `igneous`)    | Same script as E-Hentai, different domain    |

## 1. Browse & Search

- Front feed without a query: latest and popular (Hitomi: today/week/month/year). **[C]**
- Text and tag search using the source's syntax (`artist:`, `female:`, `language:`, `-tag`).
- Filters: language, category (Doujinshi, Manga, …), minimum rating (E-Hentai), page count.
  Each source declares which filters it supports; the UI renders only those. **[C]**
- Tag autocomplete (optional per source). **[C]**
- Result card: cover, title, artist, language, page count, key tags.
- Tag blacklist: galleries with blacklisted tags are hidden from results (applied by the host).
- Infinite scrolling of results. E-Hentai paginates by cursor (`?next=<gid>`), so pagination is
  an opaque cursor, not a page number. **[C]**

## 2. Gallery Details

- Titles (English and Japanese where available), cover, tags grouped by namespace, upload
  date, language, category, rating, page count, page-thumbnail grid.
- Tapping a tag starts a search for that tag.
- Actions: Read, Download, Add to favorites.

## 3. Reader

- Single mode: seamless vertical strip (no paged mode).
- Works both online (streaming + on-disk cache) and from a downloaded CBZ — same reader.
- Zoom (pinch / Ctrl+wheel), page scrubber, jump to page, reading progress saved and resumed.
- A failed page is retried in place without leaving the reader.
- Windows: keyboard (Space, PgDn, Home, End), maximum column width on wide screens.
- Page slots are reserved from known dimensions before images load (Constitution IV). **[C]**

## 4. Downloads

- Queue with pause/resume; continues in the background (Android: foreground service with
  notification); resumes after app restart; partial progress is kept.
- Per-source delays and concurrency from the script; stops on rate-limit signals and tells the
  user why (Constitution VI). **[C]**
- E-Hentai: choice of resampled (1280px, default) or original images (original consumes much
  more of the image limit).
- User-selectable library folder.
- Output: one CBZ per gallery with embedded metadata (Constitution V).

## 5. Library

- Downloaded galleries: sort and filter by source, tags, date, read/unread; text search.
- Import existing CBZ files (including archives made on the other platform).
- Delete galleries; reading history; local favorites (no sync with E-Hentai favorites in v1).

## 6. Accounts & Settings

- E-Hentai/ExHentai login via cookies (entered manually or captured from an in-app login page);
  cookies are stored locally and sent only to their source's domains. **[C]**
- Show remaining E-Hentai image limit where the site exposes it.
- Settings: UI language (6 languages), theme, preload depth (3–5), library folder, tag
  blacklist, default language filter, script update check with installed versions shown.

## Out of Scope (v1)

- Paged / right-to-left reading mode.
- App lock (PIN/biometric), hiding from recents, incognito mode.
- Sync of favorites or progress between devices or with site accounts.
- Sources other than Hitomi.la, E-Hentai, ExHentai (adding one = adding a script).

## Contract Implications

Findings that the contract spec must resolve:

1. **Cursor pagination.** `search(query, cursor)` returns `{ items, nextCursor | null }`.
2. **Structured query.** Text plus source-declared filters, not a raw string only; an empty
   query means the front feed.
3. **Host HTTP API is broader than `http.get`.** Hitomi search reads binary `.nozomi` indexes with
   HTTP Range requests; E-Hentai uses POST for `api.php` and login. Needed: request headers,
   POST bodies, binary responses, response headers/status. Resolved: Constitution 1.0.1 names
   `http.request` in Principle II.
4. **Page URL is not a string.** Hitomi images require a `Referer` header; E-Hentai offers an
   `nl` key to retry a failed image via another server. `getPageUrl` returns
   `{ url, headers }` and accepts a retry hint.
5. **Typed errors.** `RateLimited`, `LoginRequired`, `NotFound`, `SiteChanged` (parse failure),
   so the host can stop downloads correctly instead of retrying blindly.
6. **Source manifest.** id, display name, version, domains (cookie scoping), request delay,
   max concurrency, supported filters, login requirement, optional capabilities
   (autocomplete, popular feed).
7. **Page dimensions on E-Hentai** — decided (option A), see below. Gallery metadata must mark
   each page's size as `exact` or `estimated`.
8. **Resampled/original, ExHentai, remaining image limit** — decided in
   [002-ehentai-source](002-ehentai-source/spec.md) (contract 1.1): source *settings*
   (`image_quality`, `site` — ExHentai is a setting of the one E-Hentai source) and the optional
   *account status* capability for limit figures.

## Decision: E-Hentai Page Dimensions

Decided 2026-10-23: **option A**, codified in Constitution 1.0.1, Principle IV.

Hitomi returns exact width/height for every page in gallery metadata. E-Hentai exposes exact
dimensions only on each image page (`/s/…`, one request per page); the gallery page's thumbnail
grid gives only an approximate aspect ratio.

Options:

- **A (recommended):** reserve slots from the thumbnail aspect ratio; the exact size arrives with
  the `/s/` page, which is fetched anyway to resolve the image URL, while the page is still in
  the preload window (3–5 pages ahead), before it reaches the viewport. Corrections above the
  viewport are absorbed by scroll anchoring. Needs a PATCH clarification of Constitution IV
  ("exact dimensions or an aspect estimate refined before the page becomes visible").
- **B:** fetch all `/s/` pages up front before opening the reader — exact, but slow and costs
  requests against the site's limits (conflicts with Constitution VI).

## Roadmap (proposed feature order)

1. `001-source-contract` — host API `.d.ts`, console runner, Hitomi script. ✅
2. `002-ehentai-source` — E-Hentai/ExHentai script, contract 1.1. ✅
3. `003-windows-host` — WinUI 3 app shell, Jint + AngleSharp host passing the contract suite and
   replaying the source fixtures, browse/search/gallery screens, 6-language localisation. ✅
4. `004-windows-installer` — Windows installer (per-user or all users, six languages, verified
   content, silent upgrade as the basis for later self-update); portable build kept. ✅
5. `005-windows-reader` — seamless reader (online), source settings, E-Hentai login.
6. `006-cbz-downloads` — download queue, CBZ + metadata, library (Windows first).
7. `007-android-app` — Android host (QuickJS + Jsoup) and UI at parity.
8. `008-script-updates` — `sources-dist`, CI publishing, SHA-256 verification.

Windows comes first (decided 2026-10-23): faster development loop on the development machine,
and Jint is the engine the runner does not exercise yet, so contract portability is proven early.
UI framework: WinUI 3 (Constitution 1.0.3, Principle I).
