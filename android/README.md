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

`assembleRelease` signs when a keystore is configured, either through environment variables
(`HR_KEYSTORE`, `HR_KEYSTORE_PASSWORD`, `HR_KEY_ALIAS`, `HR_KEY_PASSWORD`) or a git-ignored
`android/keystore.properties`:

```properties
storeFile=C:/path/to/release.jks
storePassword=...
keyAlias=...
keyPassword=...
```

Without one, the release APK is built unsigned.

Releases: set `versionName` / `versionCode` in `gradle.properties`, add the version to
[CHANGELOG.md](CHANGELOG.md), and push a tag `android-vX.Y.Z`. The
[android-release](../.github/workflows/android-release.yml) workflow tests, builds the signed APK
from the repository secrets and attaches it with its SHA-256 to the GitHub Release.

## Install

Download `hentai-reader-android-X.Y.Z.apk` from the release, check it against the `.sha256` file,
and open it on the phone (allow installing from that app when asked). Updates install over the
previous version; the library, history and settings stay. A debug build installs with
`adb install -r app/build/outputs/apk/debug/app-debug.apk`.

Third-party components: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) (also under Settings →
About in the app).
