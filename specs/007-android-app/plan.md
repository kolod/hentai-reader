# Implementation Plan: Android App at Parity with Windows

**Branch**: `007-android-app` | **Date**: 2026-11-01 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/007-android-app/spec.md`

## Summary

The Android app ports the Windows app's design (003, 005, 006) to Kotlin and Jetpack Compose with
the same split. `:host` is a UI-free Kotlin/JVM library with the engine bridge, HTTP, HTML,
storage, reader geometry and planner, archives, database, library, downloads and page cache. It is
ported from `HentaiReader.Host`, uses the same `library.db` schema, `preferences.json` fields and
archive format 1. `:app` holds the Compose UI and the Android glue.

The script engine is the **same Bellard QuickJS commit the runner uses**, vendored with a small JNI
shim. It is built for Android ABIs and for the desktop, so the parity suite runs on the JVM in CI
without a device. jsoup is guarded for the contract's selector and text rules.

The owner's three differences:

- the navigation pane is a modal drawer, closed by default;
- every screen is immersive full screen;
- explicit resource budgets: decoded-image budget from `memoryClass`, trim-memory release, tiled tall
  pages, preload 3, 512 MB cache with a free-space reserve, no background work except a foreground
  download service, a Wi-Fi-only option, state restored after process death.

Shared strings are generated from the Windows `.resw` files. Research: [research.md](research.md).

## Technical Context

**Language/Version**: Kotlin 2.3.x (pin), JDK 21 toolchain; C (QuickJS + JNI shim, C11) via
CMake and NDK r28 (pin)

**Primary Dependencies**:

- Jetpack Compose (BOM, pin), Material 3, Navigation Compose, Activity and Lifecycle (Compose
  integration);
- jsoup 1.21.x (Constitution II), OkHttp 5.x, `androidx.sqlite:sqlite-bundled`,
  kotlinx-coroutines, kotlinx-serialization-json;
- vendored QuickJS 2025-09-13 (f1139494).

Platform only: `ImageDecoder`/`BitmapRegionDecoder`, `DocumentsContract`, Android Keystore,
`WebView`, foreground service.

**Storage**: `library.db` (SQLite, Windows schema + 2 Android columns), `preferences.json`, source
storage, encrypted `cookies.bin`, covers and download staging in `noBackupFilesDir`. Page cache in
`cacheDir/pages` or a removable card. `.cbz` archives in user-chosen document trees
([data-model.md](data-model.md)).

**Testing**: JUnit 5 on the JVM for `:host` (parity suite, ported Windows unit tests, jsoup guards,
image budget, archive crash cases); a few Compose UI tests on an emulator (not a CI gate); a manual
checklist on a real phone ([quickstart.md](quickstart.md)).

**Target Platform**: Android 8.0+ (minSdk 26), `targetSdk` = newest stable (pin); phones and
tablets; ABIs arm64-v8a, armeabi-v7a, x86_64.

**Project Type**: mobile app (Gradle: `:host` JVM library, `:quickjs` native, `:app` Android
application).

**Performance Goals**:

- cold start < 3 s (SC-002); first page < 2 s (SC-003);
- 95 % of pages loaded before view, 0 jumps (SC-004);
- touch response < 100 ms (SC-006).

**Constraints**:

- PSS < 400 MB on a 500-page read, no OOM on a 3 GB phone (SC-005);
- 0 background requests without downloads (SC-007);
- cache within its limit and the free-space reserve (SC-011);
- APK < 40 MB (SC-012);
- nothing on the UI thread (IV); no site logic in Kotlin (I); politeness and stop on limits (VI);
  nothing written before the adult confirmation.

**Scale/Scope**:

- 9 screens: search, gallery, reader, history, library, downloads, settings, sign-in page, adult
  gate;
- about 45 `:host` files ported from about 50 Windows host files, about 35 `:app` files;
- the JNI shim (~500 lines);
- 240 generated plus about 25 Android-only string keys × 6 languages;
- one new CI job and one release workflow.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Constitution v1.1.0.

| # | Principle | Status | Evidence |
|---|-----------|--------|----------|
| I | Native UI, shared source logic | ✅ | Kotlin + Jetpack Compose; no cross-platform UI; `:host` calls only contract functions, no site URLs or parsing in Kotlin |
| II | Thin, sandboxed scripts; one contract | ✅ | QuickJS (named engine) + jsoup (named parser); host API through the shared `host-glue.js`/`prelude.js` unchanged; no other globals; jsoup guards keep the portable selector subset; contract 1.2, no `.d.ts` change |
| III | Verified updates | ✅ / ➖ | Bundled copy of every source shipped (fallback); remote updates are 008 |
| IV | Reading smoothness | ✅ | Geometry from known or estimated sizes before load; anchor as (page, offset) so corrections never move the view; decode at display width (tiles for tall pages); preload 3–5 + screen height; engine, network, decode and I/O off the main thread |
| V | Open storage | ✅ | Same `.cbz` format 1 (STORED, `ComicInfo.xml`, `hentai-reader.json`), metadata in `library.db` **and** the archive; cross-platform open is a success criterion (SC-009); WebP kept as delivered |
| VI | Respect for sources | ✅ | Ported scheduler (gap, concurrency, backoff), stop on 429/509 and E-Hentai limits, cookies per source and domain, encrypted and excluded from backups |
| VII | Testability | ✅ | The same contract suite and recorded cases run against the Android host (JVM, same engine build) in CI; ported unit tests |
| VIII | Simplicity | ⚠️ justified | Own JNI shim instead of an existing binding; OkHttp (Complexity Tracking). No ORM, no image-loading library, no AppCompat, no DI framework |
| IX | Localisation | ✅ | Six languages; shared keys generated from the Windows English source, so they are identical by construction; Android plurals; locale formatting; per-app language; 360 dp layout pass |
| X | Version control | ✅ | Monorepo `/android`; branch `007-android-app`; Conventional Commits `feat(android): …`; tag `android-v0.1.0`; `android/CHANGELOG.md`; keystore never committed (`.gitignore`: `*.jks`, `*.keystore`, `keystore.properties`, `local.properties`, `build/`, `.gradle/`, `.cxx/`); CI job `android` |

Gate result: **pass** (one justified item).

## Project Structure

### Documentation (this feature)

```text
specs/007-android-app/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── parity-suite-android.md    # JVM parity suite, engine version check, jsoup guards, key parity
│   ├── android-ui.md              # drawer, immersive, Back order, touch map, notification, settings
│   ├── l10n-android.md            # .resw → strings.xml generation, Android-only keys
│   └── resource-budgets.md        # memory, preload, storage, background and data rules
├── checklists/requirements.md
└── tasks.md                       # /speckit-tasks
```

### Source Code (repository root)

```text
android/
├── settings.gradle.kts, build.gradle.kts, gradle.properties (version from the release tag, not here)
├── gradle/libs.versions.toml
├── quickjs/
│   ├── QUICKJS_VERSION                       # 2025-09-13 f1139494 (checked against the runner)
│   ├── src/main/cpp/quickjs/                 # vendored upstream sources (MIT), unmodified
│   ├── src/main/cpp/qjs_jni.c                # runtime/context, limits, interrupt, bridge, promises, ArrayBuffer
│   └── CMakeLists.txt                        # Android ABIs + desktop library for :host tests
├── host/src/main/kotlin/hentaireader/host/
│   ├── engine/      QuickJs.kt (JNI facade), EngineThread.kt, Envelope.kt, HostBridge.kt
│   ├── sources/     SourceCatalog.kt, SourceSession.kt, CallArgs.kt, ManifestRules.kt
│   ├── http/        HttpHost.kt, Transport.kt, LiveTransport.kt (OkHttp), Scheduler.kt, CookieJar.kt
│   ├── html/        HtmlHost.kt (jsoup + guards)
│   ├── storage/     SourceStorage.kt, PageCache.kt (+ reserve, CacheMover)
│   ├── reading/     StripGeometry.kt, LoadPlanner.kt, ImageBudget.kt, PageImageLoader.kt,
│   │                PageProviders.kt, LoginCookies.kt
│   ├── archive/     ZipDirectory.kt, CbzReader.kt, CbzWriter.kt, MetadataUpdater.kt,
│   │                ArchiveMetadata.kt, ImageFormat.kt, FileNames.kt
│   ├── data/        LibraryDatabase.kt, *Repository.kt, Records.kt
│   ├── library/     LibraryScanner.kt, LibraryFolders.kt, LibraryService.kt, DocumentTree.kt (interface)
│   ├── downloads/   DownloadQueue.kt
│   ├── fixtures/    OfflineTransport.kt
│   └── platform/    SeekableSource.kt, Clock.kt, NetworkState.kt (interfaces)
├── host/src/test/kotlin/…                    # parity suite + unit tests (reads ../contract, ../sources)
└── app/src/main/
    ├── AndroidManifest.xml                   # configChanges, foreground service dataSync, no backup
    ├── assets/                               # prelude.js, host-glue.js, sources/* packed at build time
    ├── kotlin/hentaireader/app/
    │   ├── MainActivity.kt, App.kt (services, trim memory), AppNavigation.kt (drawer, sections, Back)
    │   ├── system/   Immersive.kt, Locale.kt, KeepScreenOn.kt, Notifications.kt
    │   ├── storage/  SafDocumentTree.kt, SafSeekableSource.kt, CacheRoots.kt, CookieVault.kt (Keystore)
    │   ├── images/   Decoder.kt (ImageDecoder/BitmapFactory, region tiles), ThumbnailCache.kt
    │   ├── downloads/ DownloadService.kt (foreground, notification, Wi-Fi gate)
    │   ├── ui/search/, ui/gallery/, ui/reader/ (StripCanvas.kt, ReaderToolbar.kt, ReaderViewModel.kt),
    │   │   ui/history/, ui/library/, ui/downloads/, ui/settings/, ui/login/ (WebView), ui/gate/
    │   └── ui/theme/
    └── res/values*/strings_shared.xml (generated), strings_android.xml
