# Feature Specification: Source Contract, Script Runner & Hitomi Source

**Feature Branch**: `001-source-contract`

**Created**: 2026-10-23

**Status**: Draft

**Input**: User description: "Host API contract (.d.ts), console script runner with stubbed host
API and recorded fixtures, and the Hitomi source script. Use specs/product-overview.md (section
"Contract Implications") as input: cursor pagination, structured query with source-declared
filters, http.request, getPageUrl returning {url, headers} with retry hint, typed errors, source
manifest, page dimensions marked exact/estimated."

## Context

This feature builds the foundation both apps depend on, before any UI exists:

- **The contract** — one authoritative description of (a) the capabilities a host offers to
  source scripts and (b) the functions every source script provides to the host.
- **The runner** — a console tool that executes a source script outside the apps, against
  recorded site responses (offline) or the live site.
- **The Hitomi source** — the first real source script, proving the contract works for a site
  with binary search indexes, obfuscated image URLs, and per-page dimensions.

Actors:

- **Source author** — writes and maintains source scripts (today: the project owner).
- **Host implementer** — implements the host capabilities inside the Android and Windows apps
  (later features); consumes the contract and its test suite.
- **Release pipeline** — publishes scripts only when their tests pass (Constitution VII).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - One authoritative contract (Priority: P1)

The source author and both host implementers work from a single description of what a host
provides and what a source must implement: data shapes, function signatures, error kinds, and the
contract version. A script that does not conform is rejected by an automatic check before it is
ever run.

**Why this priority**: Every later feature (both apps, E-Hentai, downloads) is built on this
contract. Changing it later costs changes on two platforms at once (Constitution II).

**Independent Test**: Run the conformance check against a deliberately non-conforming sample
script (missing function, wrong return shape) and against a conforming one; the first fails with
a message naming each violation, the second passes.

**Acceptance Scenarios**:

1. **Given** the contract description, **When** a source script omits a required function or
   returns a value of the wrong shape, **Then** the conformance check fails and names the
   function and the offending field.
2. **Given** a script whose manifest requires contract version X.Y, **When** a host supporting
   only an older minor version or a different major version loads it, **Then** the host refuses
   to run it and reports the version mismatch.
3. **Given** the contract, **When** a host implementer runs the host contract test suite against
   their host, **Then** each host capability is exercised and every deviation is reported by name.

---

### User Story 2 - Run a source offline against recorded responses (Priority: P1)

The source author runs any source function (`search`, `getGallery`, `getPageUrl`, tag
suggestions) from the command line. Network requests are answered from recorded site responses,
so results are deterministic, fast, and need no internet. Output is printed as structured,
human-readable data, and test cases compare it to expected results.

**Why this priority**: This is how scripts are developed and how the release pipeline decides
whether a script may be published (Constitution VII). Without it the Hitomi source cannot be
tested.

**Independent Test**: With a minimal sample source and a small set of recorded responses, run
each function from the command line and check that the output matches the expected result and
that a request with no recording fails with a message naming that request.

**Acceptance Scenarios**:

1. **Given** a source and its recorded responses, **When** the author runs `search` with a query,
   **Then** the runner prints the result page and the next cursor, without any network access.
2. **Given** offline mode, **When** the script makes a request that has no recording, **Then** the
   run fails immediately and names the method, URL and distinguishing headers of that request.
3. **Given** a script that tries to use anything outside the contract (file system, network,
   timers, globals not in the contract), **When** it runs in the runner, **Then** the run fails
   with a message naming the forbidden capability.
4. **Given** a test suite of input/expected-output cases for a source, **When** the author runs
   the suite, **Then** every case reports pass/fail with a diff of any mismatch, and the command
   exits with a failure status if any case fails.
5. **Given** the host contract test suite, **When** it is run against the runner's own host,
   **Then** all tests pass (the runner is the reference host).

---

### User Story 3 - Browse and search Hitomi (Priority: P2)

Through the runner (and later the apps), the source author gets Hitomi results: the front feed
(latest, and popular today/week/month/year), text search, tag search with exclusions, filtered by
language and gallery type, paged by cursor. Each result carries everything a result card needs.

**Why this priority**: First real-world proof that the contract handles a demanding site. Search
is the entry point to everything else in the apps.

**Independent Test**: Run the Hitomi search test cases offline; each returns the expected
galleries, in the expected order, with the expected next cursor.

**Acceptance Scenarios**:

1. **Given** an empty query, **When** `search` is called, **Then** the latest galleries are
   returned with a next cursor; **And** with a "popular: week" sort, the popular-this-week list is
   returned instead.
