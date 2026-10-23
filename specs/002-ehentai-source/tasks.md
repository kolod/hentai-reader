---

description: "Task list for feature 002-ehentai-source"
---

# Tasks: E-Hentai / ExHentai Source

**Input**: Design documents from `/specs/002-ehentai-source/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md),
[data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: MANDATORY (Constitution VII): contract tests for 1.1, runner unit tests, and the
E-Hentai offline suite are deliverables.

**Organization**: Grouped by user story. Contract 1.1 + runner support is Foundational (blocks
every story). US4/US5 need a real account: **T030 asks the user for `cookies-eh.txt`**.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: parallelisable (different files, no unfinished dependencies)
- **[Story]**: US1–US5 from spec.md
- Paths relative to the repository root. Every E-Hentai task edits `sources/ehentai/ehentai.js`
  or `sources/ehentai/redact.mjs` → those tasks are sequential within their file.

## Recording rules (apply to every "record" task)

- Record with `npm run runner -- test sources/ehentai --case <name> --record --update` (add
  `--cookies cookies-eh.txt` for logged-in cases); then `npm run runner -- check sources/ehentai`
  must report no violations (redaction idempotence, audit).
- Review each snapshot against the acceptance scenario it covers before committing.
- Hand-made fixtures (limits, bans, rejected cookies): edit `sources/ehentai/fixtures/index.json`
  after recording, as done for Hitomi's `search-rate-limited`; mention "hand-made" in the case
  description.

---

## Phase 1: Setup

- [X] T001 Create `sources/ehentai/` with `CHANGELOG.md` (Keep a Changelog, tags `sources-ehentai-vX.Y.Z`, `## [Unreleased]`), an empty `tests/` and `fixtures/` (`.gitkeep` not needed once fixtures exist)
- [X] T002 [P] Confirm `.gitignore` ignores `cookies*.txt` at any depth and add `!**/fixtures/**` exceptions only if needed; verify with `git check-ignore -v cookies-eh.txt sources/ehentai/cookies-eh.txt`

---

## Phase 2: Foundational — contract 1.1 and runner support (blocks all stories)

