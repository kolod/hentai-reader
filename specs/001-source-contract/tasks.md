---

description: "Task list for feature 001-source-contract"
---

# Tasks: Source Contract, Script Runner & Hitomi Source

**Input**: Design documents from `/specs/001-source-contract/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md),
[data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: MANDATORY for this feature (Constitution VII): the host contract suite, the Hitomi
offline suite, and `node:test` unit tests for runner internals are deliverables, not extras.

**Organization**: Grouped by user story. Execution order differs from priority order in one place:
**US5 (recording, P3) runs before US3/US4**, because the Hitomi work needs recorded fixtures.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks)
- **[Story]**: US1–US5 from spec.md
- All paths are relative to the repository root

## Path Conventions

- Contract package: `contract/`
- Runner: `tools/runner/src/`, unit tests in `tools/runner/test/`
- Hitomi source: `sources/hitomi/` (one script file: every Hitomi task edits
  `sources/hitomi/hitomi.js` sequentially, so Hitomi tasks are never [P] with each other)

---

## Phase 1: Setup

**Purpose**: Workspaces, dependencies, ignore rules.

- [X] T001 Create root `package.json`: `"private": true`, `"type": "module"`, `"engines": {"node": ">=22.18"}`, `"workspaces": ["contract", "tools/runner"]`, scripts `"runner": "node tools/runner/src/cli.ts"`, `"schema": "npm run schema -w contract"`, `"test": "node --test tools/runner/test/ && npm run runner -- contract-test && npm run runner -- test sources/hitomi"`
- [X] T002 [P] Create `contract/package.json`: name `@hentai-reader/contract`, `"version": "1.0.0"` (this IS the contract version, FR-002), devDependencies `typescript`, `ts-json-schema-generator`, script `"schema": "ts-json-schema-generator --path host-api.d.ts --type '*' --out schema/contract.schema.json --no-type-check"`
- [X] T003 [P] Create `tools/runner/package.json` (name `@hentai-reader/runner`, `"type": "module"`, `"bin": {"source-runner": "src/cli.ts"}`, dependencies `quickjs-emscripten`, `cheerio`, `ajv`) and `tools/runner/tsconfig.json` (`noEmit`, `strict`, `module`/`moduleResolution` `nodenext`, `allowImportingTsExtensions`, `erasableSyntaxOnly` — Node type stripping, no enums/namespaces)
- [X] T004 [P] Append `contract/schema/` to `.gitignore`; create `sources/hitomi/CHANGELOG.md` in the same format as `contract/CHANGELOG.md` (tag pattern `sources-hitomi-vX.Y.Z`, `## [Unreleased]`)
- [X] T005 Run `npm install` at the root to produce `package-lock.json`; confirm `node --version` is ≥ v22.18 and `npm run runner -- --help` reaches `tools/runner/src/cli.ts` (stub that prints usage)

---

## Phase 2: Foundational (blocks every user story)

**Purpose**: The contract file, the engine, the reference host, the offline transport, and the
live transport (needed to explore Hitomi).

