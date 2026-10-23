# Research: Source Contract, Script Runner & Hitomi Source

Phase 0 output for [plan.md](plan.md). Each item: Decision / Rationale / Alternatives.

## R1. Runner runtime and script engine

**Decision**: Runner is a Node.js 22.18+ (LTS) CLI written in TypeScript (run via Node's built-in type
stripping, no build step). Source scripts run inside **QuickJS compiled to WebAssembly**
(`quickjs-emscripten`), never in Node's own V8 context.

**Rationale**:
- Scripts execute in the same engine family as Android (QuickJS), so engine-specific gaps
  (no `URL`, no `TextEncoder`, no `setTimeout`, no `fetch`) surface in tests, not on the phone.
- A fresh QuickJS context contains only ECMAScript built-ins, so "forbidden capability" (FR-022)
  is enforced by construction: anything not injected by the host is a `ReferenceError`.
- `quickjs-emscripten` supports host functions returning promises (`context.newPromise()` +
  `runtime.executePendingJobs()`), with any number pending at once → FR-004 concurrency.
- `evalCode(code, file, { type: "module" })` returns the module's exports → scripts can be ES
  modules. Jint also supports ES modules (`Engine.Modules.Add/Import`) and promises
  (`engine.Tasks.ProcessTasks`), so the format is portable to Windows.
- TypeScript tooling (`tsc`, schema generation) is native here, and the contract is a `.d.ts`.
- Node is already anticipated by the constitution (`.gitignore` for Node).

**Alternatives considered**:
- *Kotlin/JVM CLI with a QuickJS JVM binding + Jsoup*: could later share code with the Android
  host, but drags Gradle/JVM into feature 001, desktop native-library availability for QuickJS
  bindings is uncertain, and `.d.ts` → schema tooling would still need Node.
- *Run scripts directly in Node (`vm` module)*: V8 has `URL`, `TextEncoder`, etc., hiding
  incompatibilities with QuickJS/Jint; `vm` is not a security boundary.

## R2. Script module format

**Decision**: A source is **one ES module file** (`sources/<id>/<id>.js`, ES2020 syntax) with
named exports `manifest`, `search`, `getGallery`, `getPageUrl`, and optionally `suggestTags`.
No `import` statements (single file, single SHA-256). No top-level `await`.

**Rationale**: One file = one hash in `index.json` (Constitution III). Named exports are
supported by QuickJS and Jint alike. Manifest lives inside the script, so it can never drift
from the code it describes.

**Alternatives**: separate `manifest.json` (two files to hash and keep in sync); classic script
assigning `globalThis.source` (works, but non-standard and loses static checking of exports).

## R3. Host HTML API shape

**Decision**: `html.select(target, selector)` where `target` is a markup string (parsed as a full
HTML document) or an `HtmlElement` returned earlier. Elements are host handles with methods
`text()`, `ownText()`, `html()`, `outerHtml()`, `attr(name)` and property `tag`. Supported
selector syntax: CSS Selectors Level 3 plus `:has()`; non-standard pseudo-classes (`:contains`,
`:eq`, …) are forbidden and covered by contract tests. Runner implementation: `cheerio`
(parse5-based, spec-compliant HTML parsing, like Jsoup/AngleSharp).

**Rationale**: Matches the constitution's `html.select`. Element handles allow nested selection
without re-parsing fragments (re-parsing `<tr>` outside a table silently drops it in every
spec-compliant parser). Restricting to a standard selector subset is what makes Jsoup,
AngleSharp and cheerio behave identically.

**Alternatives**: plain-data elements with eager `html` (expensive for large containers; nested
selection needs fragment re-parse); declarative extraction maps (too limited for real sites).

## R4. HTTP API shape

**Decision**: `http.request({ method, url, headers, body, responseType })` → `Promise<HttpResponse>`
with `status`, final `url`, lower-cased `headers`, and `body` as string (`"text"`, default) or
`Uint8Array` (`"bytes"`). Non-2xx statuses do **not** throw; transport failures reject with
`SourceError("Network")`. Redirects are followed (max 5). The host owns a per-source cookie jar;
a script-supplied `Cookie` header is merged with it. Requests to hosts not matched by
`manifest.domains` are rejected. Host applies `politeness` (min interval between request starts,
max concurrency) to every request.

