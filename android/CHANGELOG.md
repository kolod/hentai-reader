# Changelog — Android app

All notable changes to this component. Format: [Keep a Changelog](https://keepachangelog.com/), SemVer tags `android-vX.Y.Z`.

## [Unreleased]

## [0.1.0] - Unreleased

First Android release (feature 007). Requires contract 1.2 (host reports 1.2.1). Android 8.0+.

### Added

- Source host: QuickJS 2025-09-13 with an own JNI binding (per-call time limit, 128 MiB memory
  limit, cancel), jsoup for the HTML helpers with the contract's selector guards, OkHttp with the
  sources' politeness limits and per-source cookies; passes the contract suite, every source case
  and the conformance checks on the JVM (parity with Windows).
- Navigation drawer hidden by default (☰ or a left-edge swipe), full-screen on every screen with
  the system bars shown by a swipe; adult-content gate on first start.
- Browse and search per source with exactly the declared filters, endless results, tag search,
  gallery details with page thumbnails.
- Seamless touch reader: one continuous strip, pinch zoom, fit width, gaps, go to page, scrubber,
  continue where you left off; large pages decoded in tiles; keeps the screen on while reading.
- Save to library in the background (foreground service with a notification, pause / resume /
  cancel, continues after a restart, Wi-Fi only option); one `.cbz` per gallery in archive
  format 1, the same format the Windows app writes; offline reading.
- Library folders chosen with the system folder picker (internal storage or SD card), several
  folders with a target, move or keep on removal; archives copied in by other apps appear.
- History with covers, library search / filters / sorts, Downloads screen.
- Settings: language (six languages or the system's), start page, default search per source
  (star button, long press clears), reader, downloads, page cache limit and location (internal or
  SD card, moved in the background), per-source settings, E-Hentai sign-in in an in-app web view or
  with pasted cookies; cookies stored encrypted with an Android Keystore key, never backed up.
- Kind to the phone: decoded-page budget from the device's memory class, memory released on
  low-memory signals, idle source engines closed after 2 minutes, no work in the background except
  downloads, page cache paused when storage is nearly full, screens restored after the system
  stops the app.
- Release workflow for tags `android-v*`: signed APK with its SHA-256 on the GitHub Release.