- [X] T006 Move `specs/001-source-contract/contracts/host-api.d.ts` to `contract/host-api.d.ts`; in the moved file replace the "DESIGN DRAFT…" paragraph with "Single source of truth (Constitution II). Version = contract/package.json"; in the spec folder leave a one-line `contracts/host-api.d.ts` pointer comment: `// Moved to /contract/host-api.d.ts — authoritative from now on.`
- [X] T007 [P] Create `contract/prelude.js` (plain script, no exports): `globalThis.SourceError = class SourceError extends Error { constructor(kind, message, options) {…} }` with `name = "SourceError"`, `kind` validated against the six kinds in `host-api.d.ts` (invalid kind → `TypeError`), optional `retryAfterSeconds` and `details`
- [X] T008 [P] Create `contract/tsconfig.scripts.json` for `@ts-check` source scripts: `allowJs`, `checkJs`, `noEmit`, `strict`, `target`/`lib` `["es2020"]` only (no DOM — so `fetch`, `URL`, `setTimeout` are type errors), `types: []`, `module: "es2020"`, `paths: {"@contract": ["./host-api.d.ts"]}` so scripts write `/** @type {import('@contract').SourceManifest} */`
- [X] T009 Run `npm run schema` and verify `contract/schema/contract.schema.json` contains definitions for `SourceManifest`, `SearchQuery`, `SearchPage`, `GallerySummary`, `Gallery`, `Page`, `PageImage`, `RetryHint`, `TagSuggestion`; adjust JSDoc in `contract/host-api.d.ts` if the generator rejects a construct (keep types semantically identical)
- [X] T010 Implement `tools/runner/src/schema.ts`: load `contract/schema/contract.schema.json` (missing → error "run `npm run schema`"), compile with `ajv` (allErrors), export `validate(typeName, value): string[]` returning violations formatted `<field path>: <message>` (FR-019)
- [X] T011 Implement `tools/runner/src/engine.ts` with `quickjs-emscripten`: `createEngine({ prelude, source, host, frozenClock?, limits })` → new runtime + context, `setMemoryLimit(128 MiB)`, interrupt handler with a 60 s deadline per call, evaluate `contract/prelude.js` as global code then the source with `{ type: "module" }` and keep the exports handle; `call(fn, args)` passes args/results as JSON strings (no handle leaks), resolves the returned promise with `resolvePromise` while pumping `runtime.executePendingJobs()` after every host promise settles; error classification: `SourceError` → `{kind, message, retryAfterSeconds, details}`, `ReferenceError: X is not defined` → `ScriptError` "forbidden capability: X" (FR-022), deadline/memory → `ScriptError` with `details.limit`, anything else → `ScriptError`; `frozenClock` replaces `Date.now` and argument-less `new Date()` before the source loads (R7)
- [X] T012 Implement `tools/runner/src/host/index.ts`: `installHost(context, { manifest, transport, storage, politeness: "enforce" | "skip", log })` defines globals `http`, `html`, `json` (`parse` → native `JSON.parse`), `storage`, `sleep` (≤ 60 000 ms, skipped instantly when politeness is `"skip"`), `console.log/warn/error` (to `log`); shared helper turning a native `Promise` into a QuickJS deferred promise and pumping jobs on settle
- [X] T013 [P] Implement `tools/runner/src/host/http.ts`: validate request (method GET/POST, absolute http(s) URL, body only for POST); host matching against `manifest.domains` (exact or `*.` suffix, case-insensitive) else reject `SourceError("InvalidInput")`; scheduler enforcing `minIntervalMs` between request starts and `maxConcurrency` in flight (bypassed when politeness is `"skip"`); per-source cookie jar (store `set-cookie` by domain/path/expiry, attach only to matching hosts, merge script `cookie` header); follow redirects (max 5, re-checking domains); response `{status, url, headers (lower-case, set-cookie removed, repeats joined ", "), body}` with `body` as `Uint8Array` for `responseType: "bytes"` (QuickJS `newArrayBuffer`) else charset-decoded text (default UTF-8); timeouts and transport errors → `SourceError("Network")`; append every request to the request log `{method, url, headers, startedAt, endedAt, status | refused}`; define `Transport` interface `send(rawRequest) → Promise<RawResponse>`
- [X] T014 [P] Implement `tools/runner/src/host/html.ts`: `html.select(target, selector)` — string target parsed with `cheerio.load` (full document; cache the last 8 parsed strings), element target resolved from a handle table; reject non-standard pseudo-classes (`:contains`, `:icontains`, `:eq`, `:gt`, `:lt`, `:first`, `:last`, `:even`, `:odd`, `:header`, `:parent`, `:selected`, `:checkbox`, etc.) with `SourceError("InvalidInput")` before calling css-select; returned elements are QuickJS objects with `tag` and methods `text()` (whitespace-collapsed, trimmed, Jsoup-like), `ownText()`, `html()`, `outerHtml()`, `attr(name)` (null if absent); handles released when the call completes
- [X] T015 [P] Implement `tools/runner/src/host/storage.ts`: in-memory store per source id with `get`/`set` (null deletes), limits key ≤ 128 chars, value ≤ 1 MiB, total ≤ 8 MiB → `SourceError("InvalidInput")`; `seed(object)`
- [X] T016 [P] Implement `tools/runner/src/transport/offline.ts`: load `<dir>/fixtures/index.json` (format in `contracts/fixtures-and-tests.md`), match on method + URL + `range` header + SHA-256 of body, return status/headers/body bytes from `fixtures/bodies/`; support `"echo": true` responses (200, JSON describing the sent request incl. headers/cookies); unmatched → `UnmatchedRequestError` naming method, URL and range (exit code 4); expose `recordedAt` for the frozen clock
- [X] T017 [P] Implement `tools/runner/src/transport/live.ts`: Node `fetch` with `redirect: "manual"` (host follows), `AbortSignal.timeout`, raw body as bytes, all response headers including `getSetCookie()` values
- [X] T018 Implement `tools/runner/src/cli.ts`: argument parsing (`node:util` `parseArgs`), commands `run`, `test`, `check`, `contract-test` dispatched to `tools/runner/src/commands/*.ts` (stubs for now), flags `--offline` (default) / `--live` / `--record`, `--storage`, `--cookies`, `--json`, `--verbose`, `--case`, `--update`; source loading: `<sourceDir>/<basename(sourceDir)>.js`; exit codes 0–4 exactly as in `contracts/runner-cli.md`
- [X] T019 [P] Unit tests in `tools/runner/test/http.test.ts`: domain matching (exact, wildcard, refusal), scheduler gap ≥ `minIntervalMs` and in-flight ≤ `maxConcurrency` with a fake transport, cookie scoping and header merge, redirect limit, set-cookie hidden from scripts
- [X] T020 [P] Unit tests in `tools/runner/test/html.test.ts`: document vs element targets, nested selection inside `<table>` rows, `:has()`, attribute selectors, every rejected pseudo-class, `text()` normalisation
- [X] T021 [P] Unit tests in `tools/runner/test/engine.test.ts`: module exports read, three concurrent host promises resolve in any order, frozen clock visible via `Date.now()` and `new Date()`, `fetch(...)` → "forbidden capability: fetch", 60 s deadline (use a 100 ms test limit), `SourceError` classification

