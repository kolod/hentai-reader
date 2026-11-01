---

description: "Task list for feature 007-android-app"
---

# Tasks: Android App at Parity with Windows

**Input**: Design documents from `/specs/007-android-app/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md),
[data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: MANDATORY (Constitution VII): the JVM parity suite
([parity-suite-android.md](contracts/parity-suite-android.md)) and `:host` unit tests ported from
`windows/tests/HentaiReader.Host.Tests` (geometry, planner, scheduler, cookie jar, cache, database,
archives incl. crash cases, scanner, queue, cache move), plus new tests for the jsoup guards,
image budget and free-space reserve. UI is checked with a few Compose UI tests (not a CI gate) and
the manual checklist in [quickstart.md](quickstart.md).

**Organization**: Foundational = build, engine, bridge, HTTP, HTML, storage, preferences,
database, strings, app shell (drawer, immersive, Back, adult gate). US1 (parity), US2 (browse),
US3 (reader) and US4 (save + offline + background) are P1 and form the MVP. US5–US7 add
history/library management, settings/sign-in/comfort and the remaining resource work.

**Porting rule**: where a task says "port", the Kotlin file keeps the Windows file's behaviour,
names and tests. Read the Windows source and its tests first. Any intended difference is listed in
the task.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: parallelisable (different files, no unfinished dependencies)
- **[Story]**: US1–US7 from spec.md
- Paths relative to the repository root. Abbreviations:
  - `D` = `android/`
  - `Q` = `android/quickjs/`
  - `H` = `android/host/src/main/kotlin/hentaireader/host/`
  - `HT` = `android/host/src/test/kotlin/hentaireader/host/`
  - `A` = `android/app/src/main/kotlin/hentaireader/app/`
  - `R` = `android/app/src/main/res/`
  - `WH` = `windows/src/HentaiReader.Host/`
  - `WA` = `windows/src/HentaiReader.App/`
  - `WT` = `windows/tests/HentaiReader.Host.Tests/`

---

## Phase 1: Setup

- [X] T001 Create the Gradle build in `D`:
  - `settings.gradle.kts` (modules `:host`, `:quickjs`, `:app`), root `build.gradle.kts`, `gradle.properties` (`versionName=0.1.0`, `versionCode=1`, AndroidX on, configuration cache), Gradle wrapper;
  - `gradle/libs.versions.toml` pinning the **newest stable** Kotlin 2.3.x, AGP, Compose BOM, Material 3, Navigation Compose, Activity/Lifecycle Compose, jsoup 1.21.x, OkHttp 5.x, `androidx.sqlite:sqlite-bundled`, kotlinx-coroutines, kotlinx-serialization-json, JUnit 5, NDK, CMake, `compileSdk`/`targetSdk`. Verify each version on Maven Central or the developer site and note the date in a comment;
  - `minSdk 26`, JDK 21 toolchain.
- [X] T002 [P] Extend `.gitignore` for Android (Constitution X): `android/**/build/`, `android/.gradle/`, `android/**/.cxx/`, `android/local.properties`, `android/keystore.properties`, `*.jks`, `*.keystore`, `android/.idea/`, `*.apk`, `*.aab`. Check with `git status` that the keystore patterns match.
- [X] T003 [P] `:host` module `D/host/build.gradle.kts`:
  - Kotlin JVM library with no Android dependency: jsoup, OkHttp, sqlite-bundled, coroutines, serialization;
  - JUnit 5 tests with the system property `repoRoot` = repository root, so tests read `contract/`, `sources/` and `windows/src/HentaiReader.App/Strings/`;
  - `java.library.path` pointing to the desktop QuickJS build of T005.
- [X] T004 [P] `:app` module `D/app/build.gradle.kts`:
  - Android application `hentaireader.app`, Compose on, depends on `:host` and the `:quickjs` Android native libraries;
  - ABI filters `arm64-v8a`, `armeabi-v7a`, `x86_64`; R8 and resource shrinking for release;
  - signing config from env `HR_KEYSTORE`, `HR_KEYSTORE_PASSWORD`, `HR_KEY_ALIAS`, `HR_KEY_PASSWORD` or `D/keystore.properties` (none → unsigned release);
  - a `packAssets` task that copies `contract/prelude.js`, `contract/host-glue.js` and every `sources/<id>/<id>.js` (+ manifest data the catalog needs) into generated assets, never into `src/`.
- [X] T005 `:quickjs` module:
  - vendor Bellard QuickJS commit f1139494 (2025-09-13) unmodified into `Q/src/main/cpp/quickjs/` with its LICENSE;
  - write `Q/QUICKJS_VERSION`;
  - `Q/CMakeLists.txt` builds `libhrquickjs` for Android ABIs (NDK via `externalNativeBuild`) and, in a Gradle task `buildDesktopNative`, for the host OS (Linux x64 gcc/clang, Windows x64 MSVC/clang) into `Q/build/desktop/`. `:host:test` depends on it.
- [X] T006 [P] Add an `android` job to `.github/workflows/ci.yml`:
  - path filters `android/**`, `contract/**`, `sources/**`, `windows/src/HentaiReader.App/Strings/**`, `tools/l10n/**`;
  - `ubuntu-latest`, JDK 21, Android SDK/NDK, Node 24 (`npm ci`);
  - steps: `npm run l10n:android -- --check`, `./gradlew :host:test :app:lintDebug :app:assembleDebug`;
  - upload the test reports.

---

## Phase 2: Foundational (blocks all stories)

### Engine and bridge

- [X] T007 JNI shim `Q/src/main/cpp/qjs_jni.c` per [research R2](research.md):
  - runtime and context create/free; `JS_SetMemoryLimit`, `JS_SetMaxStackSize`; interrupt handler with a per-call deadline and a cancel flag;
  - `evalGlobal(code, name)` returning a value handle; `evalModule(code, name)` returning the namespace handle;
  - install `__hostBridge`: sync members call back into Kotlin; async members return a promise from `JS_NewPromiseCapability` with an integer id;
  - `resolve(id, envelopeJson, bytes?)`; `executePendingJobs()`; `call(fnHandle, argsJson)` returning a promise id or a value;
  - ArrayBuffer in and out without copies through base64; exceptions turned into `{type, name, message, stack}` JSON;
  - all handles released on context free.
- [X] T008 `H/engine/QuickJs.kt`:
  - Kotlin JNI facade loading `hrquickjs` (`System.loadLibrary`; tests use `java.library.path`);
  - `H/engine/EngineThread.kt`: one dedicated thread per engine, a work queue, completions posted from coroutines are applied on it, then pending jobs are pumped; call deadline 60 s and memory 128 MiB from the contract;
  - `ScriptError` classification (`ReferenceError: X is not defined` → "forbidden capability: X"; limit hits → `details.limit`). Port the semantics of `WH/Engine/EngineThread.cs` and `WH/Engine/SourceEngine.cs`.
- [X] T009 [P] Port `WH/Engine/Envelope.cs` → `H/engine/Envelope.kt` (kotlinx-serialization, `{value}` or `{error:{kind,message,options}}`) and `WH/Engine/IHostBridge.cs` → `H/engine/HostBridge.kt` (members per [003 host-bridge](../003-windows-host/contracts/host-bridge.md), `frozenNow`).
- [X] T010 [P] Port `WH/Http/Transport.cs`, `WH/Http/Scheduler.cs` and `WH/Http/CookieJar.cs` → `H/http/Transport.kt`, `H/http/Scheduler.kt` and `H/http/CookieJar.kt`: per-source gap and concurrency, exponential backoff, rate-limit stop, domain-scoped cookies, `set-cookie` never exposed.
- [X] T011 `H/http/HttpHost.kt` (port `WH/Http/HttpHost.cs`):
  - domain allow-list on every redirect hop with an own redirect loop;
  - charset from `content-type`, default UTF-8;
  - header names lower-case, repeats joined with `", "`;
  - text or binary bodies.

  `H/http/LiveTransport.kt` on OkHttp with `followRedirects(false)`, no OkHttp cookie jar or cache, per-call cancellation.
- [X] T012 [P] `H/html/HtmlHost.kt` (port `WH/Html/HtmlHost.cs`) on jsoup with the guards of [research R3](research.md):
  - shared forbidden-pseudo-class regex, then the jsoup-only selector list;
  - descendants only (drop the context element);
  - `text`/`ownText` via `wholeText` and own `TextNode`s with contract normalisation (keep NBSP);
  - `html`/`outerHtml` without pretty print;
  - handles valid until the top-level call ends.
- [X] T013 [P] Port `WH/Storage/SourceStorage.cs` → `H/storage/SourceStorage.kt`: per-source JSON file, key 1–128 chars, value ≤ 1 MiB, ≤ 8 MiB per source, atomic write.
- [X] T014 [P] Port `WH/Fixtures/OfflineTransport.cs` → `H/fixtures/OfflineTransport.kt` (fixture pools, `echo`, `delayMs`, unmatched request → abort with `Network`).
- [X] T015 Port `WH/Sources/CallArgs.cs`, `WH/Sources/ManifestRules.cs`, `WH/Sources/SourceCatalog.cs` and `WH/Sources/SourceSession.cs` → `H/sources/*.kt`:
  - calling convention 1.0/1.1+ (settings last, `null` retry hint), contract version gate, `__describe`;
  - session = engine + storage + cookie jar + scheduler + settings;
  - one call at a time, cancellation by a new call.

  `H/HostInfo.kt` reports the contract version read from `contract/package.json` at build time.
- [X] T016 [P] Platform interfaces in `H/platform/`:
  - `SeekableSource.kt` (size, read at offset; writable variant with append and truncate);
  - `DocumentTree.kt` (list children with name/size/modified/mime, create, rename, delete, open);
  - `Clock.kt`;
  - `NetworkState.kt` (connected, metered, change flow).

### Data, preferences, strings

- [X] T017 Port `WH/Data/LibraryDatabase.cs`, `Records.cs` and every `*Repository.cs` → `H/data/*.kt`:
  - same schema v1 on `androidx.sqlite` bundled driver (WAL, foreign keys, `user_version`);
  - Android additions from [data-model.md](data-model.md): `library_folder.display_path`, URI paths, `download.error_kind` values `waiting_wifi` and `stopped_by_system`;
  - no `progress.json` import (nothing to import on Android);
  - every call on `Dispatchers.IO`.
- [X] T018 [P] `H/Preferences.kt`: `preferences.json` with the Windows field names (`WA/Services/Preferences.cs`) plus `WifiOnlyDownloads`, `KeepScreenOn`, `NotificationAsked`. Android defaults `PreloadPages=3`, `CacheLimitMb=512` (range 128–10240). Unknown fields are ignored; writes are atomic.
- [X] T019 [P] Generator `tools/l10n/resw-to-android.ts` + npm script `l10n:android` per [contracts/l10n-android.md](contracts/l10n-android.md):
  - `.resw` → `R/values{,-de,-fr,-es,-ja,-uk}/strings_shared.xml`, with plural families → `<plurals>`, `{n}` → `%n$s`/`%1$d`, escaping rules;
  - errors on dotted keys and on non-English-only keys;
  - `--check` mode; a "do not edit" header.

  Run it and commit the six generated files.
- [X] T020 [P] Hand-written `R/values/strings_android.xml` with the Android-only keys of [contracts/l10n-android.md](contracts/l10n-android.md) (English), plus `de`, `fr`, `es`, `ja`, `uk` translations in `R/values-*/strings_android.xml` (machine translation as a draft).

### Host tests (Foundational)

- [X] T021 Port unit tests from `WT` for scheduler, cookie jar, HTTP host (redirect allow-list, headers, charset), storage limits, envelope and engine limits → `HT/http/*Test.kt`, `HT/storage/SourceStorageTest.kt`, `HT/engine/EngineTest.kt`. Add `Engine.Interrupt` and `Engine.Memory` from [parity-suite-android.md §4](contracts/parity-suite-android.md).
- [X] T022 [P] `HT/html/HtmlGuardsTest.kt`: `Html.JsoupOnlySelectors`, `Html.DescendantsOnly`, `Html.TextSemantics` (shared markup cases from `tools/runner/test/html.test.ts`), the forbidden-pseudo regex identical to the bridge doc.
- [X] T023 [P] `HT/data/LibraryDatabaseTest.kt` and `HT/data/RepositoryTest.kt` (port from `WT/Data/*`): create, re-open, exactly one target folder once folders exist, an empty folder table allowed, history paging (5 000 rows < 1 s), archive search/filter/sort; `HT/PreferencesTest.kt` (defaults, round trip, Windows file tolerated).

### App shell

- [X] T024 `D/app/src/main/AndroidManifest.xml`:
  - single `MainActivity` with `configChanges="orientation|screenSize|screenLayout|smallestScreenSize|keyboardHidden|density|uiMode"`;
  - `allowBackup="false"` + `dataExtractionRules` excluding everything; `enableOnBackInvokedCallback="true"`;
  - permissions `INTERNET`, `ACCESS_NETWORK_STATE`, `FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_DATA_SYNC`, `POST_NOTIFICATIONS`.

  `A/App.kt` (`Application`: creates services lazily after the adult confirmation, `onTrimMemory` hook placeholder); `A/MainActivity.kt` (edge-to-edge, `setContent`).
- [X] T025 [P] `A/system/Immersive.kt` per [android-ui.md](contracts/android-ui.md#shell):
  - hide system bars with `BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE`, re-applied on resume and window focus;
  - `layoutInDisplayCutoutMode = shortEdges` on API 28+;
  - a `Modifier.safeControls()` helper (`safeDrawing` + `displayCutout` insets).
- [X] T026 [P] `A/system/Locale.kt`: app language from `Preferences.Language` (or System), applied in `attachBaseContext` with a locale-wrapped context, `recreate()` on change, `LocaleManager` on API 33+; locale-aware formatters for numbers, dates, relative times and sizes (FR-034, FR-039).
- [X] T027 [P] `A/ui/theme/Theme.kt`: Material 3 light and dark following the system, no dynamic color, typography that renders CJK (system fonts).
- [X] T028 `A/AppNavigation.kt` per [android-ui.md](contracts/android-ui.md):
  - Navigation Compose typed routes, one back stack per section (each bundled source, History, Library, Downloads, Settings);
  - `ModalNavigationDrawer` closed by default, ☰ in each top bar, edge swipe only when the left system-gesture inset is 0, downloads count badge, not composed for the reader route;
  - Back order (dialog → drawer → reader → previous → finish);
  - start page from preferences (fallback: first available source).
- [X] T029 [P] `A/ui/gate/AdultGateScreen.kt`: first-launch confirmation before any service, file or request; decline → `finishAndRemoveTask()`; the answer is stored (FR-035).
- [X] T030 `A/AppServices.kt`: the single owner of `SourceCatalog` (bundled assets), session pool, database, preferences, cookie vault and transports. It is created only after the gate and exposed to view models.

**Checkpoint**: `./gradlew :host:test` green (Foundational tests); the app starts, shows the gate, then an empty drawer shell in full screen in all six languages.

---

## Phase 3: User Story 1 — Same script behaviour as the runner and Windows (P1) 🎯 MVP

**Goal**: the Android host passes the contract suite and every recorded case, identical to the runner's snapshots.

**Independent Test**: `./gradlew :host:test`. All `Contract.*`, `Case.*` and conformance tests pass, and a deliberately changed snapshot fails.

- [X] T031 [US1] `HT/parity/ContractSuiteTest.kt`: one dynamic JUnit test `Contract.<name>` per `contract/tests/<name>/` with politeness, `echo`/`delayMs`, cookie seeds, `other.js`, `settings`, and every `expect.json` assertion (`minGapMs` ±2 ms, `maxInFlight`, `refusedUrls`, `cookies.sentTo/notSentTo`). Port from the Windows parity tests in `WT`.
- [X] T032 [US1] `HT/parity/SourceCasesTest.kt`: `Case.<source>.<case>` for every `sources/*/tests` and `contract/conformance/*/tests`:
  - fresh session, offline pool, frozen clock = `recordedAt`, storage seed, settings merge;
  - `@case` references in dependency order; `expectError` kinds;
  - JSON deep equality with `<name>.expected.json`, printing the first differing path.
- [X] T033 [P] [US1] `HT/parity/ConformanceTest.kt`: `broken-forbidden-capability` gives "forbidden capability: fetch", `broken-contract-version` is refused at load, the other broken samples fail as on Windows.
- [X] T034 [P] [US1] `HT/parity/EngineVersionTest.kt`: `Q/QUICKJS_VERSION` equals the commit named in `node_modules/@jitl/quickjs-wasmfile-release-sync/README.md` (skipped with a message when `node_modules` is absent).
- [X] T035 [US1] Run the suite and fix differences in the host (or, if a script relies on engine-specific behaviour, in the script, re-verified with the runner and Windows). Record the results (counts per group, engine differences found) in [research.md](research.md) as "R2a. Found while implementing".
- [X] T036 [US1] Update `contract/README.md` "Hosts" table (Android: QuickJS / jsoup, contract version) and the "Contract tests for hosts" paragraph with the Android command.

**Checkpoint**: Constitution VII holds for three hosts.

---

## Phase 4: User Story 2 — Browse, search and open a gallery on the phone (P1)

**Goal**: drawer → source → front feed, search with declared filters, endless results, gallery details with thumbnails, tag search, per-kind errors.

**Independent Test**: quickstart "Shell" and the first two "Browse and read" items: Hitomi `female:glasses` with English + Japanese, three pages of results, open an E-Hentai gallery, sprite thumbnails correct, a tag tap searches.

- [X] T037 [P] [US2] `A/images/ThumbnailCache.kt`:
  - in-memory LRU (16 MiB) of covers and thumbnails decoded at cell size;
  - fetched through the source session (cookies, limiter, headers) per contract 1.1;
  - sprite `crop` applied; failed or refused images leave a placeholder of the same size.
- [X] T038 [P] [US2] `A/ui/common/ErrorPanel.kt`: per-kind messages (`Error_<Kind>_Title/_Body`), Retry, "copy details". After `RateLimited` nothing is sent to that source until Retry (FR-013 of 003).
- [X] T039 [US2] `A/ui/search/SearchViewModel.kt` (port `WA/ViewModels/SearchViewModel.cs`):
  - query, declared filters (multi-language), cursor paging;
  - cancel on a new search; end and empty states;
  - `SavedStateHandle` (text, filters, cursor chain, first visible index) per [data-model.md](data-model.md#saved-screen-state-savedstatehandle).
- [X] T040 [US2] `A/ui/search/SearchScreen.kt`:
  - top bar (☰, search field, star placeholder wired in US6, filter button);
  - filter bottom sheet with exactly the declared filters and translated labels; closing it without a change does not reload;
  - adaptive grid (min 160 dp) of `ResultCard` (cover, title, language, type, page count, key tags);
  - append without jump.
- [X] T041 [P] [US2] `A/ui/gallery/GalleryViewModel.kt` + `A/ui/gallery/GalleryScreen.kt`:
  - metadata, tags grouped by namespace (`Namespace_<ns>`), thumbnail grid with sprite crop;
  - tag tap → search in the same source; not-found message;
  - actions Read (US3), Continue/progress (US3), Save to library (US4), Open in browser.
- [X] T042 [US2] Wire the source list into the drawer from `SourceCatalog`, with an unavailable state and reason (003 FR-010). Remember `LastSourceId`.
- [X] T043 [P] [US2] Compose UI test `D/app/src/androidTest/kotlin/hentaireader/app/ShellTest.kt`: the drawer opens with ☰, closes on an item, scrim and Back; no system bars are visible; Back on the first screen finishes the activity.

**Checkpoint**: browse loop works on a phone, immersive, with the hidden drawer.

---

## Phase 5: User Story 3 — Read in one seamless strip with touch (P1)

**Goal**: full-screen reader, reserved slots, anchored corrections, fling, pinch zoom, pan, tap toolbar, rotation, failures and limits, Back to an unchanged search.

**Independent Test**: quickstart "Browse and read" on a mid-range phone: a 200-page E-Hentai gallery and a long webtoon, no jump, no blank page in view, zoom, rotate, scrubber.

- [X] T044 [P] [US3] Port `WH/Reading/StripGeometry.cs` and `WH/Reading/LoadPlanner.cs` → `H/reading/StripGeometry.kt` and `H/reading/LoadPlanner.kt`. Port their tests from `WT` to `HT/reading/`:
  - (page, offset) anchor;
  - corrections above the view leave the visible content unchanged;
  - preload by pages + screen height; visible pages first; jump targets first.
- [X] T045 [P] [US3] `H/reading/ImageBudget.kt` per [contracts/resource-budgets.md](contracts/resource-budgets.md):
  - budget bytes and max entry (budget / 8);
  - release order behind → ahead beyond 1 page + 1 screen → rest ahead, never visible;
  - `shrink(factor)` and `restore()`.

  Tests `HT/reading/ImageBudgetTest.kt`.
- [X] T046 [P] [US3] Port `WH/Storage/PageCache.cs` → `H/storage/PageCache.kt` (same key, file naming, `index.json`, LRU) with the free-space reserve check (min(1 GiB, 10 %), skip write and raise `CacheReserveNotice` once). Tests `HT/storage/PageCacheTest.kt` (eviction, reserve via a fake free-space provider, index rebuild).
- [X] T047 [US3] Port `WH/Reading/PageImageLoader.cs` and `WH/Reading/PageProviders.cs` → `H/reading/PageImageLoader.kt` and `H/reading/PageProviders.kt`:
  - online provider: `getPageUrl` through the session, cache first, 20 s stall → retry hint up to 2×, failure with Retry, rate limit stops the source, exact size from the image refines estimates;
  - archive provider stub filled in US4.

  Port the related `WT` tests.
- [X] T048 [P] [US3] `A/images/Decoder.kt`:
  - decode bytes at the target width: `ImageDecoder` on API 28+ (`HARDWARE` allocator where possible), `BitmapFactory` + `inSampleSize` on API 26–27;
  - `BitmapRegionDecoder` tiles for pages taller than 2 × screen height or above the max entry;
  - animated GIF/WebP as `AnimatedImageDrawable` on API 28+ (first frame on 26–27);
  - every decode on `Dispatchers.Default`, cancellable.
- [X] T049 [US3] `A/ui/reader/ReaderViewModel.kt` (port the logic of `WA/Reader/ReaderSession.cs`):
  - pages with expected sizes and load states; position (page, offset); zoom 0.5–3.0, fit width, gaps;
  - the planner drives provider requests and decodes into `ImageBudget`;
  - progress saved 5 s after scrolling stops, on leaving and on `onStop` (005 FR-018); `SavedStateHandle` restore;
  - webtoon → no gap.
- [X] T050 [US3] `A/ui/reader/StripCanvas.kt` per [research R7](research.md):
  - one `Canvas` drawing visible slots (bitmap, tiles or placeholder of the reserved size);
  - drag and `splineBasedDecay` fling, overscroll, pinch zoom around the focal point with the anchor recomputed, horizontal pan when wider than the screen;
  - straight edges (every page at strip width), gap in the geometry;
  - centred strip with `ReadingWidth` when fit width is off and the screen is wider;
  - small overlays (progress, error + Retry) only for visible slots.
- [X] T051 [US3] `A/ui/reader/ReaderToolbar.kt` + `A/ui/reader/ReaderScreen.kt`:
  - tap toggles the toolbar, which auto-hides after 3 s;
  - bottom: "57 / 312" and scrubber (target bubble, jump on release);
  - top: back, title, go to page (dialog, invalid → message), fit width (lit only at exact screen width), gaps, first page, last page, save to library (US4);
  - safe-area insets; no drawer or top bar of the app; rate-limit banner with waiting time and Retry.
- [X] T052 [P] [US3] `A/system/KeepScreenOn.kt`: keep the screen on while the reader is shown and the last touch was < 10 min ago, if `KeepScreenOn` (FR-014).
- [X] T053 [US3] Wire Read/Continue/thumbnail-tap from the gallery screen and the details' progress line ("page 40 of 120", read mark) to `ProgressRepository`. Rotation and fold keep (page, offset) through the in-process configuration change (FR-015). Back returns to the details and then to the unchanged search without requests.
- [ ] T054 [P] [US3] Compose UI test `D/app/src/androidTest/kotlin/hentaireader/app/ReaderToolbarTest.kt`: tap toggles the toolbar; Back from the reader returns to the gallery; the drawer cannot open in the reader. Uses the offline transport with fixture pages.

**Checkpoint**: online reading is complete; together with US1–US2 this is a usable phone reader.

---

## Phase 6: User Story 4 — Save galleries and read offline, downloads in the background (P1)

**Goal**: Save to library → foreground-service download with notification → `.cbz` in a user-chosen folder → offline reading. Archives are interchangeable with Windows.

**Independent Test**: quickstart "Downloads and library" items 1–6: first-save folder prompt, save with the screen off, force-stop resume, Wi-Fi only, offline read, Windows ↔ Android archive exchange.

- [X] T055 [P] [US4] Port `WH/Archive/ZipDirectory.cs`, `ImageFormat.cs`, `ArchiveMetadata.cs` and `CbzReader.cs` → `H/archive/*.kt` over `SeekableSource`:
  - end record and central directory, entry bytes, metadata format 1, `ComicInfo.xml`;
  - content ID per the 006 data model;
  - `app` = "Hentai Reader <version> (Android)".
- [X] T056 [P] [US4] Port `WH/Archive/CbzWriter.cs` and `MetadataUpdater.cs` → `H/archive/CbzWriter.kt` and `H/archive/MetadataUpdater.kt`:
  - stream STORED entries with precomputed CRC into a writable `SeekableSource`;
  - append-only metadata update with compaction;
  - `H/archive/FileNames.kt` with the Windows rules + FAT/exFAT (no `"*/:<>?\|`, no trailing dot or space, ≤ 120 UTF-8 bytes, collision suffix).
- [X] T057 [US4] Port archive tests from `WT/Archive/*` → `HT/archive/*Test.kt`:
  - every entry STORED; names and order;
  - metadata validates against `contract/archive/hentai-reader.schema.json`;
  - foreign archive by name order + stable content ID; images byte-identical after update;
  - simulated crash at several offsets; compaction keeps unknown fields.

  Add `CrossPlatformArchiveTest`: builds, at test time, an archive in the exact layout of `contract/archive/README.md` (Windows entry names, `ComicInfo.xml`, metadata with `"app": "… (Windows)"`, tiny generated PNG pages) and checks it opens with identity, pages, progress and reader settings. Real archives in both directions are covered by the manual check (SC-009).
- [X] T058 [US4] Port `WH/Downloads/DownloadQueue.cs` → `H/downloads/DownloadQueue.kt`:
  - per-source workers up to the source's limit; states and transitions of [data-model.md](data-model.md#download-android) incl. `waiting_wifi` and `stopped_by_system`;
  - staging in a host-provided directory, cache reuse, limits and sign-in stop, resume without re-fetch, cancel removes staging and partial file;
  - free-space check before start;
  - assembling into `DocumentTree` as `<name>.cbz.partial`, then rename.

  Port `WT/Downloads/*` tests with a fake transport and an in-memory `DocumentTree`.
- [X] T059 [P] [US4] `A/storage/SafDocumentTree.kt` and `A/storage/SafSeekableSource.kt`:
  - `DocumentsContract` child queries (one query per folder), create, rename, delete;
  - `openFileDescriptor` `r`/`rw` + `FileChannel`;
  - persisted URI permissions (`takePersistableUriPermission`); availability reasons `ok`, `missing`, `no_access`.
- [X] T060 [US4] First-save flow in `A/ui/gallery/SaveToLibrary.kt`:
  - with no library folder, open `OPEN_DOCUMENT_TREE` at Documents with a prompt (`Library_ChooseFolder_Prompt`) and store the folder as the target;
  - request `POST_NOTIFICATIONS` once on API 33+ (`NotificationAsked`);
  - Save to library / "12 / 40" progress / In library states on the gallery screen and the reader toolbar (006 FR-001).
- [X] T061 [US4] `A/downloads/DownloadService.kt`:
  - foreground service type `dataSync`, started from a user action or at app start with unfinished downloads; hosts the `DownloadQueue`;
  - stops itself when idle; `onTimeout` (API 35+) → queue paused as `stopped_by_system`;
  - `A/system/Notifications.kt` per [android-ui.md](contracts/android-ui.md#download-notification): channel, ongoing progress, Pause/Resume actions, tap → Downloads, final notification.
- [X] T062 [P] [US4] `A/system/NetworkStateImpl.kt`: `NetworkCallback` with `NET_CAPABILITY_NOT_METERED` feeding `NetworkState`; the queue waits while `WifiOnlyDownloads` and the network is metered, and resumes automatically (FR-023).
- [X] T063 [US4] Archive page provider in `H/reading/PageProviders.kt` (sizes from metadata, bytes off the main thread). Opening a saved gallery uses the same reader (006 FR-012). Progress and reader settings are written back via `MetadataUpdater` on leaving and `onStop`; a refused `rw` leaves `metadata_dirty`.
- [X] T064 [US4] `A/ui/downloads/DownloadsScreen.kt`: list with state, progress and reason (limit, waiting for Wi-Fi, stopped by system, failed), Pause/Resume/Cancel, banner when notifications are off.
- [X] T065 [US4] Minimal `A/ui/library/LibraryScreen.kt`: list saved galleries (cover, title, source, pages, progress, folder), open in the reader offline, long press → delete with confirmation (`Library_DeleteFromDevice_Body`) via `DocumentsContract.deleteDocument`; the history entry is kept. Search, filters and sorts follow in US5.

**Checkpoint (MVP complete)**: US1–US4 are an Android reader at parity for browsing, reading and saving; tag `android-v0.1.0` may be cut here after the Polish release tasks.

---

## Phase 7: User Story 5 — History, library management, continue (P2)

**Goal**: History screen; Library search/filter/sort; rescans of hand-copied archives; unavailable folders.

**Independent Test**: quickstart "History: three partly read galleries…" and library items: pull-to-refresh lists copied archives, SD card removal shows the folder as unavailable.

- [ ] T066 [P] [US5] Port `WH/Library/LibraryScanner.cs`, `LibraryFolders.cs` and `LibraryService.cs` → `H/library/*.kt` over `DocumentTree`:
  - change detection by size and time;
  - foreign comic archives by file name with content ID; unreadable files listed as such;
  - identity survives rename and move;
  - folder removal with Move (background, progress, duplicate handling as Windows 0.4.0) or Keep files.

  Port `WT/Library/*` tests with an in-memory tree.
- [ ] T067 [US5] Rescan triggers in `A/ui/library/LibraryViewModel.kt`: on app start (after the first frame), on opening the Library and on pull-to-refresh. Folder states `missing` and `no_access` are shown with "Grant access" (re-pick) and "Remove folder".
- [ ] T068 [US5] Complete `A/ui/library/LibraryScreen.kt`: search by title while typing, source and read filters, three sorts, pull-to-refresh, folder name per card, unavailable galleries greyed (006 FR-011, FR-013, FR-017).
- [ ] T069 [P] [US5] `A/ui/history/HistoryViewModel.kt` + `A/ui/history/HistoryScreen.kt`:
  - newest first with cover, progress and relative time; progressive paging;
  - open at the saved position (library copy first) with per-gallery zoom, width mode and gaps;
  - swipe/long press removes; Clear history with confirmation;
  - "not found" with remove (006 FR-018 – FR-020).
- [ ] T070 [P] [US5] Port `WA/Services/HistoryCovers.cs` → `A/images/HistoryCovers.kt`: covers from the archive or page cache; network only with `NetworkCovers`, one visible row at a time; ≤ 300 px JPEG q85 in `covers/`.

**Checkpoint**: history and library behave as on Windows.

---

## Phase 8: User Story 6 — Settings, sign-in and comfort features (P2)

**Goal**: all Windows settings sections with Android differences; E-Hentai sign-in in the app; star default query; start page.

**Independent Test**: quickstart "Settings and accounts" and "Languages".

- [ ] T071 [US6] `A/ui/settings/SettingsScreen.kt` + `SettingsViewModel.kt` per [android-ui.md](contracts/android-ui.md#settings-android-differences). Sections:
  - language (six + System, applied at once);
  - start page; default searches (list with remove);
  - reader (preload 3–5, fit width, reading width, gap, keep screen on);
  - downloads (Wi-Fi only);
  - library folders (add with the picker, target, remove with Move/Keep, state);
  - per-source declared settings with translated labels (`Setting_<id>`).
- [ ] T072 [P] [US6] Port `WH/Reading/LoginCookies.cs` → `H/reading/LoginCookies.kt` (which cookies prove sign-in, `shareWith`). `A/storage/CookieVault.kt`: AES-GCM with an Android Keystore key `hr_cookies`, `cookies.bin` in `noBackupFilesDir`, never logged (FR-036).
- [ ] T073 [US6] `A/ui/login/LoginScreen.kt`:
  - `WebView` on `manifest.loginPage.url`, navigation limited to the login domains, other links to the system browser;
  - on the proving cookies, copy them into the source jar for the declared domains only, then clear WebView cookies and storage;
  - paste-cookies dialog validated by `getAccountStatus`; sign out deletes the cookies;
  - account state and image limit in settings (≤ 5 s, SC-014); ExHentai without access explained and blocked (005 FR-026 – FR-031).
- [ ] T074 [US6] Star button in `A/ui/search/SearchScreen.kt` (006 FR-022, FR-023): tap saves the current query and filters as `DefaultQueries[sourceId]`; filled only while the current search equals it; **long press** clears it; opening the source runs the default search.
- [ ] T075 [P] [US6] Cache section in `A/ui/settings/CacheSettings.kt`:
  - limit (128–10240 MB) with current size and Clear cache (progress, settings and sign-ins untouched);
  - location Internal / SD card (only when a card is mounted, identified by volume UUID);
  - the move runs through `H/storage/CacheMover.kt` (port the Windows cache move incl. its tests: usable during the move, continues after restart, failure keeps the old root) with progress.

**Checkpoint**: everything the Windows Settings page offers exists on Android.

---

## Phase 9: User Story 7 — Kind to the phone's resources (P2)

**Goal**: memory pressure handling, engine pool, background silence, free-space reserve notice, process-death restore, verified on a 3 GB phone.

**Independent Test**: quickstart "Resources" on a 3 GB phone: PSS < 400 MB on 500 pages, kill-and-return restore, 30 min background with 0 requests, reserve notice.

- [ ] T076 [US7] `onTrimMemory` in `A/App.kt`:
  - `UI_HIDDEN` → `ImageBudget` keeps only visible pages, thumbnails and covers off screen are dropped;
  - `RUNNING_LOW` and above → budget × 0.5, `ThumbnailCache` cleared, idle source engines closed;
  - restore when shown again (FR-017).
- [ ] T077 [P] [US7] Engine pool in `H/sources/SessionPool.kt`: close a source engine after 2 min idle or on demand; reopen transparently with persistent storage, cookies and settings. Tests `HT/sources/SessionPoolTest.kt`.
- [ ] T078 [US7] Background audit:
  - every coroutine of the UI layer is lifecycle-scoped (`repeatOnLifecycle(STARTED)`);
  - no timers or polling; history network covers off by default;
  - animated images animate only within 1 screen of the view;
  - `A/AppServices.kt` exposes a debug counter of requests while backgrounded, logged in debug builds (SC-007).
- [ ] T079 [P] [US7] One-time `Storage_ReserveNotice` snackbar when `PageCache` reports the reserve (FR-020); download start refused with "not enough space" when the staging estimate exceeds free space minus the reserve.
- [ ] T080 [US7] Process-death restore: verify and complete the `SavedStateHandle` state of every screen per [data-model.md](data-model.md#saved-screen-state-savedstatehandle) (search re-runs to the saved cursor depth and scrolls to the index; reader opens at page and offset with zoom). Add `D/app/src/androidTest/kotlin/hentaireader/app/RestoreTest.kt` using `ActivityScenario.recreate()` with saved state.
- [ ] T081 [US7] Measure on the 3 GB phone per [quickstart.md](quickstart.md) "Resources", tune the budget values in [contracts/resource-budgets.md](contracts/resource-budgets.md) if needed, and record measured PSS, start time and APK size in [research.md](research.md) as "R9a. Measured".

**Checkpoint**: all stories complete.

---

## Phase 10: Polish & Cross-Cutting Concerns

- [ ] T082 [P] Layout pass at 360 dp and font scale 1.3 in German and Japanese on every screen and dialog; fix clipping (SC-013); content descriptions for icon-only buttons; touch targets ≥ 48 dp.
- [ ] T083 [P] Complete translations: every key in `strings_android.xml` in all six languages; Ukrainian plural forms for `Notify_Progress` and `Notify_MoreQueued`; `npm run l10n:android -- --check` clean (Constitution IX).
- [ ] T084 [P] `D/THIRD-PARTY-NOTICES.md` (QuickJS MIT, jsoup MIT, OkHttp Apache-2.0, AndroidX/Compose Apache-2.0, Kotlin libraries Apache-2.0, SQLite public domain) and an in-app licences screen entry under Settings → About.
- [ ] T085 Release workflow `.github/workflows/android-release.yml`: on tags `android-v*`, run `:host:test`, `assembleRelease` signed from repository secrets (keystore base64 decoded to a temp file, never cached), attach the APK and its SHA-256 to the GitHub Release; verify the APK size < 40 MB (SC-012).
- [ ] T086 [P] Docs: `D/README.md` (build, test, desktop native prerequisites, signing, install); `D/CHANGELOG.md` `[0.1.0]` with everything added; `specs/product-overview.md` roadmap item 7 marked done; root `README.md` Android section (Constitution X).
- [ ] T087 Run the full [quickstart.md](quickstart.md): automated checks, then the manual checklist on a real phone (reference and 3 GB). Record unchecked items as follow-up tasks here.

---

## Dependencies & Execution Order

### Phase dependencies

- **Setup (Phase 1)**: none. T005 (native build) blocks every engine task.
- **Foundational (Phase 2)**: depends on Setup and blocks every story.
  - Engine chain: T007 → T008 → T015.
  - HTTP chain: T010 → T011.
  - The data tasks (T017–T020) and the shell tasks (T024–T030) can run beside the engine work.
- **US1 (Phase 3)**: after Foundational; needs only `:host`.
- **US2 (Phase 4)**: after Foundational. It doesn't need US1's tests to start, but US1 should be green before a phone release.
- **US3 (Phase 5)**: needs US2's gallery screen (T041) to open the reader; T044–T048 can start right after Foundational.
- **US4 (Phase 6)**: needs US3's reader and page cache (T046, T047, T049). The archive tasks T055–T057 can start right after Foundational.
- **US5 (Phase 7)**: needs US4 (archives, minimal Library). T066 can start after T055.
- **US6 (Phase 8)**: needs US2 (search screen for the star) and Foundational. T072 and T075 are independent.
- **US7 (Phase 9)**: needs US3 and US4 (reader, downloads) to measure; T077 can start after T015.
- **Polish (Phase 10)**: after the stories being shipped.

### Within each story

Ported host code and its tests come before the UI that uses them. `:host` tests must pass before the
matching screen is wired. The UI-test tasks come after their screens.

## Parallel Examples

```text
# Foundational, after T008:
T009 Envelope/HostBridge   T010 Scheduler/CookieJar   T012 HtmlHost   T013 SourceStorage   T014 OfflineTransport
T017 database (after T003)  T018 Preferences  T019 l10n generator  T020 Android strings  T025 Immersive  T026 Locale  T027 Theme

# US3, right after Foundational:
T044 StripGeometry/LoadPlanner   T045 ImageBudget   T046 PageCache   T048 Decoder

# US4, right after Foundational:
T055 archive reader   T056 archive writer   T059 SAF tree/seekable   T062 network state
```

## Implementation Strategy

### MVP first (US1 → US4)

1. Setup + Foundational: build, engine, bridge, data, shell.
2. **US1**: the parity suite is green. Stop and confirm the engine is right before any UI work.
3. **US2 + US3**: browse and read on a phone. Validate with the quickstart "Browse and read" items.
4. **US4**: save and offline reading. Run the cross-platform archive check against Windows 0.4.0.
5. Polish tasks T083–T086 for the release, then tag `android-v0.1.0`.

### Incremental delivery

- **US5**: history and library management → `android-v0.2.0`.
- **US6**: settings, sign-in, star, cache location.
- **US7**: resource tuning with measurements → `android-v0.3.0`.

## Notes

- Commit per task or logical group: `feat(android): …`, `test(android): …`, `build(android): …`,
  `ci: …`, `docs(spec): …`, `chore(l10n): …` (Constitution X). Shared inputs (`contract/`, `sources/`,
  `.resw`) are never copied by hand into `android/`.
- If an engine difference forces a script change, it lands with runner and Windows verification in
  the same merge (Constitution II, VII).
- No images, cookies, keystores or APKs are committed. Tests build archives with generated images
  in temp folders.