tools/l10n/resw-to-android.ts                 # + npm script "l10n:android"
.github/workflows/ci.yml                      # + job android (paths: android/**, contract/**, sources/**, *.resw, tools/l10n/**)
.github/workflows/android-release.yml         # tag android-v*: test, assembleRelease (secrets), attach APK
contract/README.md                            # Hosts table: Android row
android/README.md, android/CHANGELOG.md, android/THIRD-PARTY-NOTICES.md (QuickJS MIT, jsoup MIT,
                                              # OkHttp Apache-2.0, AndroidX Apache-2.0, SQLite public domain)
```

**Structure Decision**: Mirror the Windows split. Everything testable without Android goes in
`:host` (pure Kotlin/JVM), file for file with `HentaiReader.Host` wherever behaviour is shared,
so a later fix in one host has an obvious counterpart in the other. Android-only concerns stay in
`:app`: storage access, decoding, Keystore, service, WebView, Compose. Platform access reaches
`:host` through four small interfaces (`DocumentTree`, `SeekableSource`, `Clock`, `NetworkState`).
Shared inputs (`contract/`, `sources/`, Windows `.resw`) are read from their canonical locations
and never copied by hand.

## Post-design Constitution Re-check

The design keeps every row. Notes:

- II/VII: the selected engine build is the runner's own, and the jsoup guards are covered by tests,
  so parity rests on tests, not on hope.
- V: the archive format is reused unchanged (no schema change; `app` is free text).
- IX: generated shared strings remove the risk of key drift.
- VIII: the justified items are below. ✅

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| Own JNI shim over vendored QuickJS (~500 lines of C) (VIII) | Parity with the runner needs the same engine build. The bridge needs memory and time limits, host-resolved promises, `ArrayBuffer` and a synchronous `htmlSelect`. The desktop build lets the parity suite run on the JVM | quickjs-kt: module evaluation returns no exports, `UByteArray` mapping, limits not documented. Other bindings: other QuickJS forks or versions (no engine parity). `JavaScriptSandbox`: no sync calls, no byte bridge |
| OkHttp dependency (VIII) | HTTP/2, per-call cancellation, manual redirects with an allow-list check per hop, timeouts; testable on the JVM | `HttpURLConnection`: no per-call cancel, weak redirect control, HTTP/1.1 only. Ktor or Cronet: more layers or a large native library (SC-012) |
| Custom canvas strip instead of a lazy list (VIII) | Constitution IV anchoring must hold for corrections of the first visible page, and pinch zoom needs no per-item relayout | `LazyColumn` anchors on its first visible item, so that page's correction moves the view, and zoom relayouts every item per frame |