**Checkpoint**: an ES-module source can be loaded and called with host capabilities, offline.

---

## Phase 3: User Story 1 — One authoritative contract (P1) 🎯 MVP part 1

**Goal**: Non-conforming scripts are rejected with named violations; hosts refuse incompatible
contract versions; a portable host contract suite exists.

**Independent Test**: `check` on each `contract/conformance/broken-*` fails naming its one
violation; `check` on `contract/conformance/minimal` passes; `contract-test` reports per-test results.

- [X] T022 [P] [US1] Create `contract/conformance/minimal/minimal.js` (`// @ts-check`, JSDoc types via `@contract`): manifest (`id: "minimal"`, domains `["example.test"]`, politeness 0/1, one `sort` filter), `search` parsing one fixture HTML list with `html.select`, `getGallery`, `getPageUrl`; plus `contract/conformance/minimal/fixtures/` (index + 2 HTML bodies) — also the template for new sources (SC-008)
- [X] T023 [P] [US1] Create `contract/conformance/broken-missing-function/broken-missing-function.js` — copy of minimal without `getPageUrl`
- [X] T024 [P] [US1] Create `contract/conformance/broken-wrong-shape/broken-wrong-shape.js` — `getGallery` returns a page without `height`; add `tests/gallery.case.json` so the runtime check also sees it
- [X] T025 [P] [US1] Create `contract/conformance/broken-forbidden-capability/broken-forbidden-capability.js` — `search` calls `fetch(...)`
- [X] T026 [P] [US1] Create `contract/conformance/broken-contract-version/broken-contract-version.js` — `manifest.contract: "2.0"`
- [X] T027 [US1] Implement `tools/runner/src/compat.ts`: `isCompatible(hostVersion "1.0.0", required "M.m")` ⇔ same major and `m ≤ host minor`; used by `engine.ts` load so every command refuses incompatible sources with "source requires contract M.m, host supports 1.0" (FR-002)
- [X] T028 [US1] Implement `tools/runner/src/commands/check.ts`: (1) static — TypeScript compiler API over the script + `contract/host-api.d.ts` with `contract/tsconfig.scripts.json` options, each diagnostic printed `<file>:<line>: <message>`; (2) runtime — load (compat check), validate `manifest` against `SourceManifest` schema, required exports present, `suggestTags` present ⇔ `capabilities.suggestTags`; (3) if `tests/` exists run every case offline and validate results against the schema; print all violations as `<function or manifest>.<field path>: <problem>`; exit 1 if any
- [X] T029 [P] [US1] Contract tests for HTTP in `contract/tests/http-text-binary/`, `contract/tests/http-post/`, `contract/tests/http-range/`, `contract/tests/http-status-redirect/`, `contract/tests/http-domain-refused/` (each: `test.js` with `manifest` + `run()`, `fixtures/`, `expect.json`) — text vs `Uint8Array` bodies, POST with string and byte bodies (echo), Range header passed and 206 returned, 404/500 returned without throwing, 302 followed, undeclared host refused with `InvalidInput`
- [X] T030 [P] [US1] Contract tests for cookies and politeness in `contract/tests/cookies-scoping/`, `contract/tests/politeness-gap/`, `contract/tests/politeness-concurrency/` — seeded cookie sent only to its domain (echo), script cookie merged; `expect.json` `minGapMs`, `maxInFlight`
- [X] T031 [P] [US1] Contract tests for the remaining capabilities in `contract/tests/storage/` (get/set/delete, limits; isolation: `expect.json` names a second module `other.js` run first under another source id), `contract/tests/html-select/` (string and element targets, each supported selector form, forbidden pseudo-class rejected), `contract/tests/json-parse/`, `contract/tests/sleep/`, `contract/tests/source-error/` (construction, `instanceof`, invalid kind → `TypeError`)
- [X] T032 [US1] Implement `tools/runner/src/commands/contract-test.ts`: for each `contract/tests/<name>/` build a fresh engine + host with politeness `"enforce"`, offline transport (echo supported), cookie seeds from `expect.json`, run optional `other.js` then `test.js` `run()`; then assert `expect.json` against the request log (`minGapMs`, `maxInFlight`, `refusedUrls`, `cookies.sentTo/notSentTo`); print pass/fail per test; exit 1 on any failure
- [X] T033 [US1] Run `npm run runner -- check` on `minimal` and all four `broken-*` and `npm run runner -- contract-test`; fix until results match quickstart §1 (SC-003); add `## [1.0.0] - Unreleased` notes to `contract/CHANGELOG.md`