2. **Given** a query `female:glasses -male:muscle language:english`, **When** `search` is called,
   **Then** every result has the `female:glasses` tag, none has `male:muscle`, all are in English.
3. **Given** a free-text query matching a title, **When** `search` is called, **Then** galleries
   whose title matches are returned.
4. **Given** the last page of results, **When** `search` is called with its cursor, **Then** the
   result has no next cursor.
5. **Given** the source manifest, **When** the host reads the supported filters, **Then** it finds
   language, gallery type, and sort order with their allowed values, and no others.
6. **Given** a partial tag such as `female:gla`, **When** tag suggestions are requested, **Then**
   matching tags are returned with their namespace and gallery count.

---

### User Story 4 - Gallery details and page images for Hitomi (Priority: P2)

For a Hitomi gallery, the source returns full metadata — titles, tags by namespace, language,
type, date, page count, and the exact width and height of every page — and, for any page, an
image request the host can fetch directly, in WebP when available.

**Why this priority**: Exact page dimensions before image loading are the basis of the seamless
reader (Constitution IV); resolving image URLs is what makes reading possible at all.

**Independent Test**: Run `getGallery` and `getPageUrl` test cases offline; dimensions match the
recorded metadata exactly and image requests match the expected URL and headers.

**Acceptance Scenarios**:

1. **Given** a gallery id, **When** `getGallery` is called, **Then** every page has a width and a
   height of the image actually served — marked `exact`, or `estimated` (≤ 1 px error) where the
   site downscales large originals — and the page count equals the number of pages.
   *(Amended during implementation: the site serves WebP with the long side capped at 2880 px and
   its short-side rounding is not reproducible exactly — research R10a, SC-006 run.)*
2. **Given** a gallery and a page index, **When** `getPageUrl` is called, **Then** it returns an
   image request whose headers include everything the site requires (e.g. a Referer), and the
   format is WebP when the page is available in WebP.
3. **Given** an image request that failed, **When** `getPageUrl` is called again with a retry hint,
   **Then** the source refreshes whatever site data the URL depends on (e.g. the site's
   image-routing script) and returns a fresh request.
4. **Given** a gallery id that does not exist, **When** `getGallery` is called, **Then** the
   source fails with the `NotFound` error kind.

---

### User Story 5 - Record and refresh fixtures from the live site (Priority: P3)

When the site changes, the source author runs the runner in live mode, sees real results, and
records the responses as new fixtures. Recording automatically strips cookies and credentials,
never stores images, and keeps binary index data limited to the byte ranges actually requested.

**Why this priority**: Needed to keep fixtures current over time, but the first fixture set can be
recorded once; offline testing (US2) delivers value without it.

**Independent Test**: Run one search in record mode against the live site; the new fixture set
replays offline with identical output, and contains no cookies, credentials, or image data.

**Acceptance Scenarios**:

1. **Given** live mode, **When** the author runs a function, **Then** real requests are made,
   honouring the source's request delay and concurrency limit.
2. **Given** record mode, **When** a run completes, **Then** every response is saved, an offline
   replay of the same run produces identical output, and no saved response contains cookies,
   authorization headers, or image bodies.

### Edge Cases

- **Site changed structure**: a selector finds nothing or a response no longer parses → the
  source fails with `SiteChanged`, naming what was expected; it never returns partial nonsense.
- **Rate limiting**: HTTP 429/509 or a site-specific limit page → the source fails with
  `RateLimited` (with a retry-after time when the site gives one); the host does not retry it as
  an ordinary failure.
- **Unexpected script exception** (not one of the typed errors) → reported to the host as a script
  error with its message; treated as non-retryable for that call.
- **Network failure / timeout** → reported by the host itself as a network error, distinct from
  errors raised by the script; the script may let it propagate.
- **Stale image routing**: Hitomi image URLs depend on a frequently rotating site script; a
  previously valid URL may start failing → handled through the retry hint (US4, scenario 3).
- **Stale cover/thumbnail URLs** in previously returned results → the host refreshes them by
  calling `search` or `getGallery` again; no separate refresh function is required.
- **Gallery with zero pages, or a page without WebP** → zero pages is `SiteChanged`; a page
  without WebP falls back to the next best available format.
- **Very large result sets / index files**: binary indexes can be many megabytes → the source reads
  only the ranges it needs; recorded fixtures contain only those ranges.
- **Invalid cursor** (e.g. from an older script version) → the source fails with a typed error
  and the host restarts the search from the first page.
