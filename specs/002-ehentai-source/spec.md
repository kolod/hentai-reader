# Feature Specification: E-Hentai / ExHentai Source

**Feature Branch**: `002-ehentai-source`

**Created**: 2026-10-23

**Status**: Draft

**Input**: User description: "E-Hentai and ExHentai source script on contract 1.x: front page and
search with cursor pagination (?next=), category and minimum-rating filters, gallery metadata with
estimated page sizes refined via /s/ pages, page image URLs with nl-based retry, login via cookies
(ipb_member_id, ipb_pass_hash, igneous), image-limit detection as RateLimited. Resolve: ExHentai as
same or separate source, how to expose remaining image limit, how the resampled/original choice
reaches the script. Input: specs/product-overview.md."

## Context

The second source, built on the contract and runner from feature 001. Unlike Hitomi, E-Hentai:

- paginates search results with a cursor (the id of the last gallery seen), not page numbers;
- exposes exact page sizes only on each page's own viewer page, one request per page;
- limits how many images a visitor may load; the limit is larger for logged-in users;
- has a members-only mirror (ExHentai) that needs login cookies and an access grant;
- offers each page as a resampled image (default) or as the original, which costs much more of
  the image limit.

This feature also settles the three contract questions left open by the product overview
(specs/product-overview.md): ExHentai's place, the remaining image limit, and how a user's
resampled/original choice reaches the script. Two of them require contract additions, released
as **contract 1.1** (backward compatible; Hitomi 0.1.0 keeps working unchanged).

Actors: the **source author** (develops and tests the script with the runner), the **reader**
(later, through the apps), the **host implementer** (implements contract 1.1 on every host).

## Decisions on the Open Questions

1. **Remaining image limit** → a new optional source capability, *account status*: the source
   reports whether the user is logged in and, where the site shows it, image-limit usage (used,
   maximum, when it resets). Hosts call it on demand (e.g. settings screen, before a large
   download). Contract 1.1, optional — sources without it are unaffected.