**Rationale**: Covers Hitomi (Range requests, binary `.nozomi`), E-Hentai (POST `api.php`, login,
`nw=1` cookie) with one function, as required by Constitution 1.0.1 Principle II. Status-as-data
lets scripts detect 429/509 and raise `RateLimited` themselves.

**Alternatives**: separate `get`/`post`/`getBytes` functions (more surface, same power).

## R5. Contract as single source of truth, validated at runtime

**Decision**: `contract/host-api.d.ts` is the only hand-written description. JSON Schemas for
data shapes are **generated** from it (`ts-json-schema-generator`) into `contract/schema/`
(gitignored) and used by the runner (validated with `ajv`) for: manifest validation, every
result returned by a source in `run`/`test`, and the conformance check. Static checking: scripts
start with `// @ts-check` and JSDoc type references; `runner check` runs `tsc --noEmit` over them.

**Rationale**: FR-001 (described once), FR-019 (shapes checked, violations named by field).
Generated schemas are language-neutral, so Android/Windows contract tests can reuse them later.

**Alternatives**: zod/TypeBox schema as the source and `.d.ts` derived (contradicts Constitution
II, which names the `.d.ts` as the source); hand-written validators (second description → drift).

**Open for feature 003**: whether Android/Windows builds consume the generated schema via a
contract release asset or by running the generator in CI.

## R6. Shared prelude and typed errors

**Decision**: `contract/prelude.js` — a few lines of plain JS that every host evaluates before
the source script; it defines `SourceError` (`kind`, `message`, optional `retryAfterSeconds`,
`details`). Kinds: `RateLimited`, `LoginRequired`, `NotFound`, `SiteChanged`, `InvalidInput`,
`Network` (host-raised only). Any other exception reaching the host is classified `ScriptError`.

**Rationale**: Identical error construction on all engines without each host re-implementing an
error class; `instanceof SourceError` works inside scripts.

## R7. Execution model guarantees

**Decision** (documented in the contract):
- A host MAY keep one engine context per source alive across calls and MAY discard it at any
  time. Module-level variables may be used as caches but never relied on.
- A host MAY run several calls concurrently on the same context; scripts must be reentrant.
- Limits: 60 s wall-clock per call, 128 MiB engine memory; storage: key ≤ 128 chars, value
  ≤ 1 MiB, ≤ 8 MiB per source.
- `console.log/warn/error` exist and write only to the host's debug log.
- Dates exchanged with the host are ISO 8601 strings; scripts must not rely on `Date.parse` of
  non-ISO formats (engine behaviour differs).
- In offline mode the runner freezes `Date.now()` / `new Date()` at the fixture set's
  `recordedAt` so time-dependent logic (cache expiry) is deterministic.

## R8. Filter vocabulary and localisation

**Decision**: Filter ids and values come from a **well-known vocabulary defined in the contract**:
`sort` (`latest`, `popular_day`, `popular_week`, `popular_month`, `popular_year`, `rating`, …),
`language` (ISO 639-1 codes), `type` (`doujinshi`, `manga`, `artist_cg`, `game_cg`, `image_set`,
`anime`, `western`, `non_h`, `cosplay`, `misc`), `min_rating` (number), `pages` (range).
A source declares which ids it supports and which values. The popular feed is expressed as
`sort` values, not a separate capability.

**Rationale**: Filter labels are UI strings and must be in `strings.xml`/`.resx` in 6 languages
(Constitution IX). Script-supplied labels would bypass localisation. A new vocabulary item is a
contract MINOR bump plus app strings — rare, and acceptable versus untranslated UI.

