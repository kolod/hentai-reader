# Research: Windows Reader

Resolves the technical questions of [plan.md](plan.md) for [spec.md](spec.md). Facts about the
current code are from `main` at `208b466` (app 0.2.0, contract 1.1.1).

## Current state that constrains the design

- One `MainWindow` with a `NavigationView` and a `Frame`. `SourcePage` is cached
  (`NavigationCacheMode="Enabled"`); `GalleryPage` is not, so going back to it reloads.
- `GalleryPage` calls `getGallery` and shows thumbnails; there is no Read action.
- `ImageLoader` keeps `BitmapImage`s in a 150 MB in-memory LRU, fetching through
  `HttpHost.FetchImageAsync` (source cookies + politeness). Non-2xx statuses throw a generic error.
- `SourceSession` has a `Limited` state set by a `RateLimited` result of a script call, cleared by
  `ClearLimit()`. Image fetches do not set it.
- Source settings are never stored: every call passes `null`, so the manifest defaults apply.
- Cookies: per-source `CookieJar`, DPAPI file `sources/<id>/cookies.bin`; seeding from
  `k=v; …` strings and Netscape files exists (`Seed`, `SeedNetscape`).
- Manifest has `login: "none" | "optional" | "required"` but no login URL or cookie names.
- Data folder `%LOCALAPPDATA%\HentaiReader` (override `HENTAIREADER_DATA`); one instance per
  session after PR #14.

## R1. Reader inside the main window (FR-001–FR-003)

- **Decision**: a new `ReaderPage` navigated in the same `ContentFrame`: Source → Gallery → Reader.
  `GalleryPage` becomes cached like `SourcePage` and does not reload when navigated back to with the
  same gallery. Back: `NavigationView` back button, Escape, Alt+Left, mouse XButton1 (one handler on
  the window root). Selecting another source while reading navigates away and saves progress
  (`OnNavigatedFrom`).
- **Alternatives**: a second window (rejected by the user); an overlay above the frame (keeps
  browsing alive but duplicates back-stack logic).

## R2. Strip control and virtualization (FR-004–FR-008a, FR-004a)

- **Decision**: `ItemsRepeater` with a custom `VirtualizingLayout` (`StripLayout`) inside a
  `ScrollViewer`.
  - Geometry is pure arithmetic in `StripGeometry` (testable, no UI): column width `W` (min of
    window width and the reading-width setting, times zoom); page `i` height `W · h_i / w_i`;
    prefix sums give each page's top; lookup of the page at an offset by binary search. Every page
    is exactly `W` wide (FR-004a), whatever its resolution; pages touch, no spacing.
  - The layout realizes elements for the repeater's realization rect only; the **load and memory
    window** is computed separately by `LoadPlanner` (R4) because it depends on the preload setting,
    not on the repeater's cache length.
- **Alternatives**: `ListView`/`ItemsView` (spacing, selection visuals and container recycling
  fight a seamless strip; item heights must come from data anyway); a single `Canvas` with manual
  virtualization (more code, same result).

## R3. Size corrections without jumps (FR-005, FR-006, SC-003)

- **Decision**: estimated pages get `W · h/w` from the estimate (E-Hentai thumbnail aspect, else 2:3).
  When `getPageUrl` returns exact `width`/`height`, the page's height is updated **before** its
  image is decoded. The `ScrollViewer` keeps the viewport anchored (`VerticalAnchorRatio = 0`, the
  repeater registers realized elements as anchor candidates), so a height change above the anchor
  shifts the offset by the same amount. `StripLayout` additionally reports the summed height delta
  of pages above the viewport; a test asserts that the top visible page's screen position does not
  change by more than 1 px. **Fallback** if anchoring misbehaves with the custom layout: apply the
  delta with `ScrollViewer.ChangeView(…, disableAnimation: true)` in the same layout pass.
- The planner resolves `getPageUrl` for pages in the load window (≥ 1 screen ahead), so exact sizes
  normally arrive before the page is visible (spec option A).

## R4. Load and memory window (FR-007, FR-008, FR-008a, SC-002, SC-008, SC-011)

- **Decision**: `LoadPlanner` (pure, tested) computes, from scroll offset, viewport height `H` and
  page tops:
  - `visible` = pages intersecting `[y, y+H]` (any number, e.g. landscape pages on a portrait
    monitor);
  - `ahead` = the next pages until **both** N pages (setting 3–5, default 4) **and** `H` of height are
    covered;
  - `behind` = pages up to `max(1 page, H/2)` above.
  Priority: visible (top to bottom) → ahead → behind. Requests outside the window are cancelled;
  the per-source politeness limiter still caps concurrency.
