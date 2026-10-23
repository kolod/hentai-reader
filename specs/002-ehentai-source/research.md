# Research: E-Hentai / ExHentai Source

Phase 0 output for [plan.md](plan.md). Facts marked ✔ were verified anonymously against the live
site on 2026-10-23 (HTML/JSON only, ≥ 2 s between requests, no images stored). Facts marked ◇
need logged-in or ExHentai cookies and are confirmed while recording fixtures (task in US4/US5).

## R1. Contract 1.1 — source settings

**Decision**: `SourceManifest.settings?: SettingDefinition[]` from a contract vocabulary
(`image_quality`: `resampled` | `original`; `site`: `e-hentai` | `exhentai`), each with a default.
The host persists the user's value per source and passes **`settings: SettingValues` as an extra
trailing argument** to every source function — but **only to sources declaring contract ≥ 1.1**;
1.0 sources are called with exactly the 1.0 arity. For 1.1 sources an absent retry hint is passed
as `null`.

**Rationale**: Adds no host capability (Principle II's list stays as it is), so no constitution
amendment. The 1.0/1.1 arity rule matters: Hitomi 0.1.0 tests `retry !== undefined`; passing
`null, settings` to it would force a `gg.js` refresh on every call (SC-006).

**Alternatives**: a `settings.get(id)` host global (new capability → constitution amendment);
settings inside `storage` (host would write a script's private store; mixes ownership);
settings encoded as filters (filters are per query, settings are per user).

## R2. Contract 1.1 — account status, image crop, `asian_porn`, image fetching

- **Account status**: `capabilities.accountStatus?: boolean` + optional export
  `getAccountStatus(settings) → AccountStatus` (`loggedIn`, optional `imageLimit {used, max,
  resetsAt | null}`, optional `exhentai: boolean | null`). Hosts call it only when declared.
- **Sprite thumbnails** ✔: E-Hentai page thumbnails are cells of a sprite sheet
  (`background: url(…) -200px 0`). `ImageRequest.crop?: {x, y, width, height}` (sprite pixels)
  lets hosts cut the cell. Without it EH galleries could not show a page grid.
- **`GalleryType` gains `asian_porn`** (E-Hentai category with no equivalent).
- **Clarification of image fetching** (documentation, same release): hosts fetch every
  `ImageRequest` with the source's cookie jar (domain-scoped, as for `http.request`) and under the
  same politeness limiter. Needed for original images (`fullimg` requires login cookies) and
  already implied by Constitution VI.
- Contract version **1.1.0**; all additions optional → 1.0 manifests and sources stay valid.

## R3. Listing and cursor ✔

- Front page `https://e-hentai.org/` (ExHentai: `https://exhentai.org/`); 25 galleries per page.
- Search parameters ✔: `f_search` (site syntax; quoted tags end with `$` for exact match),
  `f_cats` = 1023 − Σ(bits of included categories) — Misc 1, Doujinshi 2, Manga 4, Artist CG 8,
  Game CG 16, Image Set 32, Cosplay 64, Asian Porn 128, Non-H 256, Western 512 — `f_srdd`
  (min rating 2–5), `f_spf`/`f_spt` (page range). Verified: categories, page range and tag were
  all honoured in a combined query.
- Language filter → appended to `f_search` as `language:<name>$`.
- Forward paging ✔: `a#unext` / `a#dnext` → same query + `&next=<gid>`; absent on the last page.
  **Cursor = `n:<gid>`**; malformed → `InvalidInput`.
- **Layout independence** (FR-009): list layouts differ (`itg glte`, `gltc`, `gltm`, `gld`), but
  every layout links each gallery as `/g/<gid>/<token>/`. The script reads only those links (in
  document order, de-duplicated) and the `unext` link; card data comes from the API (R4).
  `inline_set=…` is avoided: it answers with a redirect and changes the user's stored preference.
- **Popular** ✔: toplists `toplist.php?tl=15` (yesterday), `13` (month), `12` (year); 50 per page,
  `&p=<n>`. Manifest `sort` values: `latest`, `popular_day`, `popular_month`, `popular_year` (no
  week on the site). Cursor `p:<n>`. A popular sort combined with text or filters →
  `InvalidInput` (toplists cannot be filtered).

### R3a. Found while implementing (2026-10-23)

- `f_srdd` filters by **rounded stars** (a 3.93 rating passes "4"). The contract's `min_rating` is
  numeric, so the script re-checks each result against the exact `gdata` rating; a result page
  can therefore hold fewer than 25 items while the cursor stays the site's.
- No "popular right now" box appears in any listing layout; links are still read only inside
  `.itg`.
- `getGallery` cost grows with size: one thumbnail page per 20 pages (anonymous), e.g. 28 requests
  ≈ 15 s for a 545-page gallery at 500 ms — politeness, not a defect.
- Live SC-003 run: 10 galleries × 3 pages, 30/30 within 2 % aspect, all exact sizes equal.
- Original images: the viewer page shows the "Download original" link to anonymous users too and
  carries no login marker, so `original` checks login once via `home.php` (cached 10 min).

## R4. Metadata API ✔

- `POST https://api.e-hentai.org/api.php` with JSON. Response body is JSON (content-type is
  wrongly `text/html`, so the script parses the text).
- `{"method":"gdata","gidlist":[[gid,"token"],…],"namespace":1}` — up to 25 galleries per call →
  **one call per result page** (FR-008, SC-002). Fields: `title`, `title_jpn`, `category`, `thumb`
  (cover on `ehgt.org`), `uploader`, `posted` (unix seconds, string), `filecount` (string),
  `rating` (string), `expunged`, `tags` (`ns:tag`; untyped tags have no prefix).
- Language: from `language:` tags, ignoring `language:translated` / `language:rewrite`; none →
  `null` (no guessing).
- `{"method":"tagsuggest","text":"female:gla"}` ✔ → `{"tags":{id:{ns,tn,(mns,mtn for slaves)}}}`;
  no counts (`count: null`). Slave tags map to their master (`mns:mtn`), de-duplicated.
- ExHentai uses the same API host ◇ (with cookies) — confirm.

## R5. Gallery page ✔

- `https://e-hentai.org/g/<gid>/<token>/?p=<n>` — thumbnails in `#gdt`, each
  `a[href="…/s/<pagetoken>/<gid>-<page>"]` containing a `div` with
  `style="width:Wpx;height:Hpx;background:transparent url(SPRITE) -Xpx Ypx no-repeat"` and
  `title="Page N: <file name>"`. Large (`gt200`) and normal (`gt100`) modes share this shape.
- Thumbnails per gallery page depend on user settings (20 anonymous) → page count of the
  thumbnail listing comes from the pager (`.ptt` links `?p=<n>`), never assumed.
- **Estimated page size** = thumbnail cell `W×H` (`size: "estimated"`); aspect checked live
  (SC-003). Thumbnail `ImageRequest` = sprite URL + `crop {x: X, y: Y, width: W, height: H}`.
- `getGallery` = 1 `gdata` call + ⌈pages / perPage⌉ gallery pages, fetched sequentially (the host
  serialises anyway at `maxConcurrency` 2).
- Content warning ◇: the gallery page shows a warning with `?nw=session` / `?nw=always` links;
  on detection the script repeats the request with `?nw=always`.
- Removed gallery: the page says the gallery is not available / `gdata` returns an error entry →
  `NotFound`.

## R6. Page viewer and retry ✔

- `https://e-hentai.org/s/<pagetoken>/<gid>-<page>` → `img#img[src]` (H@H server on
  `*.hath.network`, often a non-standard port) with inline `style="width:Wpx;height:Hpx"`, and
  `<div>name :: W x H :: size</div>` → **exact served size**.
- Retry ✔: `onerror="… nl('<key>')"` → retry URL is the same viewer URL plus `?nl=<key>`; the key
  is returned as `PageImage.retryToken` and read back from `RetryHint.previous.retryToken`.
- Original ✔: link text `Download original W x H …` with `href …/fullimg/<gid>/<page>/<key>/<name>`.
  Anonymous → link absent or pointing to login → `LoginRequired` (FR-014). Image request = the
  `fullimg` URL (host follows its redirect with the source's cookies, R2).
- Limit ◇: an exhausted limit serves the placeholder `…/509.gif` as `img#img`, or a text page
  "You have exceeded your image viewing limits", or HTTP 509 → `RateLimited`. Temporary IP ban
  text "…temporarily banned… expires in N minutes (and M seconds)" → `RateLimited` with
  `retryAfterSeconds`. Live triggering is not attempted; cases use hand-made fixtures.

## R7. Login, account status, ExHentai

- Login cookies (host-owned): `ipb_member_id`, `ipb_pass_hash`; ExHentai additionally `igneous`.
  Domains `e-hentai.org`, `exhentai.org` and subdomains.
- `home.php` anonymous ✔ → 302 to `bounce_login.php` → `loggedIn: false`. Logged in ◇: "You are
  currently at **X** towards your limit of **Y**" → `imageLimit {used: X, max: Y, resetsAt: null}`
  (the site regenerates limits continuously; no fixed reset time).
- ExHentai anonymous ✔: `https://exhentai.org/` → 302 to `forums.e-hentai.org/remoteapi.php?ex=…`;
  without access the final response is not a gallery listing ◇ → `LoginRequired` ("ExHentai
  access is missing"). With access ◇: same markup as E-Hentai on `exhentai.org`.
- Gallery ids carry the site: `e/<gid>/<token>` or `x/<gid>/<token>` (FR-018).

## R8. Politeness

**Decision**: `minIntervalMs: 500`, `maxConcurrency: 2`.

**Rationale**: The site's API guidance allows only a few rapid sequential requests before a pause;
scrapers that load pages fast get temporary IP bans. One result page = 2 requests (≈ 1 s +
latency, SC-002 ≤ 5 s). Reading = 1 viewer request + 1 image per page → ≈ 1 page/s, ahead of
reading speed with 3–5 pages preloaded.

**Alternatives**: 1000 ms/1 (safer, but a 300-page `getGallery` takes > 15 s); 250 ms/4 (ban risk).

## R9. Fixture redaction and personal data

**Decision**: `sources/ehentai/redact.mjs` reduces each response to the elements the script reads
(the Hitomi allowlist approach, extended to HTML):
- listings → a minimal page with only the gallery links and `unext`;
- gallery pages → `#gdt` thumbnail cells, `.ptt` pager and the content-warning marker only
  (titles, tags, comments with user names are dropped);
- viewer pages → `img#img`, the size `div` (file name → `001.ext`), the `nl(...)` key, the
  original link;
- `gdata` JSON → titles `Gallery <gid>`, uploader `uploader`, tags outside the test allowlist
  → `redacted N`;
- `home.php` → only the limit sentence.

Result: no gallery text beyond the allowlist and no personal data (user name, member id, e-mail,
forum links with ids) can survive — the page parts that carry them are not kept at all
(FR-020, Constitution X). `check` re-applies the redactor (idempotence) as for Hitomi.

## R10. Runner support

- `run --setting key=value` (repeatable); test cases gain `"settings": {…}`; defaults come from
  the manifest; unknown keys/values → usage error.
- `RESULT_TYPES.getAccountStatus = "AccountStatus"`; `check`: `accountStatus` declared ⇔ export
  present; every setting default ∈ its values.
- New contract test `settings-passing` (harness passes settings to `run()`), and engine-level unit
  tests for the 1.0/1.1 arity rule.
- `live-dimension-check.ts`: estimated pages compared by aspect ratio (≤ 2 %, SC-003); exact sizes
  from `getPageUrl` compared exactly with the image header.