- **Unknown filter or filter value** passed by the host → the source does not silently ignore it;
  it fails with `InvalidInput` naming the filter.

## Requirements *(mandatory)*

### Functional Requirements

**Contract**

- **FR-001**: The contract MUST be described exactly once and be the single source of truth for
  host capabilities, source functions, data shapes, and error kinds (Constitution II).
- **FR-002**: The contract MUST carry a semantic version. A source's manifest MUST declare the
  contract version it requires; a host MUST refuse a source requiring a different major version
  or a newer minor version than it supports.
- **FR-003**: Host capabilities MUST be limited to: HTTP requests (GET/POST, request headers,
  request body, response status, response headers, text or binary body), HTML querying by CSS
  selector (on a document and within selected elements, yielding text, attribute values and
  inner markup), JSON parsing, per-source key-value storage, and sleeping.
- **FR-004**: Host capabilities that perform I/O or waiting MUST be asynchronous so a script can
  have several requests in flight; the host MUST enforce the source's concurrency limit and
  request delay regardless of how the script issues requests.
- **FR-005**: The host MUST send cookies only to the domains listed in the source's manifest,
  and MUST refuse requests to domains not listed there.
- **FR-006**: Every source MUST provide a manifest with: identifier, display name, script
  version, required contract version, domains, request delay, maximum concurrency, supported
  filters (name, kind, allowed values), login requirement, and optional capabilities offered
  (tag suggestions, popular feed).
- **FR-007**: Every source MUST implement `search(query, cursor)`, `getGallery(id)` and
  `getPageUrl(gallery, index, retryHint?)`; sources declaring tag suggestions MUST implement
  `suggestTags(prefix)`.
- **FR-008**: A search query MUST consist of free text (which may contain the source's tag
  syntax) plus a set of filter values drawn only from the filters the source declares. An empty
  text with no filters MUST mean the source's front feed.
- **FR-009**: `search` MUST return a page of gallery summaries and an opaque next cursor, or no
  cursor on the last page. The host MUST treat the cursor as opaque and pass it back unchanged.
- **FR-010**: A gallery summary MUST contain: gallery id, title, cover image request, language,
  page count, gallery type, and its most relevant tags; plus artist(s) where known.
- **FR-011**: A gallery MUST contain: id, titles (primary and, where available, original
  language), tags grouped by namespace, language, type, upload date, page count, cover image
  request, and an ordered list of pages; plus rating and uploader where the source has them.
- **FR-012**: Every page MUST have a width and a height, each page's size MUST be marked `exact`
  or `estimated`, and a page MAY carry a thumbnail image request.
- **FR-013**: A gallery MUST be self-contained and serializable: the host stores it and passes it
  back to `getPageUrl` without re-fetching; it MAY carry source-private data the host must not
  interpret.
- **FR-014**: Gallery ids MUST be strings, unique within their source; the pair (source
  identifier, gallery id) MUST be globally unique.
- **FR-015**: Every image reference (cover, thumbnail, page) MUST be an image request: a URL plus
  the headers required to fetch it.
- **FR-016**: `getPageUrl` MUST accept an optional retry hint describing the previous failed
  attempt, so the source can choose another server or refresh site data.
- **FR-017**: Sources MUST signal failures with typed error kinds: `RateLimited` (with optional
  retry-after), `LoginRequired`, `NotFound`, `SiteChanged`, `InvalidInput` (bad cursor or filter);
  hosts MUST additionally distinguish their own `Network` errors and untyped `ScriptError`s.
- **FR-018**: A host contract test suite MUST exist alongside the contract, exercising every host
  capability (including binary responses, POST, headers, storage isolation between sources,
  cookie domain scoping, concurrency and delay enforcement) with expected results, runnable
  against any host implementation (Constitution VII).
- **FR-019**: A conformance check MUST verify a source script's exported functions and returned
  data against the contract and report each violation by function and field.

**Runner**

- **FR-020**: The runner MUST execute any source function from the command line with arguments
  supplied by the author, printing the result as structured, human-readable data.
- **FR-021**: The runner MUST provide a reference host implementing every host capability and
  MUST pass the host contract test suite.
- **FR-022**: The runner MUST expose to scripts only the capabilities in the contract; any use of
  other capabilities MUST fail the run with a message naming the capability.
- **FR-023**: In offline mode, the runner MUST answer requests only from recorded fixtures,
  matching on method, URL, distinguishing headers (such as byte ranges) and body; an unmatched
  request MUST fail the run and name the request.
- **FR-024**: In offline mode the runner MUST skip politeness delays; in live and record modes it
  MUST honour them.
