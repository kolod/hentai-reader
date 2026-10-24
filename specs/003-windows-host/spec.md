# Feature Specification: Windows App Shell and Source Host

**Feature Branch**: `003-windows-host`

**Created**: 2026-10-23

**Status**: Draft

**Input**: User description: "Windows app shell in C# + WinUI 3 with a Jint + AngleSharp host
implementing contract 1.1 (http.request with cookie jar and politeness, html.select with the
portable selector subset, storage, sleep, console, SourceError prelude, settings calling
convention). The host must pass the contract test suite and replay the Hitomi and E-Hentai offline
fixtures with results identical to the runner. Minimal UI: source list, search with source-declared
filters and infinite results, gallery details screen with page thumbnails (sprite crop). All UI
strings localised in en, de, fr, es, ja, uk from the first screen; in-app language switch. Input:
specs/product-overview.md, contract/host-api.d.ts."

## Context

The first real app (Constitution 1.0.3: Windows first, WinUI 3). It has two halves:

- **The source host inside the app** — runs the same source scripts as the console runner, through
  the same contract, on a different script engine. It must behave identically: the contract test
  suite and every recorded source test case are the proof.
- **A minimal shell on top of it** — pick a source, search and browse results endlessly, open a
  gallery and see its details and page thumbnails. Reading pages, source settings and login are
  feature 004; downloads are feature 005.

Actors:

- **Reader** — the person using the app on Windows (adult, personal use).
- **Host implementer / source author** — needs proof that scripts behave the same on Windows as in
  the runner, and a quick way to see a script working in a real UI.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Same behaviour as the runner (Priority: P1)

The app's source host runs the portable contract test suite and every recorded test case of every
bundled source, offline, and produces exactly the results the runner produced. This is checked
automatically, without opening the app's windows.

**Why this priority**: Everything the app shows comes from scripts; if the Windows host behaves
differently from the runner, every later feature is built on sand. It is also the first time the
contract runs on a second script engine (Constitution II, VII).

**Independent Test**: Run the Windows host's headless check: all contract tests pass and every
source case result equals its stored snapshot.

**Acceptance Scenarios**:

1. **Given** the contract test suite, **When** the Windows host runs it, **Then** every test passes,
   including politeness timing, cookie scoping, storage isolation and settings passing.
2. **Given** the recorded test cases of Hitomi, E-Hentai and the reference samples, **When** the
   Windows host replays them offline, **Then** every result equals the stored snapshot and every
   expected error kind matches.
3. **Given** a script that uses a capability outside the contract, **When** the Windows host runs
   it, **Then** the call fails and names the capability, as in the runner.
4. **Given** a source declaring contract 1.0 and one declaring 1.1, **When** the host calls them,
   **Then** each receives exactly the arguments its contract version prescribes.

---

### User Story 2 - Browse and search a source (Priority: P1)

The reader starts the app, sees the bundled sources, picks one and immediately sees its front feed.
They type a query in the source's own syntax, set the filters the source offers (sort, language,
type, rating, page range — whichever it declares), and scroll results endlessly; more results load
as they near the end. Each result card shows cover, title, language, type, page count and key tags.

**Why this priority**: The first user-visible value; also proves the host works with live sites.

**Independent Test**: Launch the app, choose Hitomi, search `female:glasses` with language English,
scroll through at least three pages of results.

**Acceptance Scenarios**:

1. **Given** the app is started, **When** the reader picks a source, **Then** the source's front
   feed appears.
2. **Given** a source that declares filters, **When** the reader opens the filter panel, **Then**
   exactly the declared filters are offered, with translated labels and values.
3. **Given** a result list, **When** the reader scrolls near its end, **Then** the next page loads
   and is appended without the list jumping; when there is no next page, the list says so.
4. **Given** a search with no results, **When** it completes, **Then** an empty-state message is
   shown.
5. **Given** the source reports a rate limit, a missing login or a site change, **When** a search
   fails, **Then** a translated, specific message is shown; after a rate limit no further requests
   are sent to that source until the reader retries.
6. **Given** a slow network, **When** a search is running, **Then** the window stays responsive and
   the search can be cancelled by starting another one.

---

### User Story 3 - Gallery details with page thumbnails (Priority: P2)

From a result, the reader opens the gallery screen: titles, tags grouped by namespace, language,
type, upload date, rating and uploader where available, page count, and a grid of page thumbnails —
including sources whose thumbnails are cells of a larger sprite image, which are shown cropped
correctly. Clicking a tag starts a search for that tag in the same source.