**Alternatives**: script-provided labels per language (scripts would carry UI translations —
contradicts IX's single English key file).

## R9. ExHentai without contract variants

**Decision**: No multi-manifest/variant mechanism. The future E-Hentai script can expose both
sites as one source (domains include both; a `site` value is encoded in gallery ids) or ship as
two thin scripts. Decided in feature 002; nothing in 1.0 blocks either.

## R10. Hitomi site mechanics

Facts the Hitomi script relies on; all are confirmed against live responses when the first
fixture set is recorded (task in US5/US3), since the site changes hostnames over time.

- **Listing / tag / language queries**: `.nozomi` files are big-endian arrays of 32-bit gallery
  ids. A single-term query reads one page with an HTTP Range request
  (`bytes=offset*4-(offset+25)*4-1`); total count from `Content-Range`. Per-language variants
  exist (`…-english.nozomi`); popular orderings exist per period.
- **Multi-term queries**: positive terms' id sets are intersected, negative terms subtracted,
  then paged locally. Whole files are needed → result ids are kept in a module-level cache keyed
  by query (R7); cursor = offset.
- **Free-text search**: `galleriesindex` B-tree — key = first 4 bytes of SHA-256 of the UTF-8
  term; nodes and data blocks read via Range requests. SHA-256 and UTF-8 encoding are
  implemented in the script (neither engine provides `crypto`/`TextEncoder`).
- **Tag suggestions**: same B-tree format over the tag index → shared code with free-text search.
- **Gallery metadata**: `galleries/<id>.js` = `var galleryinfo = {…}`; strip the prefix, then
  `json.parse`. Contains titles, type, language, tags (with female/male flags), artists, groups,
  parodies, characters, date, and `files[]` with `name`, `hash`, `width`, `height`, `haswebp`,
  `hasavif` → exact page dimensions (FR-029).
- **Result cards**: one `galleries/<id>.js` request per result (page of 25), issued concurrently
  within the politeness limit.
- **Image URLs**: computed from the file hash by the site's `common.js` helpers (subdomain choice,
  path) combined with `gg.js` (rotating constants). The script re-implements the small `common.js`
  helpers and **evaluates the original `gg.js`** with `new Function` (Constitution II exception).
  `gg.js` text is cached in `storage` with a 10-minute TTL and refreshed on any retry hint.
  All image requests carry `Referer: https://hitomi.la/`. WebP preferred, then AVIF, then
  original (Constitution V).
- **Politeness defaults**: `minIntervalMs: 100`, `maxConcurrency: 4`. With ~300 ms latency a
  25-card page completes in ≈2–3 s (SC-005).
- **Fixture size control**: test cases for multi-term queries use rare tags so whole `.nozomi`
  files stay small (SC-007 < 5 MB).

### R10a. Confirmed against the live site (2026-10-23, task T045)

Verified from the site's own `common.js`, `gg.js`, `searchlib.js`, `search.js`, `results.js`,
`reader.js`, `gallery.js` and live responses. Where this contradicts R10 above, this wins.

- **Data host**: `ltn.gold-usergeneratedcontent.net` (not `ltn.hitomi.la`). Images and thumbnails:
  `<sub>.gold-usergeneratedcontent.net`. Suggestions: `tagindex.hitomi.la`. Manifest domains:
  `hitomi.la`, `*.hitomi.la`, `gold-usergeneratedcontent.net`, `*.gold-usergeneratedcontent.net`.
- **Nozomi URLs** live under `/n/` (`compressed_nozomi_prefix`):
  - feed: `/n/index-<lang>.nozomi` (`<lang>` = `all` or Hitomi language name);
  - popular feed: `/n/popular/<today|week|month|year>-<lang>.nozomi`;
  - term: `/n/<area>/<tag>-<lang>.nozomi`, popular: `/n/<area>/popular/<period>/<tag>-<lang>.nozomi`;
  - `female:x` / `male:x` use area `tag` with the namespace kept in the tag
    (`/n/tag/female:glasses-all.nozomi`); other areas: `tag`, `artist`, `group`, `series`,
    `character`, `type`. Spaces stay spaces (URL-encoded by the HTTP layer).
  - Range requests work: `206`, `content-range: bytes 0-99/<total>`; ids are big-endian int32,
    newest first.
- **Query semantics** (`results.js`): terms split on whitespace, `_` → space, `-` prefix = exclude,
  `language:x` sets the language for every nozomi lookup, terms with `:` are sorted before free
  text, the first positive term is the base set, other positives intersect, negatives subtract.
  Free-text terms use the `galleriesindex` B-tree; with `language` set and only free text, the
  script additionally intersects with the language feed (stricter than the site, which ignores
  the language there).
