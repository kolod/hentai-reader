# Quickstart: Android App at Parity with Windows

How to build the app and prove the feature works. Automated checks first, then a manual checklist
on a real phone.

## Prerequisites

- JDK 21, Android SDK (platform = `compileSdk` from `android/gradle/libs.versions.toml`), NDK and
  CMake versions from the same file (installed with `sdkmanager`).
- Node 24 (only for `npm run l10n:android` and the runner).
- A host C compiler for the desktop QuickJS build: gcc/clang on Linux, MSVC Build Tools or clang on
  Windows.
- A phone with Android 8.0+ (reference: 4 GB RAM, 1080 × 2400; low-end check: 3 GB) with USB
  debugging; optionally an emulator (x86_64) for UI tests.

## Automated checks

From the repository root:

```sh
npm ci
npm run l10n:android -- --check          # generated strings are up to date (l10n-android.md)
cd android
./gradlew :host:test                     # parity suite + host unit tests (parity-suite-android.md)
./gradlew :app:lintDebug :app:assembleDebug
```

Expected: every `Contract.*`, `Case.*`, `Html.*`, `Engine.*` and `Strings.*` test passes, and the
same case count as the Windows suite (contract 14 at the time of writing, plus every source case).
A deliberately changed snapshot makes `:host:test` fail.

Optional, emulator connected: `./gradlew :app:connectedDebugAndroidTest` (pane, Back order, reader
toolbar).

Release build (only CI signs; locally, a temporary `app/keystore.properties` with `storeFile`,
`storePassword`, `keyAlias`, `keyPassword`, deleted afterwards — see `android/README.md`):

```sh
./gradlew :app:assembleRelease -PappVersionName=0.1.0   # app/build/outputs/apk/release/*.apk, < 40 MB (SC-012)
adb install -r app/build/outputs/apk/release/app-release.apk
```

## Manual checklist (real phone)

Tick each item. The spec reference is in brackets.

**Shell**

- [ ] Fresh install: the adult confirmation comes first; no request happens before it (check with
      the network monitor or airplane mode) [FR-035]
- [ ] No status or navigation bar on any screen; an edge swipe shows them over the content, and
      they hide again [FR-009]
- [ ] ☰ opens the pane; picking an item closes it; Back closes it; with gesture navigation a
      left-edge swipe goes Back and does not open the pane [FR-007, FR-008]
- [ ] Back from the first screen of a section closes the app [FR-030]

**Browse and read** (US2, US3)

- [ ] Hitomi: `female:glasses`, language English + Japanese, three pages of results, no jump
- [ ] E-Hentai gallery: thumbnails (sprite crop) correct, a thumbnail tap opens the reader at that page
- [ ] Read a 200-page E-Hentai gallery by flinging: no jump in view, no visible placeholder at
      normal speed [SC-004]
- [ ] Pinch zoom around the fingers; pan sideways when wider; fit width returns; gaps toggle [FR-012]
- [ ] Tap toggles the toolbar; scrubber jump to page 150; go to page with an invalid number is
      rejected [FR-013]
- [ ] Rotate twice and fold/unfold (if possible): same page and offset [FR-015]
- [ ] Back → details → search: same results and scroll, no new request [US3 #7]
- [ ] Screen does not turn off while reading; it does on other screens [FR-014]
- [ ] Airplane mode mid-read: loaded pages stay readable, the others show Retry [005 FR-022]

**Resources** (US7, resource-budgets.md)

- [ ] Android Studio profiler, 3 GB phone: 500-page gallery and a long webtoon, PSS < 400 MB, no
      crash [SC-005]
- [ ] Open the camera, return: reader at the same place; `adb shell am kill` the backgrounded
      app, return: same screen and page [SC-010]
- [ ] App in the background 30 min without downloads: 0 requests (proxy or network inspector)
      [SC-007]
- [ ] Fill the storage near the reserve: caching pauses with a one-time notice, reading continues
      [SC-011]

**Downloads and library** (US4, US5)

- [ ] First save asks for a library folder (picker opens at Documents) [FR-028]
- [ ] Save a 40-page gallery, screen off until done; the notification shows progress and Pause works [FR-022]
- [ ] Force-stop during a download, start again: it continues without re-fetching pages [SC-008]
- [ ] Wi-Fi only on, switch to mobile data: download waits with the reason; it resumes on Wi-Fi [FR-023]
- [ ] Airplane mode: the saved gallery opens from the Library and reads to the end
- [ ] Copy a `.cbz` saved by Windows 0.4.0 into the library folder, pull to refresh: listed with
      metadata and progress; it reads. Copy an Android-saved `.cbz` to Windows: it opens there [SC-009]
- [ ] Remove the SD card holding a library folder: its galleries show as unavailable; they come
      back on reinsert
- [ ] Delete a gallery: confirmation, the file is gone, the history entry stays
- [ ] History: three partly read galleries, restart, the second resumes at its page and zoom

**Settings and accounts** (US6)

- [ ] E-Hentai sign-in in the app page: signed in, image limit shown within 5 s; a link to
      another site opens the browser [SC-014, FR-033]
- [ ] ExHentai browse after sign-in; sign out clears the state
- [ ] Star a Hitomi search, reopen Hitomi: the search runs; a long press clears it
- [ ] Start page = Library, restart: the Library opens first
- [ ] Move the cache to the SD card while reading: no failed page; then back to internal

**Languages** (FR-039, SC-013)

- [ ] Switch through all six languages: applied at once on the same screen; at 360 dp (Settings →
      Display size largest) no clipped text in German and Japanese; Ukrainian plurals for 1/2/5 pages

**Distribution** (FR-037)

- [ ] Install a newer release APK over the old one: settings, history, sign-in, cache and library kept
