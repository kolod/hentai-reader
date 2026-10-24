---

description: "Task list for feature 005-windows-reader"
---

# Tasks: Windows Reader

**Input**: Design documents from `/specs/005-windows-reader/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md),
[data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: MANDATORY (Constitution VII): unit tests for the pure reader logic, cache, progress,
cookie selection and image limits; runner/conformance tests for contract 1.2; UI Automation checks
and live reading runs for the success criteria.

**Organization**: pure logic and shared app plumbing are Foundational; US1 (strip) and US2 (single
window) form the MVP; US3–US7 add progress, navigation/full screen, failures, settings/sign-in
(with contract 1.2) and reader settings.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: parallelisable (different files, no unfinished dependencies)
- **[Story]**: US1–US7 from spec.md
- Paths relative to the repository root; `W` = `windows/`, `H` = `W/src/HentaiReader.Host/`,
  `A` = `W/src/HentaiReader.App/`, `T` = `W/tests/HentaiReader.Host.Tests/`

---

## Phase 1: Setup

- [X] T001 Create folders `H/Reading/`, `A/Reader/`, `T/Reading/`, `T/Storage/`; add `HENTAIREADER_DATA`-aware path helpers for `progress.json` and `cache/pages/` to `A/Services/Preferences.cs` (`AppPaths.Progress`, `AppPaths.PageCache`)
- [X] T002 [P] Add the English keys for all new UI text (reader toolbar, page indicator `Reader_PageOf` with plurals where counts appear, go-to-page dialog, slot errors, limit banner, full screen, Continue / Read from start / progress `Gallery_Progress`, read marker, reader settings, cache size/clear, sources section, account states, image limit, sign in / paste / sign out, login dialog, ExHentai refusal) to the scratchpad `en-US.json` and regenerate `A/Strings/en-US/Resources.resw` (keys named per [contracts/reader-ui.md](contracts/reader-ui.md))

---

## Phase 2: Foundational (block all stories)

- [X] T003 [P] Implement `H/Reading/StripGeometry.cs` per [data-model](data-model.md#strip-geometry-pure): page sizes, column width `W = min(viewport, readingWidth) × zoom`, heights `W·h/w`, prefix-sum tops, `PageAt(y)` (binary search), `ScrollFor(page, offset)`, `PositionAt(y)` → (page, offset 0–1), `SetPageSize(i, w, h)` returning the height delta and whether the page lies above a given offset
- [X] T004 [P] Unit tests `T/Reading/StripGeometryTests.cs`: equal width for pages of any resolution/proportion (FR-004a), tops/total, lookup at boundaries, position round trip across width and zoom changes, delta of a correction above vs below the viewport, 2:3 default for unknown sizes
- [X] T005 [P] Implement `H/Reading/LoadPlanner.cs` per [data-model](data-model.md#load-window-pure): visible / ahead (≥ N pages **and** ≥ H) / behind (≥ 1 page **and** ≥ H/2), ordered list and the set to keep decoded
- [X] T006 [P] Unit tests `T/Reading/LoadPlannerTests.cs`: portrait case with 7 landscape pages visible (all visible first, ahead still ≥ 1 screen), tall pages where N pages exceed H, start/end of gallery, N = 3/4/5, jump target ordering
- [X] T007 [P] Implement `H/Storage/PageCache.cs` per [data-model](data-model.md#page-cache-cachepages): key/fingerprint hashing, `TryGet`, `PutAsync` (atomic file write), LRU eviction to a limit, debounced `index.json` flush, index rebuild on missing/corrupt index, `Clear`, `SizeBytes`; thread-safe
- [X] T008 [P] Unit tests `T/Storage/PageCacheTests.cs`: hit/miss, fingerprint separates settings, eviction order and limit, rebuild from folder, clear keeps unrelated files, concurrent puts
- [X] T009 [P] Implement `A/Services/SourceSettingsStore.cs` and extend `A/Services/Preferences.cs` with `PreloadPages` (4), `ReadingWidth` (1000), `CacheLimitMb` (1024), `SourceSettings` (drop ids/values the manifest does not declare on load); pass stored settings to every `CallAsync` (`A/ViewModels/SearchViewModel.cs`, `A/Pages/GalleryPage.xaml.cs`)
- [X] T010 Image limits in the host: `H/Http/HttpHost.cs` `FetchImageAsync` maps HTTP 429/509 to a typed rate-limit exception with `Retry-After`; `H/Sources/SourceSession.cs` gains `FetchImageAsync(request)` that sets `Limited` on it and refuses while `Limited`; unit tests `T/Http/ImageLimitTests.cs` (429 and 509 → Limited, Retry-After parsed, refusal while Limited, `ClearLimit` resumes)
- [X] T011 Window-level input plumbing in `A/MainWindow.xaml(.cs)`: one handler for Back (Alt+Left, mouse XButton1, Back button) dispatching to the current page; a hook for pages to claim Escape; `NavigationView.IsPaneVisible` control used by full screen

**Checkpoint**: `dotnet test` green with the new unit tests; app unchanged for the user except settings now reaching scripts.

---

## Phase 3: User Story 1 — Read a gallery in one seamless strip (P1) 🎯 MVP

**Goal**: Read opens a seamless strip in the main window; slots reserved, corrections invisible, pages ready ahead.

**Independent test**: [quickstart.md §3](quickstart.md) steps 1–4 on one Hitomi and one E-Hentai gallery.

- [X] T012 [US1] `A/Reader/StripLayout.cs`: `VirtualizingLayout` over `StripGeometry` (measure/arrange realized items at their tops, exact width, no spacing; extent = total height; realization from the repeater's realization rect)
- [X] T013 [US1] `A/Reader/PageSlot.cs`: control with placeholder background of the expected size, `Image` (stretch to width), progress ring after 300 ms, error text + Retry button; automation name `Page {n} of {total}`
- [X] T014 [US1] `A/Reader/ReaderSession.cs`: page states per [data-model](data-model.md#reading-session-memory-only); planner loop on scroll/resize/zoom (cancel requests that left the window); for each page: cache hit → decode; else `getPageUrl` (stored settings) → apply exact size via `StripGeometry.SetPageSize` before decoding → `SourceSession.FetchImageAsync` → `PageCache.PutAsync` → decode at `W × scale` (cap 32 MP); release bitmaps outside the keep set; all off the UI thread except bitmap creation
- [X] T015 [US1] `A/Pages/ReaderPage.xaml(.cs)`: `ScrollViewer` (vertical anchoring, `VerticalAnchorRatio = 0`) with `ItemsRepeater` + `StripLayout` + `PageSlot` template, centred column with maximum width from preferences, scroll/size events to the session, keys Space/Shift+Space/PgUp/PgDn/Home/End/Up/Down per [contracts/reader-ui.md](contracts/reader-ui.md)
- [X] T016 [US1] Anchoring check: if a correction above the viewport moves the top visible page by more than 1 px (log in Debug), apply the fallback from research R3 (`ChangeView` with the geometry delta in the same pass) in `A/Pages/ReaderPage.xaml.cs`
- [X] T017 [US1] `A/Pages/GalleryPage.xaml(.cs)`: Read button navigating the `ContentFrame` to `ReaderPage` with source, gallery JSON and settings (no new window)
- [ ] T018 [US1] Live check per [quickstart.md §3](quickstart.md): Hitomi and E-Hentai galleries (100+ pages), mixed-resolution gallery for FR-004a, record first-page time (SC-001), placeholders seen while scrolling (SC-002), jumps (SC-003); fix and repeat

**Checkpoint**: MVP reading works end to end in the main window.

---

## Phase 4: User Story 2 — Back to browsing exactly where it was (P1)

**Goal**: Reader → gallery → search restore without reloads.

**Independent test**: [quickstart.md §2](quickstart.md).

- [X] T019 [US2] `A/Pages/GalleryPage.xaml`: `NavigationCacheMode="Enabled"`; `GalleryPage.xaml.cs` skips reloading when navigated back with the same source+gallery
- [X] T020 [US2] `A/Pages/ReaderPage.xaml.cs`: Back/Escape/Alt+Left/mouse back via T011 return to the gallery; leaving (also by selecting another source) cancels loads, saves position (hook for US3) and releases all bitmaps
- [X] T021 [US2] UI Automation script check (scratchpad `uia.ps1` extension): search with filter, scroll 100+ results, open gallery, Read, back twice → same query, filter, result count and scroll offset; no new search request (status unchanged); measure < 0.5 s (SC-004)

---

## Phase 5: User Story 3 — Resume where I stopped (P2)

**Goal**: Progress saved and offered as Continue.

**Independent test**: [quickstart.md §5](quickstart.md).

- [X] T022 [P] [US3] Implement `H/Storage/ProgressStore.cs` (load, get/set per `sourceId|galleryId`, atomic save, read flag stays true) and tests `T/Storage/ProgressStoreTests.cs`
- [X] T023 [US3] `A/Reader/ReaderSession.cs` + `A/Pages/ReaderPage.xaml.cs`: save position (page + offset fraction) on leave, on window close (`MainWindow.Closed`) and 5 s after scrolling stops; mark read when the last page's bottom is in view; open at a given position (Continue)
- [X] T024 [US3] `A/Pages/GalleryPage.xaml(.cs)`: show progress "{page} / {total}", Continue + Read from start when progress exists, read marker; strings from T002
- [X] T025 [US3] Live check: read to page 40, restart app, Continue → within one screen (SC-005)

---

## Phase 6: User Story 4 — Navigate a long gallery, zoom, full screen (P2)

**Goal**: Page indicator, scrubber, go to page, zoom, auto-hiding toolbar, borderless full screen everywhere.

**Independent test**: [quickstart.md §3 step 4 and §7](quickstart.md).

- [X] T026 [US4] Reader toolbar in `A/Pages/ReaderPage.xaml(.cs)`: back, title, page indicator, go to page (Ctrl+G dialog with validation), zoom out/value/in, full screen; auto-hide after 2 s, reappear near top/bottom edge or on key press (FR-017)
- [X] T027 [US4] Vertical scrubber (Slider over page index) with target tooltip while dragging and jump on release; planner prioritises pages near the target (`LoadPlanner` jump ordering from T005)
- [X] T028 [US4] Zoom 50–300 % (Ctrl+wheel around the pointer, Ctrl+Plus/Minus/0) changing `W` in `StripGeometry`, keeping the point under the pointer; re-decode visible pages at the new width; horizontal scrolling when wider than the window
- [X] T029 [US4] `A/Services/FullScreen.cs` + `A/MainWindow.xaml.cs`: F11 accelerator on the window root toggling `AppWindowPresenterKind.FullScreen` ↔ Default, saving/restoring the overlapped state (maximized or rect); reader hides the navigation pane in full screen and restores it on leave; Escape in the reader leaves full screen first (FR-003, FR-016)
- [X] T030 [US4] UI Automation + screenshot check: F11 on search, gallery, settings, reader; Alt+Tab still switches windows; leaving restores the previous size/position and maximized state

---

## Phase 7: User Story 5 — Failures and limits while reading (P2)

**Goal**: In-place retry; limits stop everything with an explanation.

**Independent test**: [quickstart.md §6](quickstart.md).

- [ ] T031 [US5] `A/Reader/ReaderSession.cs`: `Failed(kind, message)` per page; Retry calls `getPageUrl` with `RetryHint { attempt, previous, status }`; network failures keep loaded pages
- [ ] T032 [US5] Limit banner in `A/Pages/ReaderPage.xaml(.cs)`: on `RateLimited` (script or image) the planner stops, banner shows reason and waiting time (plural minutes), Retry → `ClearLimit()` and resume; account status refreshed for sources with `accountStatus`
- [ ] T033 [US5] Check: disconnect/reconnect while reading (manual); forced limit via a Debug-only environment switch `HENTAIREADER_FAULT=image509` in `H/Http/HttpHost.cs` (compiled only in Debug) → banner shown, no further requests in the request log until Retry (SC-006)

---

## Phase 8: User Story 6 — Source settings and E-Hentai sign-in (P2)

**Goal**: Declared settings editable; sign in by embedded page or pasted cookies; account state and image limit shown; contract 1.2.

**Independent test**: [quickstart.md §8](quickstart.md) steps 1–4.

- [ ] T034 [US6] Confirm live the E-Hentai login URL and the cookies set after sign-in (names only, never values) and record them in [contracts/contract-1.2-login-page.md](contracts/contract-1.2-login-page.md)
- [ ] T035 [US6] Contract 1.2 in `contract/host-api.d.ts` (`LoginPage`, `SourceManifest.loginPage` with doc comments and host rules), `contract/package.json` 1.2.0, `contract/CHANGELOG.md`, `contract/README.md` calling-convention section
- [ ] T036 [P] [US6] Runner: host supports 1.2 (`tools/runner/src/source.ts`), `check` rules from the contract table (`tools/runner/src/commands/check.ts`), conformance sample `contract/conformance/broken-login-page/` with its expected violation, runner tests; `npm test` green
- [ ] T037 [P] [US6] Windows host: `H/HostInfo.cs` 1.2.0; `H/Sources/ManifestRules.cs` with the same rules applied on load (source unavailable with the violation text); tests `T/Sources/ManifestRulesTests.cs`; parity suite green
- [ ] T038 [US6] `sources/ehentai/ehentai.js`: `contract: "1.2"`, `loginPage` from T034, version 0.2.0; `sources/ehentai/CHANGELOG.md`; `npm run runner -- check sources/ehentai` and offline tests green
- [ ] T039 [P] [US6] `H/Reading/LoginCookies.cs`: from captured cookies keep those whose domain matches `manifest.domains`, add `loginPage.cookies` for each `shareWith` domain, report missing required cookies; parsing of pasted `k=v; …` and Netscape text; tests `T/Reading/LoginCookiesTests.cs`
- [ ] T040 [US6] `A/Dialogs/LoginDialog.cs`: `WebView2` with a throw-away user data folder under the data folder (deleted after close), `NavigationStarting` limited to `manifest.domains` (others → system browser), cookie check after each navigation, completion when all `loginPage.cookies` exist, store via `LoginCookies` into the source's jar; explanation when the WebView2 runtime is missing
- [ ] T041 [US6] `A/Dialogs/PasteCookiesDialog.cs`: password-style input, store via `LoginCookies`, verify with `getAccountStatus`, never show values again
- [ ] T042 [US6] `A/Pages/SettingsPage.xaml(.cs)`: Sources section, one expander per source with its declared settings (vocabulary labels, saved via `SourceSettingsStore`), account block for `accountStatus` sources (state, image limit used/max and reset time, Sign in when `loginPage` exists, Paste cookies, Sign out deleting `cookies.bin` and clearing the jar); choosing `site = exhentai` checks `getAccountStatus` first and refuses without `exhentai: true` (FR-031)
- [ ] T043 [US6] Live check: sign in through the embedded page, image limit shown within 5 s (SC-007), ExHentai allowed only with access, sign out, paste cookies; confirm cookies never appear in logs or files outside `cookies.bin`

---

## Phase 9: User Story 7 — Reader settings (P3)

**Goal**: Preload depth, reading width, cache limit, clear cache.

**Independent test**: [quickstart.md §8](quickstart.md) step 5.

- [ ] T044 [US7] `A/Pages/SettingsPage.xaml(.cs)`: Reader section with preload (3–5), maximum width (600–2000, step 100), cache limit (256 MB–10 GB) with current size (localized number + unit) and Clear cache; changes apply immediately to an open reader (planner N, column width, cache limit)
- [ ] T045 [US7] Check: change each setting with a gallery open; Clear cache → size 0, progress/settings/sign-in kept

---

## Phase 10: Polish & Cross-Cutting Concerns

- [ ] T046 [P] Translate all keys from T002 into `de-DE`, `fr-FR`, `es-ES`, `ja-JP`, `uk-UA` (plural families per CLDR) and regenerate the `.resw` files; `ResourceParityTests` green
- [ ] T047 Many-pages check per [quickstart.md §4](quickstart.md) (reading width 600, zoom 50 %, landscape CG set): ≥ 3 pages visible, 95 % ready before visible, memory < 1 GB on a 500-page gallery (SC-008, SC-011); scroll back → no placeholders, no requests (SC-010)
- [ ] T048 Layout pass in de-DE and ja-JP at 1024 px for reader toolbar, settings, dialogs, banners (SC-009); fix clipping
- [ ] T049 [P] Docs: `W/README.md` (reader, keys, full screen, sign-in, cache location), `W/CHANGELOG.md` 0.3.0, `specs/product-overview.md` roadmap (005 ✅ when merged); `<Version>` 0.3.0 in `W/Directory.Build.props`
- [ ] T050 Final gate: `npm test`, `npx tsc -p tools/runner/tsconfig.json`, `dotnet build windows/HentaiReader.slnx -c Release -p:Platform=x64`, `dotnet test --project windows/tests/HentaiReader.Host.Tests -c Release`, `pwsh windows/build/Build-Installer.ps1 -SmokeTest` — all green before the PR

---

## Dependencies & Execution Order

```text
Setup (T001–T002) → Foundational (T003–T011)
  → US1 strip (T012–T018) → US2 back stack (T019–T021)      ← MVP
  → US3 progress (T022–T025)
  → US4 navigation/full screen (T026–T030)
  → US5 failures/limits (T031–T033)
  → US6 settings/sign-in + contract 1.2 (T034–T043)
  → US7 reader settings (T044–T045)
  → Polish (T046–T050)
```

- US1 needs T003, T005, T007, T009, T010; US2 needs US1's reader page; US3–US5 extend the reader;
  US6 is independent of US3–US5 (only T009) and can run in parallel with them; US7 needs US1 and
  T007.
- Contract 1.2 (T035–T038) must land together with the Windows host change (T037) in the same PR
  (Constitution II, X).

## Parallel Opportunities

```text
Foundational: T003 | T005 | T007 | T009 (then their tests T004 | T006 | T008)
US3:          T022 alongside US4 work
US6:          T036 | T037 | T039 after T035
Polish:       T046 | T049
```

## Implementation Strategy

1. **Pure logic first** (Phase 2): geometry, planner, cache and image limits with unit tests — the
   risky parts (seams, jumps, memory) are decided there.
2. **MVP** = US1 + US2: read any gallery in the main window and come back to the same search.
3. Then progress (US3), navigation and full screen (US4), failure handling (US5).
4. Settings and sign-in with contract 1.2 (US6) as one coherent change, then reader settings (US7).
5. PR `005-windows-reader` → `main` with CI green; tags `contract-v1.2.0`,
   `sources-ehentai-v0.2.0`, `windows-v0.3.0` after merge.