- **B-tree** (`search.js`): version from `/galleriesindex/version` (fetched **without** the site's
  `?_=` cache-buster so fixtures replay); nodes `galleries.<v>.index` read as 464-byte ranges;
  node = int32 key count, keys (int32 size + bytes), int32 data count, data (uint64 offset +
  int32 length), 17 uint64 sub-node addresses; key = first 4 bytes of SHA-256(UTF-8 term); data
  block `galleries.<v>.data` = int32 count + int32 ids.
- **Tag suggestions are no longer a B-tree**: `https://tagindex.hitomi.la/<ns|global>/<c>/<c>/….json`,
  one path segment per character of the term (` ` → `_`, `/` → `slash`, `.` → `dot`), returning
  `[[tag, count, namespace], …]`.
- **Gallery metadata**: `/galleries/<id>.js` = `var galleryinfo = {…}`. Fields used: `title`,
  `japanese_title`, `type`, `language`, `date` (`YYYY-MM-DD HH:MM:SS-05`), `galleryurl`, `tags[]`
  (`tag`, `female`, `male` flags), `artists`, `groups`, `parodys`, `characters` (arrays or null),
  `files[]` (`name`, `hash`, `width`, `height`, `hasavif`). **`haswebp` no longer exists: WebP is
  always available** (`reader.js` always sets a WebP `src`; AVIF is an optional `<source>`), so
  the script always returns WebP (Constitution V) and needs no fallback.
- **Image URL** (`common.js` + `gg.js`): `https://w<1+gg.m(g)>.gold-usergeneratedcontent.net/
  <gg.b><gg.s(hash)>/<hash>.webp`, `g = parseInt(hash[-1] + hash[-3..-2], 16)`. Thumbnails:
  `https://<'a'+gg.m(g)>tn.gold-usergeneratedcontent.net/<dir>/<hash[-1]>/<hash[-3..-2]>/<hash>.webp`
  with `dir` = `webpbigtn` (cover) or `webpsmalltn` (page thumbnail). Both verified to load with
  `Referer: https://hitomi.la/`. `gg.js` is a plain script assigning `gg = { m, s, b }`.
- **Served size ≠ metadata size for large originals** (found by the SC-006 live check, 3 runs,
  180 pages): the WebP is downscaled so its long side is exactly 2880 px; the short side matches
  `round(side × 2880 / longSide)` within ±1 px (the site's resampler rounds differently in some
  cases). Pages up to 2880 px are served unscaled. The script therefore marks pages above 2880 px
  `size: "estimated"` (Constitution IV allows estimates refined before display); the host's
  decoder provides the exact size inside the preload window.

## R11. Fixture and test-case storage

**Decision**: Per source, one fixture pool: `fixtures/index.json` (entries keyed by method, URL,
`range` header, and SHA-256 of the request body) plus raw response bodies in `fixtures/bodies/`,
named by content hash with a type extension (`.html`, `.json`, `.js`, `.bin`). Stored response
headers are an allowlist (`content-type`, `content-range`, `content-length`, `location`,
`retry-after`, `last-modified`, `etag`). Test cases: `tests/<name>.case.json` + snapshot
`tests/<name>.expected.json`, updated only with `--update`.

**Rationale**: Raw bodies keep diffs readable and bytes exact (`.gitattributes` marks fixtures
`-text`); content-hash names deduplicate shared responses like `gg.js`. Snapshots make every
acceptance scenario reviewable in Git. Fixtures and cases are platform-neutral data that the
Android/Windows harnesses can replay later.

## R12. Host contract test format

**Decision**: `contract/tests/<name>/` contains `test.js` (an ES module with its own `manifest`
and an exported async `run()` that throws on failure), `fixtures/` (same format as R11, plus an
`echo` response type that returns the received request as JSON), and `expect.json` with
host-side assertions over the request log (timing gaps ≥ `minIntervalMs`, max in-flight ≤
`maxConcurrency`, requests refused for undeclared domains, cookies present/absent). Runner
harness: `source-runner contract-test`.

**Rationale**: Tests are written once in JS and run inside each host's own engine, so the same
suite validates the Node runner now and Android/Windows hosts later (FR-018, Constitution VII).
