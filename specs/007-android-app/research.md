# Research: Android App at Parity with Windows

Phase 0 output for [plan.md](plan.md). Facts checked on 2026-11-01: the runner's QuickJS build
(`node_modules/@jitl/quickjs-wasmfile-release-sync/README.md`), jsoup docs (Context7
`/jhy/jsoup`), the quickjs-kt project page and Compose BOM listings (web). Library versions marked
"pin" are fixed to the newest stable release in the first setup task and recorded in
`android/gradle/libs.versions.toml`.

## Current state that constrains the design

- `android/` holds only a README and a CHANGELOG. Everything is new.
- The Windows app already has the full design for every feature (003, 005, 006). Its UI-free
  `HentaiReader.Host` library holds the engine, bridge, HTTP, HTML, storage, reader planning,
  archives, database, library and downloads. The Android port mirrors that split.
- `contract/host-glue.js` + `contract/prelude.js` are evaluated unchanged by every host; a host
  only implements the private bridge ([003 host-bridge](../003-windows-host/contracts/host-bridge.md)).
- The runner runs **Bellard QuickJS 2025-09-13 (commit f1139494)** through quickjs-emscripten
  0.32, with `setMemoryLimit` and a deadline interrupt handler.
- The archive format is a published cross-platform contract (`contract/archive/`, format 1).
- UI strings exist in six `.resw` files (240 keys, `Area_Element` names without dots, plurals as
  `_one/_few/_many/_other` families, placeholders `{0}`).

## R1. Project layout and toolchain

**Decision**: one Gradle build under `android/` with three modules:

- `:host` — a Kotlin/JVM library with no Android dependency: engine facade, bridge, HTTP, HTML,
  storage, sources, reading planner and geometry, archives, database, library, downloads, page cache.
  Platform services (file access to library folders, cache roots, clock, network state) come in
  through small interfaces.
- `:quickjs` — vendored QuickJS C sources + a JNI shim, built with CMake: for Android ABIs
  through the NDK, and for the development machine (Linux x64 on CI, Windows x64 locally) so
  `:host` tests run on the JVM.
- `:app` — the Android application: Compose UI, foreground download service, system folder picker,
  credential storage, image decoding, WebView sign-in.

Toolchain: Kotlin 2.3.x (pin), AGP 9.x (pin), JDK 21, NDK r28 (pin), `minSdk 26`,
`compileSdk`/`targetSdk` = newest stable API level (pin), Gradle version catalog.

**Rationale**: the same split as Windows (R1 of 003) lets the parity suite and almost all logic
tests run headless on the JVM in CI, without an emulator. A JVM module is consumed by the Android
app as-is.

**Alternatives**: Kotlin Multiplatform for `:host` (no second target needs it); one app module with
Robolectric tests (slow, and the engine would still need a desktop native build).

## R2. Script engine: own JNI binding over vendored QuickJS

**Decision**: vendor Bellard QuickJS at the **same commit the runner uses** (2025-09-13,
f1139494) under `android/quickjs/src/main/cpp/quickjs/`, plus a JNI shim of roughly 500 lines
(`qjs_jni.c`) exposing only what the bridge needs:

- runtime/context create and free; `JS_SetMemoryLimit(128 MiB)`, `JS_SetMaxStackSize`;
  `JS_SetInterruptHandler` with a per-call deadline (60 s) and a cancel flag;
- evaluate global script (prelude, glue: completion value kept as a handle), evaluate module
  (source) and get its namespace;
- install `__hostBridge` with native functions; async bridge members return promises created with
  `JS_NewPromiseCapability`; the host resolves them later by id;
- pump the job queue (`JS_ExecutePendingJob`) on the engine thread;
- bytes in and out as `ArrayBuffer` (`JS_NewArrayBufferCopy`, `JS_GetArrayBuffer`), no base64;
- frozen clock: `Date.now` and `new Date()` overridden by the glue from `frozenNow`, as in the
  runner (nothing native).

Kotlin side (`:host/engine`): one `EngineThread` per source session that owns the runtime and is the
only thread touching it (QuickJS is single-threaded). Completions from I/O coroutines are posted
to that thread, which resolves the promise and pumps jobs, the same model as Windows R2a.