2. **Resampled / original** → a new manifest element, *source settings*: a source declares
   user-facing settings drawn from a contract vocabulary (like filters, so their labels stay in
   the apps' translated string resources — Constitution IX). The host stores the user's choice and
   passes the current values with every call that depends on them. E-Hentai declares
   `image_quality` ∈ {`resampled` (default), `original`}. Contract 1.1.
3. **ExHentai** → a `site` setting of the single E-Hentai source, values `e-hentai` (default) and
   `exhentai`, using the settings mechanism from item 2 (decided 2026-10-23, option A). One source
   in the source list, one script; the user chooses explicitly where to browse. Galleries keep the
   site they came from, so a gallery opened on ExHentai is always fetched from ExHentai.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Browse and search E-Hentai anonymously (Priority: P1)

Without logging in, the source author (and later the reader) gets the front page and search
results: free text and tag search with the site's syntax, category filter, minimum rating,
page-count range and language, endlessly paged forward with an opaque cursor. Each result carries
full card data.

**Why this priority**: Core of the source and the first real test of cursor pagination and of
fetching card data efficiently.

**Independent Test**: Run the `search-*` cases offline; each returns the expected galleries in
order with the expected next cursor.

**Acceptance Scenarios**:

1. **Given** an empty query, **When** `search` is called, **Then** the front page is returned with
   a next cursor; **And** calling again with that cursor returns the following galleries with no
   overlap.
2. **Given** a query `female:glasses` with categories Doujinshi and Manga and minimum rating 4,
   **When** `search` is called, **Then** every result is in one of those categories, has rating
   ≥ 4 and the tag `female:glasses`.
3. **Given** a page-count range 20–40 and language `en`, **When** `search` is called, **Then**
   every result has 20–40 pages and is in English.
4. **Given** the last page of a result set, **When** `search` is called with its cursor, **Then**
   there is no next cursor.
5. **Given** sort "popular" values the site supports (day, month, year), **When** `search` is
   called, **Then** the corresponding toplist is returned and paged.
6. **Given** a partial tag `female:gla`, **When** tag suggestions are requested, **Then** matching
   tags with namespaces are returned.

---

### User Story 2 - Gallery details with estimated page sizes (Priority: P1)

For a gallery, the source returns full metadata (titles, tags by namespace, category, language,
rating, uploader, upload date, page count) and every page with a size estimate taken from its
thumbnail, marked `estimated`, plus a thumbnail request.

**Why this priority**: The reader reserves page slots from these sizes (Constitution IV); without
them there is no seamless strip.

**Independent Test**: Run `gallery-*` cases offline; page count matches, every page has a size
marked `estimated` whose aspect ratio is within 2% of the real image (checked live, SC-003).

**Acceptance Scenarios**:

1. **Given** a gallery id, **When** `getGallery` is called, **Then** `pages.length` equals the page
   count and every page has width, height, `size: "estimated"` and a thumbnail.
2. **Given** a gallery spread over several thumbnail pages on the site, **When** `getGallery` is
   called, **Then** all pages are present in order.
3. **Given** a removed or nonexistent gallery, **When** `getGallery` is called, **Then** it fails
   with `NotFound`.
4. **Given** a gallery behind the site's content warning, **When** `getGallery` is called,
   **Then** it returns the gallery (the warning is acknowledged on the user's behalf).

---

### User Story 3 - Page images with exact sizes and retry (Priority: P1)

For any page, the source resolves the image request and reports the exact size of the image that
will be served (resampled or original per the user's setting), refining the estimate. When an
image fails, a retry asks the site for the same page from another server.

**Why this priority**: Makes reading possible; exact sizes arriving before the page is visible are
what keeps the strip from jumping (Constitution IV).

**Independent Test**: Run `page-*` cases offline; image requests and sizes match the recorded
viewer pages; the retry case yields a different server.

**Acceptance Scenarios**:

1. **Given** a gallery and a page index, **When** `getPageUrl` is called, **Then** it returns an
   image request plus exact width and height of that image.
2. **Given** a previous attempt failed, **When** `getPageUrl` is called with a retry hint,
   **Then** the source requests the page via the site's alternate-server mechanism and returns a
   different request.
3. **Given** setting `image_quality: original` and a logged-in user, **When** `getPageUrl` is
   called, **Then** the original image request and its exact size are returned.
4. **Given** setting `image_quality: original` and no login, **When** `getPageUrl` is called,
   **Then** it fails with `LoginRequired`.
5. **Given** the image limit is exhausted (the site serves its limit placeholder or limit
   page/status), **When** `getPageUrl` is called, **Then** it fails with `RateLimited` and the
   host stops — it never retries this as an ordinary failure (Constitution VI).

---

### User Story 4 - Logged-in use and account status (Priority: P2)

With login cookies in the host's cookie jar, the source uses the logged-in site (higher limits,
original images) and reports account status: logged in or not, and image-limit usage where the
site shows it.

**Why this priority**: Needed for original images, larger limits and the limit display, but the
source is useful anonymously first.

**Independent Test**: Run `account-*` cases offline with fixtures recorded logged in and logged
out; status matches; recorded fixtures contain no personal data.

**Acceptance Scenarios**:

1. **Given** no login cookies, **When** account status is requested, **Then** it reports
   "not logged in" and no limit figures.
2. **Given** valid login cookies, **When** account status is requested, **Then** it reports
   "logged in" and the current usage and maximum of the image limit (and reset time if shown).
3. **Given** cookies that the site rejects (expired), **When** account status is requested,
   **Then** it reports "not logged in" rather than failing.
4. **Given** any recorded fixture, **When** it is inspected, **Then** it contains no cookies,
   member id, password hash, user name or e-mail address.

---

### User Story 5 - ExHentai (Priority: P2)

A user whose login grants ExHentai access switches the source's `site` setting to ExHentai and
then browses, searches, opens galleries and reads pages there with the same behaviour as on
E-Hentai. A user without access is told so clearly instead of seeing empty results.

**Why this priority**: ExHentai has content E-Hentai hides, but it only matters for users with
access.

**Independent Test**: Run `ex-*` cases offline (recorded with an account that has access, and with
one that has not).

**Acceptance Scenarios**:

1. **Given** ExHentai access and setting `site: exhentai`, **When** browsing, **Then** results,
   galleries and pages come from ExHentai and work as in US1–US3.
2. **Given** no ExHentai access (the site's "no access" response), **When** ExHentai is used,
   **Then** the call fails with `LoginRequired` and a message saying ExHentai access is missing.
3. **Given** a gallery found on ExHentai, **When** the setting is switched back to `e-hentai` and
   the gallery is opened again, **Then** it is still fetched from ExHentai.

### Edge Cases

- **Search with no results** → empty page, no cursor (not an error).
- **Cursor from a different query, or malformed** → `InvalidInput`; the host restarts the search.
- **Gallery list layout changes** (site display modes, user-specific list settings) → the source
  reads only elements common to every layout; if those are missing, `SiteChanged`.
- **Temporary IP ban page** ("your IP address has been temporarily banned") → `RateLimited` with
  the remaining time as retry-after when stated.
- **HTTP 429/509** → `RateLimited`.
- **Gallery replaced by a newer version** → the old gallery is still returned; the newer version
  is not followed automatically.
- **Expunged gallery** → returned only if the site still serves it; otherwise `NotFound`.
- **Page viewer token missing for a page** (site glitch) → `SiteChanged` for that page only.
- **Original image requested for a page the site has no separate original for** → the served
  image is the original; size reported accordingly.
- **Sizes**: an estimated size never has zero width or height; if a thumbnail lacks dimensions,
  the source uses the gallery's typical aspect ratio, still marked `estimated`.

## Requirements *(mandatory)*

### Functional Requirements

**Contract 1.1 additions** (backward compatible)

- **FR-001**: The contract MUST gain an optional *account status* capability: a source declaring
  it MUST provide a function returning whether the user is logged in and, when known, image-limit
  usage (used, maximum, reset time). Hosts MUST NOT call it unless the capability is declared.
- **FR-002**: The contract MUST gain *source settings*: a manifest MAY declare settings, each with
  an id and allowed values from a contract vocabulary and a default. The host MUST persist the
  user's choice per source and MUST pass the current setting values to `getGallery`,
  `getPageUrl` (and `search`, `suggestTags` and account status, for uniformity) as an additional,
  optional argument. Sources not declaring settings receive defaults only.
- **FR-003**: The settings vocabulary MUST include `image_quality` with values `resampled`,
  `original`, and `site` with values `e-hentai`, `exhentai`; UI labels for vocabulary items live in
  app string resources (Constitution IX).
- **FR-004**: The gallery-type vocabulary MUST gain `asian_porn` (an E-Hentai category with no
  existing equivalent).
- **FR-005**: The contract version MUST become 1.1.0; sources declaring `contract: "1.0"` MUST keep
  working unchanged on 1.1 hosts; the runner, contract tests and conformance check MUST cover the
  additions.

**Browse and search**

- **FR-006**: The source MUST support the front page, free text and tag search with the site's
  syntax (namespaces, exclusion, quoted multi-word tags), and the filters: `type` (categories,
  multi), `min_rating` (2–5), `pages` (range), `language`, and `sort` (`latest` plus the popular
  periods the site offers).
- **FR-007**: Pagination MUST use an opaque cursor derived from the site's own forward-paging
  mechanism; no page-number guessing.
- **FR-008**: Card data for a result page MUST be obtained with as few requests as the site allows
  (target: one listing request plus at most one metadata request per page of results).
- **FR-009**: Results MUST be identical regardless of any user-level display preference stored by
  the site (list mode, thumbnail size, rows per page). *(Refined in the plan: the script relies only
  on layout-independent page elements and on the site's metadata API — research R3.)*

**Galleries and pages**

- **FR-010**: `getGallery` MUST return titles (primary and original), all tags with namespaces,
  category, language, rating, uploader, upload date, page count and every page with an
  `estimated` size from its thumbnail and a thumbnail request.
- **FR-011**: `getGallery` MUST acknowledge content warnings without user interaction.
- **FR-012**: `getPageUrl` MUST return the image request for the selected quality and the exact
  width and height of that image.
- **FR-013**: On a retry hint, `getPageUrl` MUST use the site's alternate-server mechanism and
  return a different request; the retry token MUST be carried in the returned image request.
- **FR-014**: Original quality without login MUST fail with `LoginRequired`; it MUST NOT silently
  fall back to resampled (the user chose original deliberately).

**Limits, login, ExHentai**

- **FR-015**: Signs of an exhausted image limit (the site's limit placeholder image, limit page,
  HTTP 509, HTTP 429, temporary ban page) MUST raise `RateLimited`, with retry-after when the site
  states a time.
- **FR-016**: Politeness defaults MUST be conservative (decided in the plan) and declared in the
  manifest; the source MUST never issue background requests the host did not ask for.
- **FR-017**: The source MUST work with and without login cookies; login cookies are only ever
  supplied by the host and sent only to the site's own domains (Constitution VI).
- **FR-018**: With setting `site: exhentai`, search, galleries and pages MUST come from ExHentai;
  a gallery id MUST identify its site, so `getGallery`/`getPageUrl` use the gallery's own site
  regardless of the current setting. Lack of ExHentai access MUST raise `LoginRequired` with an
  explanatory message.

**Testing and hygiene**

- **FR-019**: The source MUST ship offline test cases covering every acceptance scenario above and
  every error kind it raises, recorded with the runner (anonymous, logged-in, and with/without
  ExHentai access).
- **FR-020**: Fixtures MUST be redacted like Hitomi's (allowlist of what the tests need; all other
  gallery text neutral) and MUST additionally contain no personal data: user name, member id,
  e-mail, password hash, or any account-identifying token (Constitution X).
- **FR-021**: Recording logged-in fixtures MUST use a cookie file that never enters the repository.

### Key Entities

- **Account status**: logged in (yes/no), image limit used, image limit maximum, reset time
  (optional), ExHentai access (yes/no/unknown).
- **Source setting**: id from the contract vocabulary, allowed values, default; the user's current
  value is owned by the host.
- **Setting values**: the map of current values passed with each call.
- **E-Hentai gallery reference**: the site identifies a gallery by a numeric id plus a token; both
  are part of the gallery id exposed to hosts.
- **Page viewer reference**: per-page token needed to resolve the page image; carried in the
  gallery's source-private data.
- **Retry token**: the site's alternate-server key from the previous attempt.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The full offline E-Hentai suite runs in under 30 seconds and is deterministic over
  10 runs.
- **SC-002**: A page of search results with full card data takes at most 2 site requests and
  returns within 5 seconds live with default politeness.
- **SC-003**: For 10 random live galleries (first, middle, last page each), every estimated size
  is within 2% of the real aspect ratio, and every exact size returned by `getPageUrl` matches the
  image actually served.
- **SC-004**: A user who hits the image limit sees downloading stop on the first limited response;
  0 further image requests are issued for that source until the host decides to resume.
- **SC-005**: Fixtures contain 0 cookies, 0 personal identifiers, 0 images, and total < 5 MB.
- **SC-006**: Hitomi 0.1.0 (contract 1.0) passes its full suite unchanged on the contract 1.1 host.
- **SC-007**: The contract test suite covers 100% of the 1.1 additions (settings passing, account
  status capability gating).

## Assumptions

- The source author has an E-Hentai account (and, for US5, ExHentai access) and provides cookies in
  a gitignored file only for recording; nobody else needs an account to run the offline tests.
- How the apps let users log in and enter cookies (web login page vs. manual entry) is decided in
  the app features; this feature only defines what the source needs from the host's cookie jar.
- E-Hentai list and gallery layouts can be forced per request (display-mode parameters), so a
  user's own site preferences do not change what the script parses.
- Page sizes from thumbnails are proportional to the real images closely enough for slot
  reservation; exact sizes always come from the page viewer (Constitution IV, as amended in 1.0.1).
- Popular periods available on E-Hentai do not include "week"; the manifest declares only what
  the site has.
- Politeness values are chosen in the plan from the site's published guidance and common practice.