**Checkpoint**: contract is enforceable; US1 independently demonstrable.

---

## Phase 4: User Story 2 — Run a source offline (P1) 🎯 MVP part 2

**Goal**: Any source function runs from the CLI over fixtures; test suites with snapshots gate
publication; the runner host passes the contract suite.

**Independent Test**: `run` and `test` on `contract/conformance/minimal` pass offline; a call with
no recording exits 4 naming the request; `contract-test` passes 100%.

- [X] T034 [US2] Implement `tools/runner/src/commands/run.ts`: parse JSON args (`@file.json` reads a file), call the function, validate the result (`search` → `SearchPage`, `getGallery` → `Gallery`, `getPageUrl` → `PageImage`, `suggestTags` → `TagSuggestion[]`), check `sourceData` survives a JSON round-trip, print pretty JSON (or `--json`), `--verbose` prints the request log and console output; `SourceError`/`ScriptError` → exit 3 with kind and message; unmatched request → exit 4
- [X] T035 [P] [US2] Implement `tools/runner/src/cases.ts`: load `tests/*.case.json`, validate shape (`description`, `call.fn`, `call.args`, optional `storage`, `expectError`), resolve `"@<case>"` arguments from other cases' snapshots in dependency order with cycle detection
- [X] T036 [P] [US2] Implement `tools/runner/src/diff.ts`: structural JSON diff listing changed/added/removed paths with old → new values, truncated after 50 lines
- [X] T037 [US2] Implement `tools/runner/src/commands/test.ts`: fresh engine per case, offline transport, frozen clock = fixtures `recordedAt`, storage seed; compare with `<name>.expected.json` (missing snapshot = failure unless `--update`), `expectError` passes iff a `SourceError` of that kind is thrown; schema-validate every result; `--case`, `--update`; summary line `N passed, M failed`; exit 1 on any failure (FR-025)
- [X] T038 [P] [US2] Add test cases for the minimal source in `contract/conformance/minimal/tests/` (`search-first-page`, `search-last-page`, `gallery`, `page-url` using `"@gallery"`) and generate snapshots with `--update`
- [X] T039 [P] [US2] Unit tests in `tools/runner/test/offline.test.ts`: unmatched request → exit 4 with method/URL/range in the message; deterministic output across 3 runs; `broken-forbidden-capability` via `run` names `fetch` (US2-3)
- [X] T040 [US2] Run `npm run runner -- contract-test` and `npm run runner -- test contract/conformance/minimal`; fix the reference host until 100% pass (SC-004)