**Why this priority**: Completes the browse loop; the reader (004) starts from here.

**Independent Test**: Open an E-Hentai gallery and a Hitomi gallery; every page thumbnail shows its
own page; clicking a tag runs a search for it.

**Acceptance Scenarios**:

1. **Given** a result, **When** the reader opens it, **Then** the gallery's details and a thumbnail
   grid with one thumbnail per page are shown.
2. **Given** a source whose thumbnails are sprite cells, **When** the grid is shown, **Then** each
   thumbnail shows only its own cell.
3. **Given** a tag in the gallery screen, **When** the reader clicks it, **Then** a search for that
   tag starts in the same source.
4. **Given** a gallery that no longer exists, **When** it is opened, **Then** a "not found" message
   is shown.

---

### User Story 4 - Six languages from the first screen (Priority: P2)

Every piece of interface text is available in English, German, French, Spanish, Japanese and
Ukrainian. The app follows the system language by default; the reader can choose another language
in the app's settings. Counts use correct plural forms, dates and numbers follow the chosen locale,
and long German or Japanese text is never cut off. Gallery titles and tags are shown as the site
provides them.

**Why this priority**: Constitution IX requires localisation from the first screen; retrofitting it
later costs more than building it in.

**Independent Test**: Switch through all six languages; every visible string changes, no string is
shown as a raw key or in English (unless English is selected), and nothing is clipped.

**Acceptance Scenarios**:

1. **Given** a system language among the six, **When** the app starts the first time, **Then** it
   uses that language; otherwise English.
2. **Given** the settings screen, **When** the reader picks a language, **Then** the app restarts
   itself in that language on the same source, and the choice is remembered across restarts.
3. **Given** counts such as "1 page / 2 pages / 5 pages", **When** shown in Ukrainian, **Then** the
   correct plural form is used for each number; in Japanese, no plural forms are invented.
4. **Given** filter and type labels, **When** shown in any language, **Then** they are translated
   (they come from the contract vocabulary, not from the script).

---

### User Story 5 - Adult-content confirmation (Priority: P3)

On first launch the app asks the reader to confirm they are an adult before showing any content;
the answer is remembered.

**Why this priority**: The content is adults-only (product constraints); a one-time gate is the
expected minimum for such software, but it does not block development of the rest.

**Independent Test**: Start with a fresh profile; no source content is fetched or shown before
confirming; after confirming once, the gate does not reappear.

**Acceptance Scenarios**:

1. **Given** a fresh installation, **When** the app starts, **Then** a confirmation is shown and no
   network request to any source is made until the reader confirms.
2. **Given** the reader declines, **When** they answer, **Then** the app closes.

### Edge Cases

- **Script throws an unexpected error** → shown as a generic "the source failed" message with a way
  to copy technical details; the app keeps running.
- **Script exceeds the time or memory limit** → the call fails like any other source error; the
  engine for that source is restarted for the next call.
- **Image request fails or is refused** (domain not declared) → placeholder shown in its slot;
  the layout does not change.
- **No network** → specific message; the source list still opens.
- **Very long titles or tag lists** → wrapped or trimmed with a tooltip, never overflowing.
- **High-DPI and window resizing** → result grid reflows; thumbnails keep their aspect ratio.
- **Two searches in quick succession** → only the latest result is shown; the earlier is cancelled.
- **Bundled script requires a newer contract than the app supports** → the source is listed as
  unavailable with a message, not hidden silently.

## Requirements *(mandatory)*

### Functional Requirements

**Source host (contract 1.1)**

- **FR-001**: The app MUST host source scripts with every capability of contract 1.1 and nothing
  else: HTTP requests (with per-source cookie jar, domain allow-list, politeness limits, redirects,
  text and binary bodies), HTML selection restricted to the contract's portable selector subset,
  JSON parsing, per-source storage, sleeping, a debug-only console, and the shared error type.
- **FR-002**: The host MUST follow the contract's calling convention: 1.0 sources with the 1.0
  arguments, 1.1 sources with the current settings and a null retry hint.
- **FR-003**: The host MUST enforce the contract's limits (time and memory per call) and keep
  script execution, networking and image decoding off the UI thread (Constitution IV).
- **FR-004**: Images (covers, thumbnails) MUST be fetched with the source's cookies and through the
  same politeness limiter as the source's requests, with the headers the source specifies, and
  sprite thumbnails MUST be cropped to their declared region (contract 1.1).