**Rationale**: the same engine build as the runner makes "identical results" (SC-001) close to
certain: same regex engine, same number formatting, same `JSON.stringify` order. Direct control of
limits, interrupts, promise capabilities and `ArrayBuffer` matches the bridge protocol 1:1.

**Alternatives**:

- quickjs-kt (dokar3, KMP, Android and JVM): idiomatic and maintained, but ES module evaluation
  returns no value (exports must be fetched through a binding), Uint8Array maps to `UByteArray`,
  and memory or interrupt limits are not documented. Every gap would need a workaround in the glue.
- quickjs-wrapper (HarlonWang) and app.cash/zipline: different QuickJS forks and versions (no
  parity with the runner's build), and zipline targets a different model (bytecode, its own bridge).
- Android `JavaScriptSandbox` (WebView-based isolate): needs a recent WebView, has no `ArrayBuffer`
  bridge and no synchronous host calls (`htmlSelect` is synchronous in the protocol).

### R2a. Found while implementing (2026-11-01)

- The JNI shim ended up **thinner than planned**, following the Windows host (003 R2a): promises
  for the async bridge members are created in JavaScript by a small adapter
  (`android/host/src/main/resources/hentaireader/host/engine/qjs-bridge.js`, the twin of
  `jint-bridge.js`). C exposes only sync native callbacks, function calls on value handles, the job
  queue, the limits and ArrayBuffer transfer. Kotlin resolves bridge promises by calling the
  adapter's `settle`/`settleHttp` on the engine thread.
- Strings cross JNI as UTF-16 and are converted in C (pairs ↔ 4-byte UTF-8, lone surrogates as
  3-byte sequences), not through JNI's modified UTF-8, so characters outside the BMP and lone
  surrogates survive both ways (`EngineTest`).
- Module exports come from `JS_GetModuleNamespace` after `JS_EvalFunction` and settling the
  module's evaluation promise.
- Desktop build: MinGW-w64 gcc 13 + Ninja on Windows (statically linked `hrquickjs.dll`), gcc on
  Linux CI. The NDK (30.0.16248370) was installed automatically by AGP from the accepted SDK
  licences.
- **Parity result: no engine differences.** The first run passed every contract test (14) and every
  recorded case (67: Hitomi, E-Hentai, minimal, settings), deep-equal to the runner's snapshots, plus
  the conformance samples. A deliberately changed Hitomi snapshot was detected (`~ title`). No source
  script needed changes for the Android host.
- The jsoup guards of R3 hold (`HtmlGuardsTest`). In particular `<br>` and block boundaries add no
  spaces to `text()`, NBSP is kept, the context element never matches itself, and `html()` is not
  pretty-printed.

## R3. HTML: jsoup with contract-specific guards

**Decision**: jsoup 1.21.x (pin). Document handles per top-level call, as on the other hosts.
Rules, from the shared bridge rules plus jsoup specifics found in its docs:

- The shared forbidden-pseudo-class pre-check runs first (same regex as runner and Windows).
- **jsoup-only selectors are refused as well**: `:containsOwn`, `:containsData`,
  `:containsWholeText`, `:containsWholeOwnText`, `:matches`, `:matchesOwn`, `:matchText`,
  `:matchesWholeText`, `:matchesWholeOwnText`, `:lt/:gt/:eq` variants and `[attr~=regex]`. The shared
  regex does not catch `:containsOwn` (letters after `contains`), and AngleSharp and cheerio reject
  these anyway. A source using one would work only on Android. Implemented as a second Android-only
  check with `InvalidInput`.
- `Element.select` in jsoup **includes the element itself**; the contract requires descendants only.
  Results equal to the context element are dropped.
- `text()` / `ownText()`: jsoup's `text()` adds spaces at block boundaries and `<br>`. The contract
  wants text-content semantics, so the host uses `wholeText()` (and the element's own `TextNode`s for
  `ownText`) followed by the contract normalisation (collapse `[ \t\n\r\f]+`, trim, keep NBSP).
- `html()` / `outerHtml()`: pretty printing **off** (`outputSettings().prettyPrint(false)`) so the
  markup matches AngleSharp and cheerio.
- `:has()` and `:is()` are supported by jsoup (1.17+); CSS3 structural pseudo-classes as well.

**Rationale**: Constitution II names jsoup. Each difference above is a known source of parity
failures; the parity suite (R11) proves the result.

## R4. HTTP, cookies, politeness

**Decision**: OkHttp 5.x (pin) as transport, behind the same `Transport` interface as Windows (live
and offline). Port from Windows: `CookieJar` (per-source, domain-scoped, never exposes
`set-cookie`), `Scheduler` (per-source gap and concurrency, exponential backoff, rate-limit stop),
redirects handled by the host (`followRedirects(false)`, own loop to keep the domain allow-list
check on every hop), charset from `content-type`. Images go through the same limiter (contract 1.1).
OkHttp's own cookie handling and cache are disabled.

**Rationale**: the platform's `HttpURLConnection` has no HTTP/2, no per-call cancellation and
awkward redirect control. OkHttp is the standard Android client: one small dependency, already
inside the platform in older form, and JVM-testable.

**Alternatives**: Ktor client (more layers for the same engine); Cronet (large native library,
works against SC-012).

## R5. Local database and preferences

**Decision**: `androidx.sqlite:sqlite-bundled` (pin) with the androidx SQLite driver API, plain SQL,
**the same schema as Windows `library.db`** (006 data-model, `PRAGMA user_version`), plus two
Android columns ([data-model.md](data-model.md)). `preferences.json` with the Windows field names
(`kotlinx.serialization`), written atomically. Both live in `noBackupFilesDir`.

**Rationale**: the bundled driver runs identically on the JVM (tests) and on Android, and ships one
SQLite version on every API level. The same schema means the Windows repositories port line by
line. Preferences as the same JSON keeps FR-002 (same keys and defaults) checkable.

**Alternatives**: Room (an ORM layer; Windows deliberately has none, Constitution VIII); framework
`SQLiteOpenHelper` (not usable in JVM tests); DataStore (different keys and format from Windows).

## R6. Library folders, archives and the page cache on Android storage

**Decision**:

- **Library folders** are document trees chosen with the system folder picker (`OPEN_DOCUMENT_TREE`)
  with persisted permissions; the database stores the tree URI and a display path. The first save
  without a folder opens the picker at Documents (FR-028).
- **Listing** queries `DocumentsContract` child documents directly (name, size, last-modified, mime)
  instead of `DocumentFile` (one query per folder instead of one per file). Rescans run on start, on
  opening the Library and on pull-to-refresh. Unchanged size and time means skip (Windows R7).
- **Reading archives** goes through a `SeekableSource` interface in `:host`. The app implements it
  with `openFileDescriptor(uri, "r")` and a `FileChannel`. The Windows ZIP central-directory reader
  (`ZipDirectory`) is ported, so no `java.util.zip.ZipFile` (that needs a file path).
- **Writing**: pages are staged in app storage (`noBackupFilesDir/partial/<downloadId>/`), then
  streamed STORED with precomputed CRC into a new document `<name>.cbz.partial` in the target tree,
  then renamed. Metadata write-back uses the Windows append-only update over `openFileDescriptor
  (uri, "rw")`; when a provider refuses `rw`, the row stays `metadata_dirty` (006 FR-009 already
  allows this).
- **Deleting** uses `DocumentsContract.deleteDocument` after a confirmation (there is no recycle bin).
- **Page cache** roots: internal `cacheDir/pages` (default) or a removable card's
  `externalCacheDirs[n]/pages` when mounted. Same index file and eviction as Windows, plus a free
  space reserve (R9). The move runs in the background exactly as on Windows (006 R10).
- **File names**: the Windows rules plus FAT/exFAT restrictions (no `"*/:<>?\|`, no trailing dot or
  space, at most 120 UTF-8 bytes), so an archive copies to Windows unchanged.

**Rationale**: scoped storage allows shared folders only through the picker. Staging in app storage
keeps the half-written state out of user folders (006 FR-006) and makes resume cheap.

**Alternatives**: `MANAGE_EXTERNAL_STORAGE` (broad permission meant for file managers, a privacy
regression); MediaStore Downloads (no folder choice, not usable for removable cards with a custom
layout).

## R7. Reader: one custom strip drawn on a canvas

**Decision**: port `StripGeometry` and `LoadPlanner` (pure) from the Windows host into `:host`
unchanged in behaviour. The app draws the strip with one Compose `Canvas` and its own scroll state
(touch drag, `splineBasedDecay` fling, overscroll), instead of a lazy list:

- page slots come from geometry (exact or estimated sizes, gap, zoom); only visible slots draw;
- **anchoring**: the scroll position is kept as `(page, offset in page)` and the pixel offset is
  derived. A size correction above the view therefore changes nothing in view, by construction
  (Constitution IV);
- **zoom**: pinch changes the strip width around the focal point (anchor recomputed), up to
  `0.5–3.0` as on Windows; wider than the screen enables horizontal pan; fit width resets;
- failed and loading slots show small composables (Retry, progress) only for visible slots;
- tall pages (height > 2 × screen height after scaling) are drawn from tiles decoded with
  `BitmapRegionDecoder` at display scale. Only tiles in or near the view are decoded.

Images are decoded with `ImageDecoder` (API 28+) or `BitmapFactory` with `inSampleSize` (API
26–27), at the displayed width, as `HARDWARE` bitmaps where possible (pixel memory outside the Java
heap, drawn directly). Animated GIF/WebP: `AnimatedImageDrawable` on API 28+, started only within
one screen of the view; API 26–27 shows the first frame.

**Rationale**: a lazy list anchors on its first visible item, so a correction of that item still
moves the view. Pinch-zooming a lazy list means relayout of every item per frame. The Windows reader
already proved the geometry-plus-planner approach, and a single canvas costs one composition, not
one per page.

**Alternatives**: `LazyColumn` with fixed heights (anchoring and zoom issues above); Subsampling
Scale Image View (one image, not a strip); Coil for decoding (a second memory manager competing with
the budget of R9).

## R8. Navigation, hidden pane, full screen, Back

**Decision**: one activity, Navigation Compose with typed routes, one back stack per navigation
section (sources, History, Library, Downloads, Settings) as on Windows.

- **Pane**: Material 3 `ModalNavigationDrawer`, closed by default. Edge-swipe opening is enabled only
  when the left system-gesture inset is 0 (3-button navigation); with gesture navigation the menu
  button opens it. Dragging closes it. Not composed in the reader.
- **Full screen**: edge-to-edge, `WindowInsetsControllerCompat.hide(systemBars())` with
  `BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE`, re-applied on resume and focus gain;
  `layoutInDisplayCutoutMode = shortEdges` (API 28+). Controls use `WindowInsets.safeDrawing` and
  the cutout insets. Transient bars overlay the content without moving it. In split-screen or a
  pop-up window the app fills its area.
- **Back**: `BackHandler` order: dialog or sheet, pane, reader → details → search, section start →
  finish the activity (005 FR-003 amended). Predictive back gesture enabled.
- **Configuration changes**: orientation, screen size, layout and smallest width handled in
  process (`configChanges`), so rotating or unfolding keeps decoded pages and the strip anchor.
  Locale changes recreate the activity (R10).

## R9. Resource budgets (memory, storage, battery, data)

**Decision** (numbers in [contracts/resource-budgets.md](contracts/resource-budgets.md)):

- **Decoded-image budget** = clamp(`memoryClass` × 0.5 MB, 64 MB, 192 MB). On a 3 GB phone
  (`memoryClass` ≈ 256) that is 128 MB, about 12 screens at 1080 × 2400. `ImageBudget` in `:host`
  orders release: farthest behind, then farthest ahead (the preload area shrinks to the minimum),
  never visible pages. One page or tile may use at most 1/8 of the budget, or it is decoded at a
  lower scale or tiled.
- **`onTrimMemory`**: `UI_HIDDEN` drops everything outside the view; `RUNNING_LOW` and above also
  thumbnails, covers and idle source engines. A source engine also closes after 2 minutes unused
  (its storage is persistent, so it comes back cold).
- **Preload default 3** (FR-019); the screen-height rule from 005 still applies.
- **Cache**: default 512 MB, writes paused while free space < min(1 GB, 10 % of the volume),
  checked before each write batch.
- **Background**: no timers or polling. Lifecycle-scoped coroutines stop when the activity stops,
  except the download service. History cover loading from the network stays off by default.
- **Data**: images only through the cache (no re-fetch), WebP preferred (Constitution V). The
  Wi-Fi-only switch uses `NetworkCallback` with `NET_CAPABILITY_NOT_METERED`.

**Rationale**: `memoryClass` reflects what the device grants a normal app; hardware bitmaps keep
the Java heap small, but their pixels still cost RAM, so they count against the same budget.

### R9a. Measured (2026-11-01, emulator only)

Emulator `sdk_gphone64_x86_64`, Android 16, 2.5 GB RAM, heap growth limit 192 MB. **Not yet
measured on a real 3 GB phone**; T081 stays open until it is.

| What | Value | Target |
|------|-------|--------|
| APK, release (R8; natives for arm64-v8a, armeabi-v7a, x86_64) | 8.2 MB | < 40 MB (SC-012) |
| Cold start to first frame, release build signed with the debug key, 4 runs | 2.33–2.42 s | < 3 s (SC-002) |
| Cold start, debug build | 3.4–3.7 s | (debuggable, not optimised) |
| PSS on the search screen after start | 50 MB | — |
| PSS while reading 500 pages | not measured | < 400 MB (SC-005) |

Found while measuring:

- Closing a source engine (the 2-minute idle close, low memory) aborted the app: the JNI shim
  freed the runtime while Kotlin still held value handles (the adapter's `settle`, `settleHttp`,
  `watch` and the call invoker), and QuickJS asserts that no object is alive. Engines were never
  closed while the app ran before T077. The shim now keeps every handle on a list and frees what
  is left before `JS_FreeRuntime`. The desktop build used for the JVM tests is `Release` and so had
  assertions off; it now keeps them (`-UNDEBUG`), though the leak did not reproduce there.
- The engine thread woke up every second while idle (`poll(1 s)`); it now blocks until work
  arrives, so an idle app does nothing.

## R10. Localisation shared with Windows

**Decision**: the Windows `.resw` files stay the single source for every shared string. A Node
script `tools/l10n/resw-to-android.ts` generates `android/app/src/main/res/values{,-de,-fr,-es,-ja,-uk}/strings_shared.xml`
(committed; CI runs it with `--check` and fails on any difference):

- `{0}`, `{1}` → `%1$s`, `%2$s` (or `%1$d` when the English text uses it as a count in a plural);
- plural families `<Key>_<category>` → `<plurals name="Key">` with `quantity` items;
- XML and Android escaping (`'`, `"`, `@` and `?` at the start, `%` not part of a placeholder).

Android-only strings (notification, Wi-Fi only, keep screen on, folder access, permission notes)
live in hand-written `strings_android.xml` per language. Windows-only keys (F11, Explorer, Recycle
Bin) are generated too but unused (lint `UnusedResources` off for the generated file).

The language switch is per app: stored in `preferences.json` (`Language`), applied in
`attachBaseContext` with a locale-wrapped context, then `recreate()`; on API 33+ also set through
`LocaleManager`, so system settings show it. No AppCompat dependency.

**Rationale**: one translation per string for both apps, keys identical by construction
(Constitution IX), no Node needed in the Android build.

## R11. Parity suite on the JVM

**Decision**: `:host` tests (JUnit 5) implement [contracts/parity-suite-android.md](contracts/parity-suite-android.md),
the same procedure as the Windows suite (003 parity-suite): every `contract/tests/*`, every
`sources/*/tests` and `contract/conformance/*/tests` case, offline transport, frozen clock, JSON
deep-equality with the runner's snapshots, plus string-key parity for Android. They run against the
desktop build of the same QuickJS sources and the JNI shim (R2), so the code under test is the
code shipped. An optional instrumented smoke test runs a subset on an emulator before a release.

CI: new job `android` on `ubuntu-latest` (JDK 21, CMake, Android SDK). It builds the native library
for the host, runs `:host:test`, builds `:app:assembleDebug`, runs `lint` and the l10n `--check`.
Path filter: `android/**`, `contract/**`, `sources/**`, the `.resw` files and `tools/l10n/**`.

## R12. Background downloads

**Decision**: a foreground service (type `dataSync`) runs while any download is queued or
downloading and hosts the ported `DownloadQueue`. It shows one ongoing notification (gallery,
"12 / 40", Pause/Resume, tap opens Downloads) and stops itself when the queue is idle. The service
starts only from a user action or at app start with unfinished downloads (allowed while the app is
in the foreground). On Android 15+ `onTimeout` (the daily `dataSync` limit) pauses the queue with
reason `stopped_by_system`; it continues on the next app start. `POST_NOTIFICATIONS` is requested on
the first save (API 33+). Without it, downloads run while the app is open and the Downloads screen
explains why.

**Rationale**: one code path for every API level from 26. The queue already persists state per
page (006 R5), so a system stop loses nothing.

**Alternatives**: WorkManager long-running workers (the same `dataSync` limits plus another layer
of scheduling); user-initiated data transfer jobs (API 34+ only, two code paths).

## R13. Sign-in, cookies, privacy

**Decision**: the E-Hentai login page opens in an `android.webkit.WebView` screen. Navigation is
limited to `manifest.loginPage` domains (`shouldOverrideUrlLoading`), and other links go to the
system browser. Cookies are read from `CookieManager` for the declared domains, copied into the
source's jar, then WebView cookies and storage are cleared. Stored cookies are encrypted with an
AES-GCM key in the Android Keystore (`cookies.bin` in `noBackupFilesDir`).
`android:allowBackup="false"` and data extraction rules exclude everything, so nothing reaches
device or cloud backups. Cookies are never logged (the source `console` goes to logcat only in debug
builds).

## R14. Process death and state restoration

**Decision**: the navigation back stack is saved by Navigation Compose. Each screen's view model
keeps its restorable state in `SavedStateHandle`: search text, filters and cursor chain (results are
loaded again after a process death; within a living process returning from the reader repeats
nothing, as on Windows); reader (source, gallery, page, offset, zoom). Reading progress is also
written to the database 5 s after scrolling stops and on `onStop`, so the restored reader and the
database agree (SC-010).

## R15. Distribution

**Decision**: `:app:assembleRelease` produces one universal APK (`arm64-v8a`, `armeabi-v7a`,
`x86_64`), R8 minified and resource-shrunk, signed only in CI. The release system is the
same as in the owner's other Android projects (circuitopedia, aligned 2026-11-01): a tag
`android-vX.Y.Z` on `main` starts the workflow; its `build` job runs in the `release` environment
(approval required, read-only, the only job with the signing secrets `ANDROID_KEYSTORE_BASE64`,
`ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD`), writes and deletes
`app/keystore.properties`; a separate `publish` job creates the GitHub Release with generated
notes. Version from the tag (`-PappVersionName`) and the run number (`-PappVersionCode`); local
builds are `0.1.0-dev`. Application ID `io.github.kolod.hentaireader`.
The first tag is `android-v0.1.0`. Size target < 40 MB (SC-012); expected about 12–18 MB (QuickJS
and SQLite are about 1–1.5 MB per ABI).

## R16. Testing strategy

- `:host` JVM tests: parity suite (R11); ported Windows unit tests for geometry, planner, scheduler,
  cookie jar, cache with eviction and reserve, database repositories, archive writer, reader and
  updater (including crash cases), scanner on a fake `SeekableSource` tree, download queue with a
  fake transport, cache move; `ImageBudget` release order; jsoup guards (R3).
- `:app`: a few Compose UI tests (pane open and close, Back order, reader toolbar toggle) on an
  emulator, not a CI gate.
- Manual checks on a real phone, as a checklist (owner's preference): immersive mode, rotation,
  memory profile of a 500-page read, background download with the screen off, cross-platform
  archives, all six languages at 360 dp ([quickstart.md](quickstart.md)).