**Checkpoint**: MVP (US1 + US2) — the contract and the runner work end-to-end on a sample source.

---

## Phase 5: User Story 5 — Record fixtures from the live site (P3, executed before US3/US4)

**Goal**: Live runs and sanitised recording, so Hitomi fixtures can be created and refreshed.

**Independent Test**: record one minimal live call (e.g. Hitomi front feed after T047), replay
offline with identical output; no cookies, credentials, or image bodies in `fixtures/`.

- [X] T041 [US5] Implement `tools/runner/src/transport/recorder.ts`: wraps `live.ts`; per response: drop request `cookie`/`authorization` from stored keys, keep only allowlisted response headers (`content-type`, `content-range`, `content-length`, `location`, `retry-after`, `last-modified`, `etag`), refuse image bodies (`content-type: image/*` or magic bytes JPEG `FFD8FF`, PNG `89504E47`, GIF `GIF8`, WebP `RIFF….WEBP`, AVIF `ftypavif`) with a loud error; write body to `fixtures/bodies/<first 16 hex of sha256>.<html|json|js|bin|txt>`; upsert entry in `fixtures/index.json` (entries sorted by URL then range, 2-space JSON) and set `recordedAt`
- [X] T042 [US5] Wire `--live` and `--record` into `tools/runner/src/commands/run.ts` and `tools/runner/src/commands/test.ts` (politeness `"enforce"`, real clock, `--cookies` loads a Netscape cookie file into the jar); `test --record` records the selected cases, then replays them offline and compares to the live result
- [X] T043 [P] [US5] Unit tests in `tools/runner/test/recorder.test.ts` with a fake live transport: set-cookie/cookie never written, image body refused, identical bodies deduplicated, record → offline replay gives identical output
- [X] T044 [US5] Add fixture audit to `tools/runner/src/commands/check.ts`: `fixtures/` total < 5 MB, no body file with image magic bytes, no `cookie`, `set-cookie`, or `authorization` key anywhere in `fixtures/index.json` (SC-007)

**Checkpoint**: fixtures can be produced safely from the live site.

---

## Phase 6: User Story 3 — Browse and search Hitomi (P2)

**Goal**: Front feed, popular, tag search with exclusions, free text, language/type filters,
cursor paging, tag suggestions — all offline-tested.

**Independent Test**: `npm run runner -- test sources/hitomi` passes all `search-*` and
`suggest-*` cases offline.

