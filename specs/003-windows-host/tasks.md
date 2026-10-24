---

description: "Task list for feature 003-windows-host"
---

# Tasks: Windows App Shell and Source Host

**Input**: Design documents from `/specs/003-windows-host/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md),
[data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: MANDATORY (Constitution VII): host unit tests, the parity suite (contract tests + every
recorded source case) and the resource key-parity test are deliverables.

**Organization**: Host core is Foundational; US1 proves parity headlessly; US2–US5 build the app.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: parallelisable (different files, no unfinished dependencies)
- **[Story]**: US1–US5 from spec.md
- Paths relative to the repository root; `W` = `windows/`

---

## Phase 1: Setup

- [X] T001 Move `tools/runner/src/glue.js` to `contract/host-glue.js` (`git mv`); add `hostGlue` to `tools/runner/src/paths.ts` and load it from there in `tools/runner/src/engine.ts`; mention it in `contract/README.md` (link `specs/003-windows-host/contracts/host-bridge.md`, "not contract surface; every host changes with it"); `npm test` must stay green
- [X] T002 Create `windows/global.json` (SDK 10.0.x, rollForward latestFeature), `windows/Directory.Build.props` (`TargetFramework` defaults, `Nullable` enable, `TreatWarningsAsErrors`, `LangVersion` latest, `RepoRoot` = `$(MSBuildThisFileDirectory)..\`) and `windows/HentaiReader.slnx` listing the three projects below
- [X] T003 [P] Create `windows/src/HentaiReader.Host/HentaiReader.Host.csproj` (net10.0 class library; PackageReference Jint 4.x and AngleSharp, latest stable at implementation time, pinned); embed `$(RepoRoot)contract/prelude.js` and `$(RepoRoot)contract/host-glue.js` as `EmbeddedResource` with logical names `contract/prelude.js`, `contract/host-glue.js`
- [X] T004 [P] Create `windows/tests/HentaiReader.Host.Tests/HentaiReader.Host.Tests.csproj` (net10.0, xUnit, references Host; `RepoRoot` passed to tests via an `AssemblyMetadata` attribute)
- [X] T005 [P] Create `windows/src/HentaiReader.App/HentaiReader.App.csproj` (WinUI 3: `net10.0-windows10.0.19041.0`, `TargetPlatformMinVersion` 10.0.18362.0, `UseWinUI`, `WindowsPackageType` None, `WindowsAppSDKSelfContained` true, `SelfContained` true, `RuntimeIdentifier` win-x64, `Platforms` x64; PackageReference Microsoft.WindowsAppSDK and CommunityToolkit.Mvvm; reference Host; link every `$(RepoRoot)sources/*/*.js` except `redact.mjs` as `Content` → `Sources\<id>\<id>.js`, copy to output) with a minimal `App.xaml`/`App.xaml.cs` and `MainWindow.xaml` that open an empty window
- [X] T006 Verify `dotnet build windows/HentaiReader.slnx -p:Platform=x64` and `dotnet test windows/tests/HentaiReader.Host.Tests` succeed (no tests yet), and the app window opens with `dotnet run --project windows/src/HentaiReader.App`

---

## Phase 2: Foundational — the host library (blocks every story)

- [X] T007 Create `W/src/HentaiReader.Host/Engine/Envelope.cs` (value/error envelopes, `SourceErrorKind`) and `W/src/HentaiReader.Host/Engine/IHostBridge.cs` with the members of `specs/003-windows-host/contracts/host-bridge.md` (`Http`, `HtmlSelect`, `HtmlCall`, `StorageGet`, `StorageSet`, `Sleep`, `Log`, `FrozenNow`, `Idle`, `Fatal`)
- [X] T008 Create `W/src/HentaiReader.Host/Engine/SourceEngine.cs`: one Jint `Engine` per instance on a dedicated worker thread (a single-threaded work queue; every engine access happens there); options `LimitMemory(128 MiB)`, `LimitExecutionTime(60 s)` per call, `Constraints.RegexTimeout`, cancellation per call, CLR interop off; install `__hostBridge` as plain JS functions (`ClrFunction`) whose async members return `engine.Tasks.RegisterPromise()` promises resolved from completion callbacks (bytes via `Intrinsics.ArrayBuffer.Construct(byte[])`; incoming ArrayBuffer via `AsArrayBuffer()`); evaluate `prelude.js` and `host-glue.js` as scripts (the latter's completion value = `bind`), the source via `Modules.Add("source", code)` + `Modules.Import("source")`, then `invoker = bind(exports)`; `Task<CallResult> CallAsync(string fn, string argsJson, CancellationToken)` calls the invoker and pumps `engine.Tasks.ProcessTasks()` / `WaitForScheduledWorkAsync` until the returned promise settles, then parses the JSON envelope; classification as in `contracts/host-bridge.md` (forbidden capability, `details.limit` for time/memory); a time/memory/cancel hit marks the engine broken; `FrozenNow` set before the glue runs
- [X] T009 [P] Port `tools/runner/src/host/cookies.ts` to `W/src/HentaiReader.Host/Http/CookieJar.cs` (store from Set-Cookie, seed, Netscape import, `For(url)`, script-header merge with script values winning) with JSON (de)serialisation for persistence
- [X] T010 [P] Port the runner's `Scheduler` to `W/src/HentaiReader.Host/Http/Scheduler.cs` (min interval between request starts, max in flight, FIFO, disabled mode for offline replay)
- [X] T011 Create `W/src/HentaiReader.Host/Http/Transport.cs` (`RawRequest`, `RawResponse`, `ITransport`, `FatalTransportException`) and `W/src/HentaiReader.Host/Http/LiveTransport.cs` (`HttpClient`: `UseCookies=false`, `AllowAutoRedirect=false`, automatic decompression, per-request timeout, all response headers incl. every `set-cookie`)
- [X] T012 Create `W/src/HentaiReader.Host/Http/HttpHost.cs`: port of `tools/runner/src/host/http.ts` — request validation (method, absolute http(s) URL, body only with POST, responseType), manifest domain allow-list (exact / `*.` suffix, re-checked on every redirect, max 5, 303 and POST-301/302 → GET), cookie attach/merge/store, scheduler, response shaping (lower-case names, joined repeats, `set-cookie` removed, charset decode with UTF-8 default, bytes), request log, fatal transport errors routed to the session; plus `FetchImageAsync(ImageRequest)` that uses the same domain check, cookies, headers and scheduler for images (contract 1.1)
- [X] T013 [P] Create `W/src/HentaiReader.Host/Html/HtmlHost.cs`: AngleSharp `HtmlParser` document cache (last 8 markup strings), handle table released on `Idle`, the forbidden pseudo-class pre-check from `contracts/host-bridge.md`, invalid selector → `InvalidInput`, descendants-only `QuerySelectorAll` on elements, ops `text`/`ownText` (ASCII-whitespace collapse, NBSP kept), `html`, `outerHtml`, `attr` (null when absent), tag lower-case
- [X] T014 [P] Create `W/src/HentaiReader.Host/Storage/IStorage.cs`, `MemoryStorage.cs` (contract limits: key 1–128 chars, value ≤ 1 MiB, ≤ 8 MiB per source → `InvalidInput`; seed; snapshot) and `FileStorage.cs` (same limits, JSON file per source, atomic write via temp file + replace)
- [X] T015 [P] Create `W/src/HentaiReader.Host/Fixtures/OfflineTransport.cs`: port of `tools/runner/src/transport/offline.ts` (index.json, key = method + URL + range + body SHA-256, `echo`, `delayMs`, `recordedAt`, unmatched → `UnmatchedRequestException : FatalTransportException`)
- [X] T016 Create `W/src/HentaiReader.Host/Sources/CallArgs.cs` (port of `resolveSettings`, `takesSettings`, `callArgs` from `tools/runner/src/source.ts`), `W/src/HentaiReader.Host/Sources/SourceSession.cs` (engine + `HttpHost` + `HtmlHost` + storage + cookie jar + scheduler implementing `IHostBridge`; `DescribeAsync`, manifest checks — required fields, `contract` compatibility with host contract `1.1.0`, settings rules; `CallAsync(fn, args, settings)` through `CallArgs`; state machine `Idle`/`Busy`/`Limited`/`Broken` per `data-model.md`; `Broken` → fresh engine on next call) and `W/src/HentaiReader.Host/Sources/SourceCatalog.cs` (loads `Sources/<id>/<id>.js` from a base directory, availability with reason)
- [X] T017 [P] Unit tests `W/tests/HentaiReader.Host.Tests/Unit/CookieJarTests.cs`, `SchedulerTests.cs`, `HttpHostTests.cs` (domain allow-list, redirects incl. limit and re-check, cookie scoping/merge, set-cookie hidden, POST bytes, non-2xx returned) using a fake `ITransport` — ports of `tools/runner/test/http.test.ts`
- [X] T018 [P] Unit tests `W/tests/HentaiReader.Host.Tests/Unit/HtmlHostTests.cs` (port of `tools/runner/test/html.test.ts`) and `EngineTests.cs` (module exports, three concurrent host promises, frozen clock via `Date.now()`/`new Date()`, forbidden capability names `fetch`/`setTimeout`/`URL`/`TextEncoder`/`require`/`process`, `__hostBridge` unreachable, 100 ms time limit on a runaway loop, `SourceError` classification, `typeof System === "undefined"` (no CLR access)) and `CallArgsTests.cs` (1.0 vs 1.1 arity, settings merge/validation, account-status gating)

**Checkpoint**: a bundled source can be loaded and called headlessly with the full contract.

---

## Phase 3: User Story 1 — Same behaviour as the runner (P1) 🎯 MVP part 1

**Goal**: The parity suite passes: every contract test and every recorded case, deep-equal to the runner's snapshots.

**Independent Test**: `dotnet test windows/tests/HentaiReader.Host.Tests` → all `Contract.*`, `Case.*`, conformance tests pass.

- [X] T019 [US1] Create `W/tests/HentaiReader.Host.Tests/Parity/RepoPaths.cs` (repo root from the assembly metadata, falling back to walking up to the directory containing `contract/host-api.d.ts`)
- [X] T020 [US1] Create `W/tests/HentaiReader.Host.Tests/Parity/ContractSuite.cs`: xUnit theory over `contract/tests/*/` (test name `Contract.<dir>`), procedure of `specs/003-windows-host/contracts/parity-suite.md` §1 — politeness enforced, offline transport, cookie seeds, `other.js` sharing per-source storage by manifest id, `settings` for 1.1, `expect.json` assertions on the request log (`minGapMs` −2 ms tolerance, `maxInFlight` overlap count, `refusedUrls`, `cookies.sentTo/notSentTo`)
- [X] T021 [US1] Create `W/tests/HentaiReader.Host.Tests/Parity/CaseRunner.cs` (port of `tools/runner/src/cases.ts`: load and validate `*.case.json`, dependency order with cycle check, `@case` / `@case.path` substitution at any depth, `settings`, `fixtures` pools, `storage` seeds, frozen clock = pool `recordedAt`, politeness skipped, `expectError`) and `W/tests/HentaiReader.Host.Tests/Parity/SourceCases.cs`: theory over every `sources/*/tests/*.case.json` and `contract/conformance/*/tests/*.case.json` (name `Case.<source>.<case>`), result compared with `<case>.expected.json` by `JsonNode.DeepEquals`, failure message lists the differing JSON paths; snapshots are read-only
- [X] T022 [US1] Create `W/tests/HentaiReader.Host.Tests/Parity/ConformanceTests.cs`: `broken-forbidden-capability` `search` fails with "forbidden capability: fetch"; `broken-contract-version` is refused at load with "requires contract 2.0"
- [X] T023 [US1] Run the parity suite and fix the host until 100 % pass (SC-001); record every engine difference found and how it was resolved in `specs/003-windows-host/research.md` (new section R2a); if a source relied on engine-specific behaviour, fix the script, re-run `npm test` (runner) and bump that source's patch version and CHANGELOG
- [X] T024 [US1] Add a `windows` job to `.github/workflows/ci.yml` (`runs-on: windows-latest`, `actions/setup-dotnet` 10.0.x, `dotnet build windows/HentaiReader.slnx -c Release -p:Platform=x64`, `dotnet test windows/tests/HentaiReader.Host.Tests -c Release`); keep the existing Linux job unchanged

**Checkpoint**: the Windows host is proven equivalent to the runner on every recorded case.

---

## Phase 4: User Story 2 — Browse and search a source (P1) 🎯 MVP part 2

**Goal**: Pick a source, see its feed, search with declared filters, endless results with covers, specific error messages, `RateLimited` stops the source.

**Independent Test**: quickstart §2 steps 2–3.

- [X] T025 [P] [US2] Create `W/src/HentaiReader.App/Localization/Plural.cs` (P/Invoke to the system `icu.dll`: `uplrules_open`, `uplrules_select` (double), `uplrules_close`; returns the CLDR category for a locale and number; falls back to `other` if ICU is unavailable) and `W/src/HentaiReader.App/Localization/Strings.cs` (`Get(key)`, `Format(key, args)`, `Plural(key, n)` → `<key>_<category>` then `<key>_other`; vocabulary helpers `FilterLabel(id)`, `SortLabel(value)`, `TypeLabel(value)`, `LanguageName(iso)` via `CultureInfo.NativeName`, `ErrorTitle/ErrorBody(kind)`, `NamespaceLabel(ns)` with as-is fallback; number/date/size formatting with the current `CultureInfo`) per `specs/003-windows-host/contracts/l10n-keys.md`
- [X] T026 [P] [US2] Create `W/src/HentaiReader.App/Strings/en-US/Resources.resw` with every key needed by US2–US5: app/shell, navigation, search box and buttons, filter labels for the whole vocabulary (`Filter_*`, `Sort_*`, `Type_*`, `Setting_*`), result card (`Gallery_PageCount_one/_other`), loading/end/empty states, every `Error_<Kind>_Title/_Body` (RateLimited with retry-after, LoginRequired, NotFound, SiteChanged, InvalidInput, Network, ScriptError), retry/copy-details actions, gallery screen labels, `Namespace_*` for known namespaces, settings and language picker, adult gate
- [X] T027 [US2] Create `W/src/HentaiReader.App/Services/Preferences.cs` (JSON at `%LOCALAPPDATA%\HentaiReader\preferences.json`: language, adultConfirmed, lastSourceId) and wire `App.xaml.cs`: load preferences, set `Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride` before any resource loads, build services (`SourceCatalog` from `AppContext.BaseDirectory\Sources`, a `SourceSession` factory with `FileStorage` and DPAPI-protected cookie files under `%LOCALAPPDATA%\HentaiReader\sources\<id>\`, `ImageLoader`), open `MainWindow`
- [X] T028 [US2] Create `W/src/HentaiReader.App/Images/ImageLoader.cs`: bytes via the session's `HttpHost.FetchImageAsync` (cookies, headers, domain check, politeness), decode off the UI thread into `BitmapImage` with `DecodePixelWidth`, in-memory LRU (~150 MB by estimated decoded size), per-request cancellation, failed images → placeholder; sprite sources cached by URL and shared
- [X] T029 [US2] Create `W/src/HentaiReader.App/MainWindow.xaml(.cs)`: `NavigationView` with one item per bundled source (unavailable sources disabled, reason in tooltip), Settings in the footer, a `Frame`; select `lastSourceId` (or the first available source) on start
- [X] T030 [US2] Create `W/src/HentaiReader.App/ViewModels/SearchViewModel.cs` (CommunityToolkit.Mvvm): query text, filter values built from the manifest's filters, `SearchCommand` (cancels the running call, clears items), `LoadMoreCommand` via `nextCursor`, status `Idle`/`Loading`/`End`/`Error(kind, message, retryAfter)`, `RetryCommand`; maps `SourceSession` state `Limited` to a blocked UI that only `RetryCommand` clears (FR-013, SC-007); results and errors marshalled to the UI thread
- [X] T031 [US2] Create `W/src/HentaiReader.App/Controls/FilterPanel.xaml(.cs)`: per declared filter — `single` → `ComboBox` with vocabulary labels (language values shown with native names), `multi` → checkboxes, `number` → `NumberBox` within min/max, `range` → two `NumberBox`es; only declared filters rendered (US2-2)
- [X] T032 [US2] Create `W/src/HentaiReader.App/Controls/ResultCard.xaml(.cs)` (cover via `ImageLoader` at card width, title with trimming + tooltip, language, type, pluralised page count, up to 5 key tags) and `W/src/HentaiReader.App/Pages/SourcePage.xaml(.cs)`: search box, filter flyout, `ItemsView` with `UniformGridLayout`, incremental loading when scrolled past 80 %, progress ring, end-of-results and empty states, error `InfoBar` with title/body/retry/copy details
- [X] T033 [US2] Verify quickstart §2 steps 2–3 manually on Hitomi and E-Hentai (live), including a forced error (disconnect network → `Network` message) and that no request is sent after a `RateLimited` until Retry (use the runner's hand-made 429 fixture scenario via a debug `OfflineTransport` toggle or by inspection of the request log)

**Checkpoint**: MVP — the app browses and searches both sources.

---

## Phase 5: User Story 3 — Gallery details with page thumbnails (P2)

**Goal**: Gallery metadata, tag groups, clickable tags, one correctly cropped thumbnail per page.

**Independent Test**: quickstart §2 step 4.

- [X] T034 [US3] Create `W/src/HentaiReader.App/Controls/SpriteThumbnail.xaml(.cs)`: for an `ImageRequest` with `crop`, show the shared decoded sprite inside a container clipped to the crop rectangle (image offset by −x/−y, scaled to the cell size by a `Viewbox`); without `crop`, a plain image; fixed aspect from the page size so the grid never jumps
- [X] T035 [US3] Create `W/src/HentaiReader.App/ViewModels/GalleryViewModel.cs` and `W/src/HentaiReader.App/Pages/GalleryPage.xaml(.cs)`: titles, cover, language, type, upload date (locale format), rating, uploader, pluralised page count, tags grouped by namespace (`NamespaceLabel`) as buttons, a virtualised thumbnail grid of `SpriteThumbnail`, `NotFound` and other errors as `InfoBar`; open from `SourcePage` on card click
- [X] T036 [US3] Tag click → navigate back to `SourcePage` of the same source with query `<namespace>:<name>` where spaces become `_` (and `other` namespace stays `other:`); verify both sources accept this form; if a source does not, fix it in the script (E-Hentai: translate `ns:a_b` tokens into the site's quoted exact form `ns:"a b$"` in `listingUrl`), add a recorded case for it in that source, re-run `npm test`, bump the source's patch version and CHANGELOG, and document the convention ("sources accept `namespace:name` with `_` for spaces in `SearchQuery.text`") in `contract/host-api.d.ts` `SearchQuery.text` doc comment as a contract 1.1.1 PATCH (`contract/package.json`, `contract/CHANGELOG.md`)

---

## Phase 6: User Story 4 — Six languages from the first screen (P2)

**Goal**: All strings in six languages, plurals via ICU, language switch with restart, no clipping.

**Independent Test**: quickstart §3; `Resources.*` tests pass.

- [X] T037 [P] [US4] Create `W/src/HentaiReader.App/Strings/{de-DE,fr-FR,es-ES,ja-JP,uk-UA}/Resources.resw` with every English key translated (machine draft allowed, reviewed for UI fit); plural families per CLDR: `uk` `_one/_few/_many/_other`, `fr` `_one/_many/_other`, `de`/`es`/`en` `_one/_other` (es also `_many`), `ja` `_other` only
- [X] T038 [P] [US4] Create `W/tests/HentaiReader.Host.Tests/Resources/ResourceParityTests.cs` (theory per language `Resources.<tag>`: same key set as en-US, no empty values, every plural family has `_other`) and `PluralTests.cs` (system ICU categories: uk 1→one, 2→few, 5→many, 21→one; en 1→one, 2→other; ja 1→other; fr 0→one) — tests reference the `.resw` files by repo path and P/Invoke `icu.dll` directly
- [X] T039 [US4] Create `W/src/HentaiReader.App/Pages/SettingsPage.xaml(.cs)`: language picker (System + six native names); on change save preferences and restart via `Microsoft.Windows.AppLifecycle.AppInstance.Restart("--source=<current id>")`; on start honour `--source`
- [X] T040 [US4] Layout pass over every page for long German and Japanese strings (wrapping or trimming with tooltip, `MinWidth`s removed where they clip) at 1024 px width and 150 % scaling; fix and re-check (SC-005)

---

## Phase 7: User Story 5 — Adult-content confirmation (P3)

**Goal**: One-time confirmation before any source request; decline closes the app.

**Independent Test**: quickstart §2 step 1.

- [X] T041 [US5] Create `W/src/HentaiReader.App/Dialogs/AdultGateDialog.xaml(.cs)` (`ContentDialog`, translated) shown from `App.xaml.cs` before `MainWindow` selects a source when `adultConfirmed` is false; Confirm → persist and continue; Decline → exit
- [X] T042 [US5] Guard in the `SourceSession` factory (`W/src/HentaiReader.App/App.xaml.cs` service setup): no session (and therefore no request) can be created while `adultConfirmed` is false; unit-style check by starting with a fresh profile and inspecting that no `HttpHost` request was logged before confirmation

---

## Phase 8: Polish & Cross-Cutting Concerns

- [X] T043 [P] Write `windows/README.md` (prerequisites, build, run, test, publish, where data is stored) and `windows/CHANGELOG.md` `0.1.0` (host on contract 1.1, browse/search/gallery, six languages, adult gate)
- [X] T044 [P] Update `contract/README.md` (hosts: runner, Windows; shared `host-glue.js`) and `specs/product-overview.md` roadmap (003 ✅ when merged)
- [X] T045 Portable build: `dotnet publish windows/src/HentaiReader.App -c Release -r win-x64`; run the published folder from another directory with a fresh profile (FR-022); note size in `windows/CHANGELOG.md`
- [X] T046 Run quickstart end to end: parity, app scenarios, languages, publish; measure start-to-source-list time (SC-002 < 2 s) and results time (SC-003 ≤ 5 s); scroll 500 results (SC-004)
- [X] T047 Final gate: `npm test`, `npx tsc -p tools/runner/tsconfig.json`, `dotnet build windows/HentaiReader.slnx -c Release -p:Platform=x64`, `dotnet test windows/tests/HentaiReader.Host.Tests -c Release` — all green before the PR

UI translations (template task): covered by T037/T038 (Constitution IX).

---

## Dependencies & Execution Order

```text
Setup (T001–T006) → Foundational (T007–T018)
  → US1 parity (T019–T024)
  → US2 browse (T025–T033) → US3 gallery (T034–T036)
  → US4 languages (T037–T040)  [needs the en-US keys of T026 and the pages]
  → US5 gate (T041–T042)
  → Polish (T043–T047)
```

- US1 depends only on Foundational and can run in parallel with US2's localisation/image groundwork.
- US2 depends on Foundational; US3 on US2 (navigation, ImageLoader); US4 on US2/US3 pages existing;
  US5 on US2's App wiring.
- T036 may touch `sources/ehentai` and `contract/` — run `npm test` before continuing.

## Parallel Opportunities

```text
Setup:        T003 | T004 | T005
Foundational: T009 | T010 | T013 | T014 | T015 (after T007/T008); T017 | T018 after their targets
US2:          T025 | T026 (independent of each other and of US1)
US4:          T037 | T038
Polish:       T043 | T044
```

## Implementation Strategy

1. **Host first, headless** (Phases 1–3): the parity suite proves Jint + AngleSharp behave exactly
   like the runner before any UI exists. Stop and fix engine differences here.
2. **MVP** = US1 + US2: browsing and searching both sources in the app.
3. Then gallery (US3), six languages (US4), adult gate (US5), polish.
4. PR `003-windows-host` → `main` when `npm test` and `dotnet test` are green and CI's new
   Windows job passes; tag `windows-v0.1.0` after merge.

## Notes

- Commits: `refactor(contract): move host glue …`, `feat(windows): …`, `test(windows): …`,
  `ci: …`, and `fix(sources/ehentai): …` / `docs(contract): …` if T036 needs them.
- The Windows suite never rewrites snapshots — the runner owns them.