- **FR-025**: The runner MUST run a source's test suite (input → expected output cases), report
  pass/fail with a diff per case, and exit with a failure status if any case fails, so the
  release pipeline can gate publication on it.
- **FR-026**: In record mode the runner MUST save every response as a fixture, stripping cookies,
  set-cookie and authorization headers, refusing to store image bodies, and storing only
  requested byte ranges of large binary resources.
- **FR-027**: Per-source storage in the runner MUST start empty by default and MAY be seeded from
  a file, so tests can cover cached-state scenarios.

**Hitomi source**

- **FR-028**: The Hitomi source MUST support: front feed (latest; popular today, this week, this
  month, this year), free-text title search, tag search using Hitomi namespaces, tag exclusion,
  language filter, gallery-type filter, and combinations of these.
- **FR-029**: The Hitomi source MUST return, for every page, the width and height of the image it
  serves: `exact` when the original is served unscaled, otherwise `estimated` with at most 1 px
  error per side (amended during implementation, see US4-1).
- **FR-030**: The Hitomi source MUST produce image requests in WebP when available, falling back
  to the best other format, including all headers the site requires.
- **FR-031**: The Hitomi source MAY execute the site's own image-routing script inside the
  engine instead of reimplementing it (Constitution II), and MUST refresh it when given a retry
  hint or when it has expired.
- **FR-032**: The Hitomi source MUST implement tag suggestions.
- **FR-033**: The Hitomi source MUST ship with a test suite and fixtures covering every acceptance
  scenario of US3 and US4, and every typed error it can raise.

### Key Entities

- **Contract**: the versioned description of host capabilities and source functions.
- **Source manifest**: identity, versions, domains, politeness settings, supported filters,
  login requirement, optional capabilities of one source.
- **Filter definition**: name, kind (single choice / multiple choice / number), allowed values,
  default.
- **Search query**: free text + filter values.
- **Search page**: list of gallery summaries + optional next cursor.
- **Gallery summary**: card-level data for one result.
- **Gallery**: full metadata, ordered pages, optional source-private data.
- **Page**: index, width, height, size kind (exact/estimated), optional thumbnail request.
- **Image request**: URL + required headers.
- **Retry hint**: description of a failed previous attempt (status, server used, source-specific
  key).
- **Source error**: kind + message + optional details (e.g. retry-after).
- **Fixture**: a recorded request/response pair, keyed by request identity.
- **Test case**: function, arguments, expected output (or expected error kind).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The full Hitomi offline test suite runs in under 30 seconds on a typical developer
  machine with no network access, and is fully deterministic (identical results on 10
  consecutive runs).
- **SC-002**: 100% of US3/US4 acceptance scenarios are covered by passing offline test cases.
- **SC-003**: A deliberately broken sample script (one violation of each kind: missing function,
  wrong shape, forbidden capability, wrong contract version) is rejected with each violation
  named — 4 out of 4.
- **SC-004**: The runner's reference host passes 100% of the host contract test suite.
- **SC-005**: Against the live site, the first page of Hitomi search results (with full card data)
  is returned within 5 seconds on a typical broadband connection with default politeness
  settings; a gallery's metadata within 3 seconds.
- **SC-006**: For 20 randomly chosen live Hitomi galleries, sampling the first, middle and last
  page of each (60 pages), 100% of returned page dimensions match the actual image dimensions
  (exactly for `exact` pages, within 1 px for `estimated` pages) and 100% of returned page image
  requests load successfully.
- **SC-007**: Fixtures for the Hitomi source contain zero image bodies and zero cookies or
  credentials, and total under 5 MB.
- **SC-008**: Adding a second sample source requires no change to the runner or contract.

## Assumptions

- No app UI is part of this feature; the runner is the only host until the app features.
- The E-Hentai source is a separate feature (`002`), but the contract is designed for its known
  needs (cursor pagination, POST, login, estimated dimensions, retry via alternate server) so
  that it requires no breaking contract change.
- Hitomi requires no login; the contract's login-related parts are only exercised by the
  contract test suite in this feature.
- Fixtures for Hitomi necessarily include non-HTML/JSON responses: the site's image-routing
  script and byte ranges of binary search indexes. These are site responses, not images and not
  personal data; this is explicitly allowed by Constitution 1.0.2, Principle X.
- Politeness defaults for Hitomi: modest request delay and concurrency (exact values decided in
  the plan); search results need one extra request per result for card data, so concurrency is
  required to meet SC-005.
- Tag suggestions use the site's own suggestion data; their ranking follows the site.