- [X] T045 [US3] Explore the live site with `npm run runner -- run … --live --verbose` and the browser devtools; confirm current hostnames and URL schemes for `.nozomi` lists (latest, per tag/area, per language, popular per period), `galleries/<id>.js`, `galleriesindex` and tag index (version file, node and data files), `gg.js`, thumbnails and images; update section R10 of `specs/001-source-contract/research.md` with the confirmed facts
- [X] T046 [US3] Create `sources/hitomi/hitomi.js` (`// @ts-check`, JSDoc types via `@contract`): `manifest` (`id: "hitomi"`, `name: "Hitomi.la"`, `version: "0.1.0"`, `contract: "1.0"`, `domains` from T045, `politeness: {minIntervalMs: 100, maxConcurrency: 4}`, `login: "none"`, filters `sort` [`latest`, `popular_day`, `popular_week`, `popular_month`, `popular_year`] default `latest`, `language` (ISO codes for Hitomi's languages), `type` [`doujinshi`, `manga`, `artist_cg`, `game_cg`, `image_set`, `anime`], `capabilities.suggestTags: true`); mapping tables Hitomi language name ↔ ISO 639-1 and Hitomi type ↔ `GalleryType`; stub functions throwing `SourceError("SiteChanged", "not implemented")`
- [X] T047 [US3] Add helpers to `sources/hitomi/hitomi.js`: UTF-8 encoder, pure-JS SHA-256, big-endian uint32 reader over `Uint8Array`, `http.request` wrapper that maps 429/503-with-limit-page to `SourceError("RateLimited", …, {retryAfterSeconds})` from `retry-after` and unexpected statuses to `SiteChanged`
- [X] T048 [US3] Add the `gg.js` loader to `sources/hitomi/hitomi.js`: fetch `gg.js`, store `{text, fetchedAt}` in `storage` key `gg`, reuse if younger than 10 min (`Date.now()`), evaluate with `new Function("'use strict'; var gg; " + text + "; return gg;")`, `ensureGg({force})`; re-implement the `common.js` helpers (subdomain selection from hash, full path, thumbnail path) needed for cover and image URLs; failure to obtain `gg` members → `SiteChanged`
- [X] T049 [US3] Add the query parser to `sources/hitomi/hitomi.js`: split text into terms; `ns:value` tags (namespaces `female`, `male`, `tag`, `artist`, `group`, `series`/`parody`, `character`, `type`, `language`), `-` prefix = exclusion, underscores ↔ spaces, lower-case; remaining words = free-text terms; merge `filters.language` / `filters.type` / `filters.sort`; validate filter ids and values against the manifest → `SourceError("InvalidInput")`
- [X] T050 [US3] Add `.nozomi` access to `sources/hitomi/hitomi.js`: URL builder (area, tag, language suffix `-all`/`-<lang>`, popular period), single-page Range read (`bytes=o*4-(o+25)*4-1`, `responseType: "bytes"`, total from `content-range`), whole-file read for multi-term queries
- [X] T051 [US3] Add the B-tree index reader to `sources/hitomi/hitomi.js` (shared by free-text search and tag suggestions): index version fetch, node decoding (keys, data pointers, sub-node addresses), key = first 4 bytes of SHA-256 of the UTF-8 term, Range reads of nodes and of the gallery-id data block
- [X] T052 [US3] Implement `search` in `sources/hitomi/hitomi.js`: empty query → latest (or popular per `sort`) feed for the language; single positive term with no free text → paged Range reads; otherwise intersect positive sets, subtract negatives, order per `sort`, keep the id list in a module-level `Map` keyed by the normalised query; cursor `"o:<offset>"` (invalid → `InvalidInput`); fetch `galleries/<id>.js` for the page's ids concurrently (`Promise.all`; host enforces politeness), map to `GallerySummary` (title, cover thumbnail `ImageRequest` with `referer: https://hitomi.la/`, ISO language, type, `pageCount = files.length`, artists, ≤ 20 tags with namespaces, `webUrl`); `nextCursor` null on the last page; `total` when known
- [X] T053 [US3] Implement `suggestTags` in `sources/hitomi/hitomi.js` via the tag index (`female:gla` → namespace + prefix), returning `{namespace, name, count}` in site order
- [X] T054 [US3] Record fixtures and write cases in `sources/hitomi/tests/`: `search-latest`, `search-latest-page2` (cursor from the first), `search-popular-week`, `search-tags-exclude-language` (`female:glasses -male:muscle` + `language: en`; pick rare tags so whole lists stay small), `search-free-text`, `search-type-filter`, `search-last-page` (tag with < 25 galleries → `nextCursor: null`), `search-invalid-cursor` (`expectError: InvalidInput`), `search-unknown-filter` (`expectError: InvalidInput`), `search-rate-limited` (hand-made 429 fixture with `retry-after` → `RateLimited`), `suggest-female-gla`; run with `--record`, then `--update`; review each snapshot against US3 acceptance scenarios 1–6 (every result has `female:glasses`, none has `male:muscle`, all `language: "en"`, etc.)

**Checkpoint**: Hitomi search works offline; US3 independently testable.

---

## Phase 7: User Story 4 — Hitomi gallery details and page images (P2)

**Goal**: Full gallery metadata with exact page sizes; image requests in WebP with required
headers; retry refreshes `gg.js`.

**Independent Test**: `npm run runner -- test sources/hitomi` passes all `gallery-*` and `page-*`
cases offline.

- [X] T055 [US4] Implement `getGallery` in `sources/hitomi/hitomi.js`: fetch `galleries/<id>.js` (404 → `NotFound`), strip `var galleryinfo = `, `json.parse`, map to `Gallery`: `title`, `titleOriginal` (`japanese_title`), all tags (female/male flags → namespaces; artists/groups/parodies/characters as namespaced tags), ISO language, type, `uploadedAt` normalised to ISO 8601 without `Date.parse` of the site format, `rating: null`, `uploader: null`, `pages[i] = {index, width, height, size: "exact", fileName, thumbnail}`, `webUrl`, `sourceData: {files: [{hash, haswebp, hasavif}]}`; zero files → `SiteChanged`
- [X] T056 [US4] Implement `getPageUrl` in `sources/hitomi/hitomi.js`: from `sourceData.files[index]` choose WebP → AVIF → original, build URL via `gg` + `common.js` helpers, return `{url, headers: {referer: "https://hitomi.la/"}, format, width, height}`; `retry` present → `ensureGg({force: true})` before building; index out of range → `InvalidInput`
- [X] T057 [US4] Record fixtures and write cases in `sources/hitomi/tests/`: `gallery-basic` (all pages `exact`, `pages.length == pageCount`), `gallery-not-found` (`expectError: NotFound`), `page-url-webp` (`"@gallery-basic"`, index 0), `page-url-cached-gg` (control: cached `gg` used without a retry hint), `page-url-retry` (storage seeded with a stale `gg`, retry hint → fresh `gg.js` fetched and URL differs), `page-url-out-of-range` (`InvalidInput`); `--record`, `--update`, review against US4 scenarios 1–4. (A WebP-fallback case is not applicable: the site always serves WebP — research R10a.)
- [X] T058 [US4] Create `tools/runner/scripts/live-dimension-check.ts` (SC-006): take 20 random ids from the live latest feed, `getGallery` each, for the first/middle/last page call `getPageUrl`, fetch the image through the runner host (politeness enforced, headers from the request), read dimensions from the WebP (VP8/VP8L/VP8X) or AVIF (`ispe` box) header in memory only (never written to disk), compare with `Page.width/height`; print a table and exit 1 on any mismatch or failed load; run it once and record the result in `sources/hitomi/CHANGELOG.md` notes

**Checkpoint**: all five stories complete.

---

## Phase 8: Polish & Cross-Cutting Concerns

- [X] T059 [P] Create `.github/workflows/ci.yml`: on push and pull_request — Node 22, `npm ci`, `npm run schema`, `node --test tools/runner/test/`, `npm run runner -- contract-test`, `npm run runner -- check` + `test` for `contract/conformance/minimal` and every `sources/*/` (fail the job on any non-zero exit) — enforces "main always green" (Constitution X)
- [X] T060 [P] Update `contract/README.md` (what the contract is, versioning rule, how to regenerate the schema, how platform hosts run `contract/tests`), `tools/README.md` (runner usage, modes, exit codes; link `specs/001-source-contract/contracts/runner-cli.md`), `sources/README.md` (adding a source = copy `contract/conformance/minimal`, record, test, check)
- [X] T061 [P] Finalise `sources/hitomi/CHANGELOG.md` (`0.1.0`: search, popular, tags, free text, suggestions, gallery, page URLs) and `contract/CHANGELOG.md` (`1.0.0`: initial contract)
- [X] T062 Run every step of `specs/001-source-contract/quickstart.md` (offline suite 10× for determinism and < 30 s, SC-001; live checks SC-005; SC-008 copy of minimal) and fix any failures
- [X] T063 Run `npm test` at the root and confirm exit code 0 before merging into `main`

UI translations (template task): **not applicable** — this feature adds no UI strings.

---

## Dependencies & Execution Order

### Phase Dependencies

```text
Setup (T001–T005)
  └─> Foundational (T006–T021)
        ├─> US1 (T022–T033) ─┐
        └─> US2 (T034–T040) ─┴─> US5 (T041–T044) ─> US3 (T045–T054) ─> US4 (T055–T058) ─> Polish (T059–T063)
```

- **US1 and US2** both depend only on Foundational and can proceed in parallel; T028 (`check`)
  step 3 reuses `cases.ts` from T035, and T033 needs T032 (contract-test command).
- **US5** depends on US2 (`run`/`test` commands to wire modes into).
- **US3** depends on US5 (fixtures must be recorded) and on T045 (confirmed site facts).
- **US4** depends on US3 (shares `gg.js` loader, helpers and the same script file).

### Within Each Story

- Hitomi tasks are strictly sequential (single file `sources/hitomi/hitomi.js`).
- Snapshots are generated with `--update` only after the implementation task they cover, and
  reviewed against the acceptance scenarios before committing.

## Parallel Opportunities

```text
# Setup
T002, T003, T004 together

# Foundational, after T006–T012
T013 http.ts | T014 html.ts | T015 storage.ts | T016 offline.ts | T017 live.ts
T019 | T020 | T021 unit tests (after their targets exist)

# US1
T022–T026 conformance samples together; T029 | T030 | T031 contract test groups together

# US2
T035 cases.ts | T036 diff.ts | T038 minimal cases | T039 offline tests

# Polish
T059 | T060 | T061
```

## Implementation Strategy

### MVP (US1 + US2)

1. Setup → Foundational → US1 → US2.
2. **Stop and validate**: quickstart §1 passes; `contract-test` 100%; minimal source tested
   offline. The contract is now usable by anyone writing a source.

### Incremental delivery

3. US5 → record tooling validated with a fake transport and one live call.
4. US3 → Hitomi search offline-tested (commit per case group).
5. US4 → gallery/pages + live dimension check (SC-006).
6. Polish → CI, READMEs, CHANGELOGs, full quickstart; merge `001-source-contract` into `main`
   and tag `contract-v1.0.0` and `sources-hitomi-v0.1.0`.

## Notes

- Commits: Conventional Commits with component scope — `feat(contract): …`,
  `feat(tools/runner): …`, `feat(sources/hitomi): …`, `test(sources/hitomi): …`.
- Never commit cookie files (`cookies*.txt` is gitignored), images, or `contract/schema/`.
- If Hitomi facts in T045 contradict research R10 (hostnames, file formats), update R10 first,
  then implement against the confirmed facts.