- Decoded `BitmapImage`s live only for pages in the window; pages leaving it are released (their
  `Image.Source` cleared, references dropped). Decoded width = `W` × rasterization scale (× zoom).
- **Budget check**: page 1500×2121 px decoded (W = 1000 at 150 %) ≈ 12.7 MB; a landscape 16:9 page
  at the same width ≈ 3.4 MB. Worst realistic window: 7 visible landscape pages + 4 ahead + 2 behind
  ≈ 13 pages ≈ 45 MB; portrait pages with N = 5 + 1 screen + half screen ≈ 9 pages ≈ 115 MB.
  Far below SC-008 (1 GB). Very tall pages: decode is capped at 32 MP (width reduced), so one page
  never exceeds ≈ 128 MB.
- The existing `ImageLoader` LRU stays for covers and thumbnails; the reader has its own loader.

## R5. Disk cache (FR-023–FR-025, SC-010)

- **Decision**: `PageCache` in `%LOCALAPPDATA%\HentaiReader\cache\pages\`; file name =
  hex SHA-256 of `sourceId|galleryId|pageIndex|settingsFingerprint` (fingerprint = hash of the
  source's current setting values, so E-Hentai resampled and original never mix); content = the
  downloaded bytes as received (compressed). Index `index.json` (key → file, size, lastUsed),
  written atomically and at most every 10 s; rebuilt from the folder if missing or corrupt. LRU
  eviction when the total exceeds the limit (setting, default 1 GB, range 256 MB–10 GB). A cache hit
  skips `getPageUrl` and the network; only uncached pages go to the source. Clear deletes the folder
  content and index; progress, settings and cookies are elsewhere.
- **Alternatives**: SQLite for the index (dependency for a key → size map); caching decoded
  bitmaps (10× larger).

## R6. Reading progress (FR-018, FR-019, SC-005)

- **Decision**: `ProgressStore` file `%LOCALAPPDATA%\HentaiReader\progress.json`, key
  `sourceId|galleryId`, value `{ page, offset (0..1 within the page), pageCount, read, updatedAt,
  title }`; atomic writes; saved on leaving the reader, on window close, and debounced 5 s after
  scrolling stops. Offset is a fraction of the page height so it survives width and zoom changes.
  `read` = last page reached with its bottom in view.
- **Constitution V note**: metadata belongs in a local database; the library feature introduces it
  and imports this file. A JSON file now avoids a database dependency for one map (recorded in
  Complexity Tracking).

## R7. Failures, retries and limits (FR-020–FR-022, SC-006)

- **Decision**: per page state machine `Placeholder → Resolving (getPageUrl) → Downloading →
  Decoding → Loaded`, any step → `Failed(kind, message)`. Retry calls `getPageUrl(gallery, i,
  { attempt, previous, status })` with the previous `PageImage` (contract `RetryHint`). A
  `RateLimited` result of a script call already sets the session to `Limited`; **image fetches**
  with HTTP 429 or 509 now do the same (host change in `HttpHost.FetchImageAsync`, with
  `Retry-After` as wait time) — the rule "HTTP 429/509 = limit" is the constitution's, not site
  logic. While `Limited`, the planner sends nothing; a banner explains the limit and offers Retry
  (`ClearLimit()` then resume). Network failures leave loaded pages untouched.

## R8. Zoom, keyboard, scrubber, toolbar (FR-012–FR-017)

- **Decision**: zoom changes `W` (not a `ScrollViewer` zoom factor), so decode width follows and
  text stays sharp; range 50–300 %, steps of 10 %; Ctrl+wheel keeps the page point under the pointer
  by recomputing the offset from `StripGeometry`; Ctrl+0 → 100 %. When `W` exceeds the viewport,
  horizontal scrolling appears. Keys are `KeyboardAccelerator`s on the reader page (Space,
  Shift+Space, PgUp/PgDn, Home/End, arrows, Ctrl+G go to page). The scrubber is a vertical `Slider`
  bound to page index with a tooltip showing the target; release jumps (`StripGeometry.TopOf`).
  The toolbar auto-hides after 2 s without pointer movement near the top or bottom edge.

## R9. Borderless full screen (FR-016, clarification)

- **Decision**: `AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen)` — the window covers its
  monitor without title bar and taskbar but remains a normal top-level window (no exclusive mode;
  Alt+Tab, notifications and display changes work). Leaving restores `AppWindowPresenterKind.Default`
  plus the saved `OverlappedPresenter` state (maximized or the previous rect). F11 is a window-level
  accelerator (works on every page); Escape leaves full screen in the reader. In the reader, full
  screen also sets `NavigationView.IsPaneVisible = false`; leaving the reader restores the pane.
- **Alternatives**: manual borderless style via Win32 (`WS_POPUP`) — the presenter does this
  correctly per monitor and DPI.

## R10. Source settings and accounts (FR-026–FR-032)

- **Decision**: settings values stored per source in `preferences.json` (`sourceSettings: { id: {
  settingId: value } }`), passed to every call (search, gallery, page URL, account status).
  A "Sources" section in Settings shows one expander per source: its declared settings (combo boxes
  with the existing vocabulary labels), and for `capabilities.accountStatus` the account block:
  state, image limit (used / max, reset time), Sign in / Paste cookies / Sign out. Changing `site`
  to `exhentai` checks `getAccountStatus` first; without `exhentai: true` it is refused with the
  explanation (FR-031). The page cache fingerprint changes with settings (R5).
- **Login page — contract 1.2** (see [contracts/contract-1.2-login-page.md](contracts/contract-1.2-login-page.md)):
  the host must not know E-Hentai's login URL or cookie names (Constitution I/II), so the manifest
  gains an optional `loginPage { url, cookies, shareWith? }`. E-Hentai declares
  `forums.e-hentai.org` login, cookies `ipb_member_id` + `ipb_pass_hash`, shared with
  `exhentai.org`. Runner validates it (`url` host and `shareWith` within `domains`); Windows uses it.
- **Embedded login**: `WebView2` in a dialog with a throw-away user data folder under
  `%LOCALAPPDATA%\HentaiReader\webview\<random>` deleted afterwards (no browsing data persists).
  `NavigationStarting` cancels hosts outside `manifest.domains` and opens them in the system browser
  (FR-029). After each navigation the dialog reads cookies for `loginPage.url` from
  `CoreWebView2.CookieManager`; when all `loginPage.cookies` are present it copies every cookie whose
  domain matches `manifest.domains` into the source's jar, also seeds the `loginPage.cookies` for
  each `shareWith` domain, closes, and refreshes `getAccountStatus`. If the WebView2 runtime is
  missing, the button explains it and the paste option remains (spec assumption).
- **Paste**: accepts `name=value; …` or a Netscape `cookies.txt`; stored only for the login domain
  and `shareWith` domains (existing `Seed`/`SeedNetscape`), then verified with `getAccountStatus`.
  Values are never shown again or logged (FR-032). Sign out deletes `cookies.bin` and clears the jar.

## R11. Localization and accessibility (FR-034, SC-009)

- **Decision**: ~70 new keys (reader toolbar, page indicator with plurals, errors, settings,
  account, login dialog) added to all six `.resw` via the existing generator; key-parity test covers
  them. Reader controls get automation names; page slots are named "Page n of m" for UI tests.

## R12. Testing (Constitution VII)

- **Unit (no UI)** in `HentaiReader.Host.Tests`: `StripGeometry` (widths, prefix sums, lookup,
  corrections above/below the viewport, zoom anchoring), `LoadPlanner` (portrait monitor with 7
  landscape pages visible, N vs screen height, priorities, cancellation set), `PageCache` (LRU,
  limit, fingerprint, corrupt index recovery, clear), `ProgressStore` (round trip, atomic write),
  `LoginPage` cookie selection (domain filtering, `shareWith`, missing cookies), image fetch 429/509 →
  `Limited`.
- **Contract 1.2**: runner `check` rule + conformance sample `broken-login-page` (url outside domains);
  E-Hentai offline cases unchanged; Windows parity suite runs on 1.2.
- **App**: UI Automation scripts (as in 003/004) for single-window navigation, back-stack state,
  keyboard, full screen, and screenshots in de/ja; live reading of one Hitomi and one E-Hentai
  gallery for SC-001–SC-003, SC-010, SC-011. A portrait monitor is reproduced by geometry, not
  hardware: a reading width of ~500 px in a full-height window shows ≥ 3 landscape pages at once,
  which is the same planner situation as landscape pages on a rotated monitor.
- Pure logic lives in `HentaiReader.Host` (`Reading/`, `Storage/`) so it is testable without WinUI;
  the app keeps only views and glue.
