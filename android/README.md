# Hentai Reader for Android

The Android app: the same sources, library and reader as the Windows app, for phones. Kotlin and
Jetpack Compose; source scripts run in QuickJS (vendored, own JNI binding), their HTML helpers in
jsoup. Specification: [specs/007-android-app](../specs/007-android-app/).

Android 8.0 (API 26) or newer. Adult content, personal use only.

## Modules

| Module | Contents |
|--------|----------|
| `:quickjs` | QuickJS 2025-09-13 (unmodified, `src/main/cpp/quickjs`) and the JNI shim `qjs_jni.c`. Built by the NDK for the APK and with the host's C compiler for the JVM tests. |
| `:host` | Plain Kotlin/JVM: script engine bridge, HTTP and cookies, HTML, storage, database, archives, reader geometry, downloads, library. Everything that does not need Android, tested on the JVM. |
| `:app` | The Android app: Compose UI, storage access (SAF), download service, notifications, Keystore. |

The contract scripts (`contract/`) and the source scripts (`sources/`) are packed into the APK's
assets at build time; shared strings are generated from the Windows `.resw` files.

## Prerequisites

- JDK 21.
- Android SDK with platform 37, build tools and NDK 30.0.16248370 (`local.properties` with
  `sdk.dir`, or `ANDROID_HOME`).
- For the JVM tests, a desktop C toolchain and CMake ≥ 3.22 to build QuickJS for the development
  machine: on Windows MinGW-w64 `gcc` and Ninja on `PATH`; on Linux and macOS the system compiler.
- Node.js 24 for the string generator (`npm ci` at the repository root).

## Build and test

From `android/`:

```sh
./gradlew :host:test                  # unit tests and the parity suite (contract tests, source cases, conformance)
./gradlew :app:lintDebug              # lint
./gradlew :app:assembleDebug          # debug APK in app/build/outputs/apk/debug/
./gradlew :app:connectedDebugAndroidTest   # UI tests on a running emulator or device (not a CI gate)
```

From the repository root, after changing Windows strings:

```sh
npm run l10n:android            # regenerate res/values*/strings_shared.xml
npm run l10n:android -- --check # what CI runs
```

Android-only strings live in `res/values*/strings_android.xml`, in all six languages.

## Signing and release

The same release system as the other Android projects (circuitopedia). Only CI signs: the release
build is signed when `app/keystore.properties` exists, which the release workflow writes and
deletes again; it is git-ignored, as is `*.jks`. The version comes from the tag, never from the
repository: `-PappVersionName` (the tag without `android-v`) and `-PappVersionCode` (the workflow
run number). Local builds are `0.1.0-dev` with version code 1.

To release:

1. Merge to `main` through a pull request, with the version added to [CHANGELOG.md](CHANGELOG.md).
2. Tag the merge commit `android-vX.Y.Z` and push the tag.
3. Approve the run of [android-release](../.github/workflows/android-release.yml) (the `build`
   job runs in the `release` environment). It refuses tags not on `main`, validates the Gradle
   wrapper, checks the generated strings, runs the tests and the parity suite, builds and lints the
   signed APK and checks the 40 MB limit.
4. The `publish` job, which never sees the secrets, creates the GitHub Release with
   `hentai-reader.apk`, `hentai-reader.apk.sha256` and generated release notes.

Repository setup (once): an environment `release` with required reviewers, and the secrets
`ANDROID_KEYSTORE_BASE64` (the keystore, base64), `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`
and `ANDROID_KEY_PASSWORD`, named as in the other projects.

To try a signed release build locally, put a `keystore.properties` next to `app/build.gradle.kts`
(`storeFile` relative to `app/`, `storePassword`, `keyAlias`, `keyPassword`) and run
`./gradlew :app:assembleRelease -PappVersionName=X.Y.Z`; delete it afterwards.

## Install

Download `hentai-reader.apk` from the release, check it against `hentai-reader.apk.sha256`,
and open it on the phone (allow installing from that app when asked). Updates install over the
previous version; the library, history and settings stay. A debug build installs with
`adb install -r app/build/outputs/apk/debug/app-debug.apk`.

Third-party components: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) (also under Settings →
About in the app).