- **FR-005**: Per-source storage MUST persist across app restarts and stay isolated between sources.
- **FR-006**: The app MUST ship the current scripts of every source in `sources/` as its built-in
  copy (Constitution III); script updates are out of scope (feature 007).
- **FR-007**: Settings of 1.1 sources MUST be passed with their manifest defaults in this feature
  (the settings screen for sources is feature 004).

**Parity proof**

- **FR-008**: A headless check MUST run the contract test suite and every source test case
  (`sources/*/tests`, `contract/conformance/*/tests`) against the Windows host offline, using the
  same fixture pools, frozen clock and comparison rules as the runner, and fail on any difference.
- **FR-009**: The headless check MUST run in continuous integration on every change to the
  Windows app, the contract or a source.

**Browse and gallery UI**

- **FR-010**: The source list MUST show every bundled source with its display name and state
  (available, or unavailable with the reason).
- **FR-011**: Search MUST accept free text in the source's syntax and render controls only for the
  filters the source declares, using translated labels from the contract vocabulary.
- **FR-012**: Results MUST page endlessly via the source's cursor, preserve scroll position when
  appending, show progress while loading and a clear end-of-results state.
- **FR-013**: Errors MUST be shown per kind (rate limit, login required, not found, site changed,
  invalid input, network, script error) with translated messages; after `RateLimited` the app MUST
  NOT send further requests to that source until the reader explicitly retries (Constitution VI).
- **FR-014**: The gallery screen MUST show all metadata the source returns, tags grouped by
  namespace, and one thumbnail per page; clicking a tag MUST search for it in the same source.
- **FR-015**: Starting a new search or leaving a screen MUST cancel the work started for the
  previous one.

**Localisation (Constitution IX)**

- **FR-016**: Every UI string MUST come from the app's string resources in en, de, fr, es, ja, uk;
  English is the source of truth for keys and the fallback for missing translations.
- **FR-017**: Plurals MUST use the platform's plural rules; dates, numbers and sizes MUST be
  formatted for the chosen locale.
- **FR-018**: The interface language MUST be selectable in the app independently of the system
  language and remembered.
- **FR-019**: Layouts MUST fit long German strings and Japanese text without clipping.
- **FR-020**: Source content (titles, tags, names) MUST be shown untranslated.

**Shell**

- **FR-021**: First launch MUST require adult-content confirmation before any source request.
- **FR-022**: The app MUST run on Windows 10 (1903+) and 11 as a portable application (a folder
  that runs without installation). The installer is produced with the first public release
  (Constitution "Platform & Distribution Constraints"; decided in the plan, research R7).

### Key Entities

- **Bundled source**: script, manifest, availability state.
- **Source session**: one running script engine for a source with its storage, cookie jar,
  politeness limiter and current settings.
- **Search state**: query, filter values, loaded result pages, next cursor, loading/error state.
- **Gallery view**: the gallery returned by the source plus thumbnail load states.
- **App preferences**: interface language, adult confirmation, last used source.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% of contract tests pass on the Windows host, and 100% of recorded source test
  cases (Hitomi, E-Hentai, reference samples) produce results identical to their snapshots.
- **SC-002**: From app start to the source list takes under 2 seconds on a typical PC.
- **SC-003**: The first page of results appears within 5 seconds of a search on a typical
  broadband connection with default politeness (the same bound as the sources' own targets).
- **SC-004**: Scrolling through 500 loaded results shows no visible stutter, and the window reacts
  to input within 100 ms while searches and image loads are running.
- **SC-005**: Every visible string exists in all six languages; a review of every screen at
  1024 px window width and 150 % scaling finds no clipped text in German or Japanese.
- **SC-006**: For galleries with sprite thumbnails, 100% of sampled thumbnails show their own page.
- **SC-007**: After a rate-limit error, 0 further requests are sent to that source until the
  reader retries.

## Assumptions

- Reading pages, per-source settings UI (image quality, ExHentai site), E-Hentai login, tag
  blacklist and the image disk cache are feature 004; downloads and library are feature 005.
- Covers and thumbnails are cached in memory only in this feature.
- Windows 10 version 1903 or newer: the system's ICU library (plural rules, Constitution IX) ships
  from 1903 on; x64 first, ARM64 if it costs nothing extra.
- Changing the interface language restarts the app and returns to the same source (the platform
  applies a language override only before resources load; research R6).
- The headless parity check runs on a Windows CI runner; the existing Linux CI job stays for the
  runner and sources.
- The adult-content gate is a simple confirmation, not age verification.
- System language detection uses the user's Windows display language list; the first of the six
  languages found wins.