- [X] T003 Edit `contract/host-api.d.ts` per `specs/002-ehentai-source/contracts/contract-1.1.md`: add `SettingDefinition`, `SettingValues`, `AccountStatus`; `SourceManifest.settings?`; `capabilities.accountStatus?`; `ImageRequest.crop?`; `GalleryType` `"asian_porn"`; document the 1.1 function signatures (trailing `settings`, `retry: RetryHint | null`, optional `getAccountStatus`) and the calling-convention rule for 1.0 vs 1.1 sources in `SourceModule` comments; add the normative image-fetching note (cookie jar + politeness limiter) to the header comment
- [X] T004 Set `contract/package.json` version `1.1.0`; run `npm run schema`; verify `contract/schema/contract.schema.json` defines `SettingDefinition`, `SettingValues`, `AccountStatus` and `ImageRequest.crop`
- [X] T005 In `tools/runner/src/source.ts` add settings support: `resolveSettings(manifest, overrides)` (defaults ∪ overrides; unknown id/value → `UsageError`) and `callSource(open, fn, args, settings)` applying the calling convention — contract `"1.0"`: call with `args` as given (trailing `undefined` retry dropped); `"1.1"`: pad `getPageUrl` retry with `null`, append the resolved settings object; `getAccountStatus` allowed only when `capabilities.accountStatus`
- [X] T006 In `tools/runner/src/schema.ts` add result type `getAccountStatus → "AccountStatus"`; in `tools/runner/src/cli.ts` add repeatable `--setting id=value` (parseArgs `multiple: true`) to `CliOptions`; in `tools/runner/src/commands/run.ts` call through `callSource` with resolved settings
- [X] T007 In `tools/runner/src/cases.ts` accept optional case field `"settings"` (object), allow `fn: "getAccountStatus"`, and call through `callSource`; update `specs/001-source-contract/contracts/fixtures-and-tests.md` case format with the `settings` field
- [X] T008 In `tools/runner/src/commands/check.ts` add rules: `capabilities.accountStatus === true` ⇔ `getAccountStatus` exported; every setting id unique and `default ∈ values` (violations `manifest.settings.<i>: …`)
- [X] T009 [P] Add contract test `contract/tests/settings-passing/` (`test.js` with a 1.1 manifest declaring both settings, `run(settings)` asserting it received exactly the merged values; `expect.json` `{"settings": {"site": "exhentai"}}`) and extend `tools/runner/src/commands/contract-test.ts` to pass `expect.settings` merged over defaults to `run()` for 1.1 tests
- [X] T010 [P] Add conformance sample `contract/conformance/settings/settings.js` (contract `"1.1"`, both settings, `capabilities.accountStatus: true`, `getAccountStatus` returning a fixed status, one thumbnail with `crop`) with `tests/` cases using `"settings"` and `fixtures/`; snapshots via `--update`
- [X] T011 [P] Unit tests `tools/runner/test/settings.test.ts`: a 1.0 inline source sees `arguments.length` unchanged and `retry === undefined`; a 1.1 source receives `null` retry and the merged settings as last argument; unknown setting id/value rejected; `getAccountStatus` refused when not declared
- [X] T012 Update `tools/runner/scripts/live-dimension-check.ts`: call through `callSource` (settings from `--setting`), compare `estimated` pages by aspect ratio (≤ 2 % difference), and when `getPageUrl` returns `width/height` compare those exactly with the image header; keep Hitomi behaviour (±1 px for its estimated pages still passes the 2 % rule)
- [X] T013 Run `npm test` and `npm run runner -- contract-test`: Hitomi 18/18 unchanged (SC-006), minimal 1.0 sample unchanged, new tests pass; add `## [1.1.0] - Unreleased` to `contract/CHANGELOG.md` (settings, account status, crop, `asian_porn`, image-fetch rule)

**Checkpoint**: contract 1.1 usable; the runner passes settings and gates account status.

---

## Phase 3: User Story 1 — Browse and search anonymously (P1) 🎯 MVP

**Goal**: Front page, search with filters, toplists, cursors, tag suggestions — offline-tested.

**Independent Test**: `npm run runner -- test sources/ehentai` passes all `search-*` and `suggest-*` cases.

- [X] T014 [US1] Create `sources/ehentai/ehentai.js` (`// @ts-check`, JSDoc via `@contract`): manifest per data-model.md (id `ehentai`, contract `"1.1"`, login `optional`, domains, politeness 500/2, filters `sort` [latest, popular_day, popular_month, popular_year] / `type` (10 values) / `min_rating` 2–5 / `pages` 1–2000 / `language` (ISO codes of the site's languages), settings `image_quality` (default `resampled`) and `site` (default `e-hentai`), capabilities `{suggestTags: true, accountStatus: true}`); helpers: `base(site)` → `https://e-hentai.org` | `https://exhentai.org`, gallery-id format/parse `e|x/<gid>/<token>`, cursor parse/format `n:<gid>` / `p:<n>`, category ↔ type and category bit tables, language name ↔ ISO table, `request()` wrapper raising `RateLimited` on 429/509 and on the temporary-IP-ban text (parse "N minutes and M seconds" → `retryAfterSeconds`), stub exports throwing `SiteChanged`
- [X] T015 [US1] Add the `gdata` client to `sources/ehentai/ehentai.js`: `POST https://api.e-hentai.org/api.php` `{"method":"gdata","gidlist":[…≤25],"namespace":1}`, parse body text as JSON, map entries to `GallerySummary` (title, cover `ImageRequest` from `thumb`, ISO language from `language:` tags excluding translated/rewrite, type via category, `pageCount`, artists from `artist:` tags, ≤ 20 tags with namespaces, `webUrl` `<base>/g/<gid>/<token>/`); error entries → skipped in search, `NotFound` in `getGallery`
- [X] T016 [US1] Implement `search` in `sources/ehentai/ehentai.js`: listing URL (`f_search` = text + ` language:<name>$` when filtered, `f_cats` = 1023 − Σ included bits, `f_srdd`, `f_spf`/`f_spt`, `next`), collect `/g/<gid>/<token>/` links in document order (de-duplicated), `a#unext` → `n:<gid>` or null, then one `gdata` call; popular sorts → `toplist.php?tl=15|13|12&p=<n>` with cursor `p:<n>` and `InvalidInput` when combined with text or other filters; no links and no "no hits" marker → `SiteChanged`, explicit "No hits found" → empty page
- [X] T017 [US1] Implement `suggestTags` in `sources/ehentai/ehentai.js` via `tagsuggest` (master tag for slave entries, de-duplicated, `count: null`, namespace `null` when absent)
- [X] T018 [US1] Create `sources/ehentai/redact.mjs` (idempotent): listing pages → minimal HTML containing only the gallery links (as `<a href>` in order), `a#unext`/`a#dnext` and a "No hits found" marker if present; `gdata` responses → `title` `Gallery <gid>`, `title_jpn` `""`, `uploader` `uploader`, tags outside the allowlist (`female:glasses`, `female:sunglasses`, `language:english`, `language:japanese`, and any tag a case queries) → `misc:redacted N`; `tagsuggest` → names outside the allowlist → `redacted N`
- [X] T019 [US1] Record cases in `sources/ehentai/tests/`: `search-front`, `search-front-page2` (cursor from the first), `search-filters` (`female:glasses`, type doujinshi+manga, min_rating 4), `search-pages-language` (pages 20–40, language en), `search-last-page` (a query with < 25 results), `search-no-hits`, `search-popular-day`, `search-popular-day-page2`, `search-popular-with-text` (`InvalidInput`), `search-bad-cursor` (`InvalidInput`), `search-unknown-filter` (`InvalidInput`), `suggest-female-gla`; hand-made `search-http-429` and `search-ip-ban` (ban page text with "expires in 59 minutes and 12 seconds" → `RateLimited`, `retryAfterSeconds` 3552); verify snapshots against US1 scenarios 1–6

**Checkpoint**: anonymous browsing and search work offline.

---

## Phase 4: User Story 2 — Gallery details with estimated page sizes (P1)

**Goal**: Full metadata, every page with an estimated size and a cropped thumbnail.

**Independent Test**: all `gallery-*` cases pass offline.

- [X] T020 [US2] Implement `getGallery` in `sources/ehentai/ehentai.js`: parse id (`InvalidInput` if malformed), `gdata` for metadata (titles, all tags, category, language, rating number, uploader, `uploadedAt` from `posted` as ISO UTC, `pageCount`), then gallery pages `<base>/g/<gid>/<token>/?p=<n>` until all `filecount` pages are collected (page count from the `.ptt` pager, never assumed); per thumbnail cell: page number from `-<page>` in the `/s/` link, viewer token, `width/height` from the cell style (`size: "estimated"`, never 0 — fall back to the gallery's median aspect), thumbnail `ImageRequest` = sprite URL + `crop` from the background offset; content-warning page → repeat with `?nw=always`; "not available"/removed → `NotFound`; `sourceData: {pages: [{token, page}]}`; result pages ordered and complete or `SiteChanged`
- [X] T021 [US2] Extend `sources/ehentai/redact.mjs` for gallery pages: keep only `#gdt` thumbnail anchors (href, inner div style, `title="Page N: 00N.ext"`), the `.ptt` pager links and the content-warning marker; drop everything else (titles, tags, comments, uploader links)
- [X] T022 [US2] Record cases: `gallery-basic` (≤ 20 pages), `gallery-multipage` (> 40 pages, ≥ 3 thumbnail pages), `gallery-mixed-aspect` (a gallery with landscape spreads), `gallery-content-warning` (find a gallery that shows the warning), `gallery-not-found` (`NotFound`), `gallery-bad-id` (`InvalidInput`); verify US2 scenarios 1–4 (page count, all `estimated`, all thumbnails with `crop`, order)

---

## Phase 5: User Story 3 — Page images with exact sizes and retry (P1)

**Goal**: Exact sizes and image requests from viewer pages; `nl` retry; original via login; limits → `RateLimited`.

**Independent Test**: all `page-*` cases pass offline.

- [X] T023 [US3] Implement `getPageUrl` in `sources/ehentai/ehentai.js`: viewer URL `<site base of the gallery>/s/<token>/<gid>-<page>` (+ `?nl=<retry.previous.retryToken>` on retry); parse `img#img` src, exact `W x H` from the size `div`, `nl('<key>')` → `retryToken`; limit signals (`509.gif` src, "exceeded your image viewing limits" text, HTTP 509) → `RateLimited`; `settings.image_quality === "original"`: require the "Download original W x H" link (absent or login link → `LoginRequired`), return the `fullimg` URL with the original's exact size; index out of range → `InvalidInput`
- [X] T024 [US3] Extend `sources/ehentai/redact.mjs` for viewer pages: keep only `img#img` (src, style, onerror with `nl`), the size `div` (file name → `001.<ext>`), the original link and limit/ban text markers
- [X] T025 [US3] Record cases: `page-url` (`@gallery-basic`, index 0), `page-url-last`, `page-url-retry` (retry hint with the recorded `retryToken` → different server), `page-original-anonymous` (`settings: {"image_quality": "original"}` → `LoginRequired`), `page-out-of-range` (`InvalidInput`); hand-made `page-limit-509` and `page-limit-text` (`RateLimited`); verify US3 scenarios 1, 2, 4, 5
- [X] T026 [US3] Run `node tools/runner/scripts/live-dimension-check.ts sources/ehentai 10` (anonymous; SC-003) and record the result in `sources/ehentai/CHANGELOG.md` notes; on aspect deviations > 2 % investigate the cell-size parsing before continuing

**Checkpoint**: MVP — anonymous E-Hentai fully usable (US1–US3).

---

## Phase 6: User Story 4 — Logged-in use and account status (P2)

**Goal**: Account status with image-limit figures; original images when logged in; no personal data in fixtures.

**Independent Test**: all `account-*` cases and `page-original-logged-in` pass offline; fixtures contain no personal data.

- [X] T027 [US4] Implement `getAccountStatus` in `sources/ehentai/ehentai.js`: `GET https://e-hentai.org/home.php`; final URL on `bounce_login.php` (or no limit block) → `{loggedIn: false}`; otherwise parse "You are currently at X towards your limit of Y" → `imageLimit {used, max, resetsAt: null}`; when logged in, probe `https://exhentai.org/` once → `exhentai: true` if a gallery listing is returned, `false` otherwise
- [X] T028 [US4] Extend `sources/ehentai/redact.mjs`: `home.php` → keep only the limit sentence (numbers) — drop user name, member id, forum links, e-mail; verify idempotence
- [X] T029 [US4] Add a private pre-commit check to the procedure (not committed): after recording with cookies, `grep` the fixtures and snapshots for the user's own member id and user name (values taken from `cookies-eh.txt`/the user, typed in the terminal only) — must find nothing
- [X] T030 [US4] **Ask the user** for `cookies-eh.txt` (Netscape format, `ipb_member_id`, `ipb_pass_hash`, and `igneous` if ExHentai access exists), placed at the repository root (gitignored); confirm with `git check-ignore`
- [X] T031 [US4] Record cases: `account-anonymous`, `account-logged-in` (with cookies), `page-original-logged-in` (`settings: {"image_quality": "original"}`, with cookies); hand-made `account-expired-cookies` (home.php redirect to bounce_login → `loggedIn: false`); run T029's check; verify US4 scenarios 1–4

---

## Phase 7: User Story 5 — ExHentai (P2)

**Goal**: The `site` setting switches to ExHentai; galleries stay on their own site; no access → `LoginRequired`.

**Independent Test**: all `ex-*` cases pass offline.

- [X] T032 [US5] In `sources/ehentai/ehentai.js`: `search`/`suggestTags` use `base(settings.site)`; ids from ExHentai listings get prefix `x/`; `getGallery`/`getPageUrl` always use the gallery id's own site; ExHentai responses that are not a listing/gallery (empty body, access-check redirect chain ending elsewhere, "sad panda") → `LoginRequired("ExHentai access is missing")`; confirm via live runs with cookies that the `gdata` API serves ExHentai galleries (research R4 ◇) and adjust if not
- [X] T033 [US5] Record cases (with cookies): `ex-front` (`settings: {"site": "exhentai"}`), `ex-gallery`, `ex-page-url`, `ex-gallery-with-eh-setting` (an `x/` id with `site: e-hentai` still fetched from ExHentai — US5-3); without `igneous` (cookies file minus that line): `ex-no-access` (`LoginRequired`); run T029's check; verify US5 scenarios 1–3

---

## Phase 8: Polish & Cross-Cutting Concerns

- [X] T034 [P] Update `contract/README.md` (1.1 additions, calling convention), `tools/README.md` (`--setting`, recording with `--cookies`), `sources/README.md` (settings, account status, logged-in recording and the personal-data check)
- [X] T035 [P] Finalise `sources/ehentai/CHANGELOG.md` (`0.1.0`, requires contract 1.1) and `contract/CHANGELOG.md` (`1.1.0`)
- [X] T036 [P] Update `specs/product-overview.md` "Contract Implications": mark settings, account status and ExHentai placement as decided (link this spec)
- [X] T037 Extend the root `package.json` `test` script with `npm run runner -- test sources/ehentai` (CI already loops over `sources/*/`)
- [X] T038 Run `specs/002-ehentai-source/quickstart.md` end to end: offline suite 10× identical and < 30 s (SC-001), live result page ≤ 2 requests and ≤ 5 s (SC-002), fixtures audit clean and < 5 MB (SC-005), Hitomi unchanged (SC-006), contract tests 100 % (SC-007)
- [X] T039 Run `npm test` and `npx tsc -p tools/runner/tsconfig.json`; both clean before merging into `main` and tagging `contract-v1.1.0` and `sources-ehentai-v0.1.0`

UI translations (template task): **not applicable** — no UI strings. The new setting/vocabulary
ids will get app string keys in the app features.

---

## Dependencies & Execution Order

```text
Setup (T001–T002) → Foundational (T003–T013)
  → US1 (T014–T019) → US2 (T020–T022) → US3 (T023–T026)   ← MVP (anonymous)
  → [user provides cookies, T030] → US4 (T027–T031) → US5 (T032–T033)
  → Polish (T034–T039)
```

- All stories depend on Foundational (contract 1.1 + runner).
- US2 depends on US1 (`gdata` client, helpers, same script); US3 on US2 (viewer tokens from
  `getGallery`); US4 and US5 on US3 and on the cookies file (T030).
- Within each story, script tasks precede redaction tasks precede recording tasks.

## Parallel Opportunities

```text
Foundational: T009 (contract test) | T010 (conformance sample) | T011 (unit tests) after T005–T008
Polish:       T034 | T035 | T036
```

E-Hentai story tasks are sequential (shared script and redactor).

## Implementation Strategy

1. **Foundational first**: contract 1.1 and runner support, proven by Hitomi passing unchanged.
2. **MVP = US1–US3 anonymous**: browse, search, galleries, pages, limits — no account needed.
3. **Then logged-in (US4) and ExHentai (US5)** once the user provides cookies.
4. Merge `002-ehentai-source` into `main`; tags `contract-v1.1.0`, `sources-ehentai-v0.1.0`.

## Notes

- Commits: `feat(contract): …`, `feat(tools/runner): …`, `feat(sources/ehentai): …`,
  `test(sources/ehentai): …`; contract 1.1 and its runner implementation land in the same merge.
- Never commit `cookies*.txt`. Never trigger the real image limit on purpose — limit cases are
  hand-made fixtures.
