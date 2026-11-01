# Feature Specification: Android App at Parity with Windows

**Feature Branch**: `007-android-app`

**Created**: 2026-11-01

**Status**: Draft

**Input**: User description: "android застосунок аналогічний до windows застосунку окрім: ліва
панель ховається; тільки повноекранний режим; врахувати обмежені ресурси смартфону" (an Android
app equivalent to the Windows app, except: the left pane hides; full screen only; account for the
limited resources of a smartphone). Roadmap item 7 of [product-overview.md](../product-overview.md).
Parity baseline: the Windows app as specified in
[003-windows-host](../003-windows-host/spec.md), [005-windows-reader](../005-windows-reader/spec.md)
and [006-windows-library](../006-windows-library/spec.md), including their amendments, as shipped
in Windows 0.4.0.

## Context

The second app of the pair (Constitution I). It does everything the Windows app does — browse and
search the bundled sources, gallery details, the seamless reader, progress, source settings and
E-Hentai sign-in, history, library, downloads, start page, default queries, six languages — with
the same source scripts through the same contract. It differs from Windows in three ways the
owner asked for, plus the adaptations a phone needs anyway:

1. **The navigation pane is hidden** and slides in on demand, instead of being always visible.
2. **Full screen only**: the app always uses the whole screen, with the status and navigation bars
   hidden; there is no windowed mode and nothing to toggle.
3. **Limited resources**: memory, storage, battery and mobile data are budgeted, and the app copes
   with being stopped by the system at any time.

Touch replaces mouse and keyboard; system Back replaces Escape and Alt+Left.

Actors:

- **Reader** — the person using the app on an Android phone or tablet (adult, personal use).
- **Host implementer / source author** — needs proof that scripts behave on Android exactly as in
  the runner and on Windows.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Same script behaviour as the runner and Windows (Priority: P1)

The Android source host runs the portable contract test suite and every recorded test case of every
bundled source, offline, and produces exactly the results stored as snapshots — the same ones the
runner and the Windows host produce. This runs automatically in continuous integration.

**Why this priority**: Everything the app shows comes from scripts; this is the first time they run
on the third engine, and Constitution II/VII require identical behaviour on both platforms.

**Independent Test**: Run the Android host's automated parity check: all contract tests pass and
every source case equals its snapshot.

**Acceptance Scenarios**:

1. **Given** the contract test suite, **When** the Android host runs it, **Then** every test passes,
   including politeness timing, cookie scoping, storage isolation and settings passing.
2. **Given** the recorded test cases of Hitomi, E-Hentai and the reference samples, **When** the
   Android host replays them offline, **Then** every result equals the stored snapshot and every
   expected error kind matches.
3. **Given** a script that uses a capability outside the contract, **When** the Android host runs it,
   **Then** the call fails and names the capability, as in the runner.

---

### User Story 2 - Browse, search and open a gallery on the phone (Priority: P1)

The reader starts the app, confirms the adult-content notice once, and sees the front feed of the
start source on the whole screen. A menu button (or a swipe from the left edge) slides in the
navigation pane with the sources, History, Library, Downloads and Settings; choosing an item closes
the pane. They search in the source's syntax, set the source's filters, scroll results endlessly,
open a gallery, see its details and page thumbnails, and tap a tag to search for it.

**Why this priority**: The first user-visible value on Android; everything else starts from here.

**Independent Test**: On a phone, choose Hitomi from the pane, search `female:glasses` with
language English, scroll three pages of results, open a gallery, tap a tag.

**Acceptance Scenarios**:

1. **Given** any screen except the reader, **When** the reader taps the menu button or swipes from
   the left edge, **Then** the navigation pane slides in over the content; tapping outside it, Back,
   or choosing an item closes it.
2. **Given** the app, **When** any screen is shown, **Then** it covers the whole screen without the
   status and navigation bars; a swipe from a screen edge shows them briefly, and they hide again by
   themselves.
3. **Given** a source with filters, **When** the reader opens the filters, **Then** exactly the
   declared filters are offered with translated labels; several languages may be chosen at once.
4. **Given** a result list, **When** the reader scrolls near its end, **Then** the next page is
   appended without the list jumping; the end of results is shown when there is no more.
5. **Given** a gallery, **When** it is opened, **Then** its metadata, tags grouped by namespace and
   one thumbnail per page (sprite cells cropped correctly) are shown; tapping a thumbnail opens the
   reader at that page.
6. **Given** a rate limit, missing sign-in, site change or no network, **When** a request fails,
   **Then** a translated, specific message is shown; after a rate limit nothing more is sent to that
   source until the reader retries.

---

### User Story 3 - Read in one seamless strip with touch (Priority: P1)

The reader chooses Read (or Continue). The reader screen takes the whole screen — no navigation
pane, no system bars, no toolbar until asked for. All pages form one vertical strip at the screen
width, slots reserved before images arrive, so the strip never jumps. Flinging scrolls smoothly;
pinch zooms around the fingers; a tap shows or hides the toolbar (page indicator, scrubber, go to
page, fit width, gaps, save to library, back). Rotating the phone keeps the place.

**Why this priority**: Reading is the purpose of the app and the first-priority UX
(Constitution IV); on a phone it must stay smooth within a much smaller memory budget.

**Independent Test**: On a mid-range phone, read a 200-page E-Hentai gallery and a long webtoon from
start to end by flinging, zoom in and out, rotate twice, jump with the scrubber; no visible jump,
no blank page in view at normal speed, no crash.

**Acceptance Scenarios**:

1. **Given** a gallery's details, **When** the reader chooses Read, **Then** the reader screen shows
   the first page within 2 seconds on a typical connection, covering the whole screen.
2. **Given** pages still loading, **When** images arrive or an estimated size is corrected, **Then**
   the content in view does not move (Constitution IV), as on Windows (005 FR-005, FR-006).
3. **Given** the reader, **When** the reader taps the strip, **Then** the toolbar appears or hides;
   it also hides by itself after a few seconds without interaction.
4. **Given** the reader, **When** the reader pinches, **Then** the strip zooms around the fingers;
   zoomed wider than the screen, the strip can be panned sideways; the fit-width button returns to
   the screen width.
5. **Given** the reader, **When** the phone is rotated, **Then** the same page and offset stay in
   view and the preload and memory areas adapt to the new screen height.
6. **Given** a page that fails, stalls for 20 s or hits a rate limit, **When** it happens, **Then**
   the behaviour is that of Windows (005 FR-020 – FR-022): Retry in place, automatic retry hint up to
   twice, rate limit stops the source with an explanation.
7. **Given** the reader, **When** the reader presses Back, **Then** the gallery's details are shown,
   and Back again returns to the search with the same results, filters and scroll position, without
   new requests.

---

### User Story 4 - Save galleries and read offline, downloads in the background (Priority: P1)

The reader chooses Save to library. The download runs in the background with a progress
notification, also while the screen is off or another app is in front; the notification can pause
or resume it. Finished galleries appear in the Library and open in the same reader without a
network. Archives are the same as on Windows, so galleries saved on one device open on the other.

**Why this priority**: Offline reading is what a phone needs most (travel, no signal, saving data);
cross-device archives are a constitution requirement (V).

**Independent Test**: Save a 40-page gallery, turn the screen off until done, switch on airplane
mode, open it from the Library and read to the end. Copy a gallery saved by the Windows app into a
library folder and read it; copy one saved on Android to Windows and read it there.

**Acceptance Scenarios**:

1. **Given** a gallery, **When** the reader chooses Save to library, **Then** the download is queued,
   and while it runs a notification shows the gallery and progress ("12 / 40") with Pause.
2. **Given** a running download, **When** the app goes to the background or the screen turns off,
   **Then** the download continues until done, paused, or stopped by a limit.
3. **Given** the system stops the app during a download, **When** the app is next started (or the
   system restarts the download), **Then** it continues with the next missing page.
4. **Given** "Downloads on Wi-Fi only" is on, **When** the phone is on mobile data, **Then** downloads
   wait with that reason shown and continue by themselves on Wi-Fi.
5. **Given** an archive saved by the Windows app, **When** it is in a library folder, **Then** it is
   listed with its metadata, progress and reader settings and reads like any other.
6. **Given** a saved gallery, **When** the reader deletes it and confirms, **Then** its archive is
   removed from the device and from the Library; its history entry stays.

---

### User Story 5 - History, library management and continuing where I stopped (Priority: P2)

As on Windows: History lists opened galleries newest first and continues at the saved page with the
gallery's own zoom, width mode and gaps; the Library searches, filters and sorts saved galleries
from all library folders; progress is offered as Continue on the gallery's details.

**Why this priority**: Used every day, but reading and saving already work without it.

**Independent Test**: Read three galleries partly, force-stop the app, open History, open the second
entry → it resumes at the saved page and zoom. Search, filter and sort a Library of 20 galleries.

**Acceptance Scenarios**:

1. **Given** galleries read partly, **When** History is opened, **Then** they are listed newest first
   with progress and last-read time, and an entry opens at its saved position (the saved copy when
   there is one).
2. **Given** the Library, **When** the reader types part of a title or sets source, read-state
   filters and one of the three sort orders, **Then** the list follows them as on Windows (006
   FR-011).
3. **Given** archives copied into a library folder by another app or a computer, **When** the
   Library is opened or pulled to refresh, **Then** they are listed; foreign comic archives by file
   name; unreadable files as unreadable.
4. **Given** a removable card holding a library folder, **When** the card is removed, **Then** its
   galleries show as unavailable and come back when it is inserted again.

---

### User Story 6 - Settings, sign-in and comfort features (Priority: P2)

Settings has the same sections as on Windows: interface language, start page, default queries,
reader (preload, fit width, reading width, gap, cache size, Clear cache), library folders, cache
location, per-source settings and E-Hentai account (sign in on the site's login page inside the app
or paste cookies, account status and image limit, sign out). The star next to the search box saves
the current search as the source's default; a long press on it clears the default.

**Why this priority**: Defaults work for most users; sign-in matters for long E-Hentai sessions.

**Independent Test**: Sign in to E-Hentai inside the app, see the image limit, switch to ExHentai and
browse; set the start page to Library and restart; star a Hitomi search and reopen Hitomi.

**Acceptance Scenarios**:

1. **Given** E-Hentai settings, **When** the reader signs in on the in-app login page, **Then** the
   cookies are kept for E-Hentai's domains only and the state shows signed in with the image limit.
2. **Given** a language chosen in Settings, **When** it is applied, **Then** the whole app is in that
   language and returns to the same screen; the choice survives restarts.
3. **Given** a star pressed on a search, **When** that source is opened later, **Then** the default
   search runs; a long press on the star clears it.
4. **Given** Settings → Library, **When** the reader adds a folder with the system folder picker,
   **Then** galleries already in it are listed and the folder can be made the target for new saves.

---

### User Story 7 - Kind to the phone's resources (Priority: P2)

The app keeps within the phone's memory, storage, battery and data: it never runs work in the
background other than downloads, keeps the image cache within its limit and away from a nearly
full disk, frees memory when the system asks, and survives being stopped and restored by the system
without losing the reader's place.

**Why this priority**: The owner asked for it explicitly; without it the app is killed, drains the
battery or fills the phone, and every other story suffers.

**Independent Test**: On a phone with 3 GB of memory, read a 500-page gallery, switch to a camera app
and back, let the system reclaim the app, return → it reopens at the same page; check memory and
background traffic during the session.

**Acceptance Scenarios**:

1. **Given** the app in the background without running downloads, **When** time passes, **Then** it
   sends no requests and does no work.
2. **Given** the system signals low memory, **When** the reader is open, **Then** everything except
   the pages in view is released and is restored from the disk cache when needed, without a crash.
3. **Given** the system has stopped the app while it was in the background, **When** the reader
   returns to it, **Then** the same screen comes back — the reader at the same page and offset, a
   search with the same query and filters.
4. **Given** free storage falls below the reserve, **When** pages would be cached, **Then** caching
   pauses (reading continues online) and a one-time notice says why.

---

### Edge Cases

- **Back with the navigation pane open** → Back closes the pane first.
- **Back on a section's first page** → the app closes, as Back closes any Android app; a few presses
  always quit (same rule as Windows 005 FR-003).
- **Left-edge swipe vs. system back gesture** → where the system uses the left edge for Back, the
  pane opens by the menu button and the swipe must not steal the system gesture.
- **Display cutout (notch) and rounded corners** → the strip may extend under the cutout, but
  toolbar buttons, the pane, text and controls are never placed under it.
- **Very tall images (webtoons) and spreads** → decoded in parts or at a capped resolution so one
  page never exhausts memory; shown at the strip width with their own proportions.
- **Animated images** → only pages in or near view animate.
- **Tablet or foldable** → layouts reflow to the screen width; on wide screens the reading-width
  setting centres the strip as on Windows; unfolding keeps the reading position.
- **Phone call, notification shade or another app on top** → reading position kept; nothing is
  reloaded unnecessarily.
- **Network switches between Wi-Fi and mobile data** → requests in flight that fail are retried by
  the normal rules; "Wi-Fi only" downloads wait.
- **Library folder permission revoked or folder deleted by another app** → its galleries show as
  unavailable with an action to grant access again or remove the folder.
- **Archive file names** → shortened and stripped of characters not allowed on common Android
  storage (including removable cards), so the same archive can be copied to Windows unchanged.
- **Download stopped by the system for running too long in the background** → it resumes on the
  next start or when the system allows, never restarts from zero.
- **Notifications not permitted** → downloads still run while the app is open; the Downloads screen
  explains that background progress needs the notification permission.
- **Cookies** → stored only on the device, protected by the device's credential storage, never
  backed up to cloud backups, never logged.
- **App updated by installing a newer package** → settings, history, sign-ins, cache and library are
  kept.
- **Battery saver on** → reading keeps working; downloads follow the system's restrictions and
  continue when allowed.

## Requirements *(mandatory)*

### Functional Requirements

**Parity with the Windows app**

- **FR-001**: The Android app MUST offer every user-facing capability of the Windows app as
  specified in 003, 005 and 006 (with their amendments) and shipped in Windows 0.4.0, except where
  this specification states an Android difference. Mouse, keyboard and window behaviours are
  replaced as listed in FR-030 – FR-034.
- **FR-002**: Settings keys, defaults and string keys MUST be the same as on Windows where the
  setting exists on both platforms (Constitution IX).

**Source host**

- **FR-003**: The app MUST host source scripts with every capability of the current contract (1.2)
  and nothing else, with the same calling convention, limits, cookie scoping and politeness as the
  Windows host (003 FR-001 – FR-005).
- **FR-004**: An automated parity check MUST run the contract test suite and every source test case
  against the Android host offline, with the same fixtures, frozen clock and comparison rules, and
  fail on any difference; it MUST run in continuous integration on every change to the Android app,
  the contract or a source.
- **FR-005**: The app MUST ship the current scripts of every source as its built-in copy
  (Constitution III); script updates are feature 008.
- **FR-006**: Script execution, networking, image decoding and storage access MUST NOT run on the UI
  thread (Constitution IV).

**Navigation pane (Android difference)**

- **FR-007**: The navigation pane (sources, History, Library, Downloads, Settings, with the downloads
  count) MUST be hidden by default on every screen and on every screen size; it opens over the
  content by a menu button in the screen's top bar and by a swipe from the left edge where that does
  not conflict with the system back gesture.
- **FR-008**: The pane MUST close when an item is chosen, on a tap outside it, on Back, and by a
  swipe; it is never shown in the reader.

**Full screen only (Android difference)**

- **FR-009**: Every screen MUST use the whole screen with the system status and navigation bars
  hidden; a swipe from a screen edge shows them temporarily, and they hide again by themselves.
  There is no windowed mode and no full-screen toggle.
- **FR-010**: Controls and text MUST stay clear of display cutouts and of the temporarily shown
  system bars; reader page content MAY use the whole screen including the cutout area.

**Reader on touch**

- **FR-011**: The reader MUST keep the strip rules of Windows: no seams, optional gap (setting,
  default 8, toggle on the toolbar, webtoons open without), every page at the strip width, slots
  reserved before images load, corrections without moving the content in view, decoding at the
  displayed width (005 FR-004 – FR-010, Constitution IV).
- **FR-012**: Scrolling MUST follow the finger with platform-standard fling and overscroll; pinch
  MUST zoom around the fingers, relative to the starting width; a strip wider than the screen MUST
  be pannable sideways; a fit-width toolbar button returns to the screen width (lit only at exactly
  that width).
- **FR-013**: A tap on the strip MUST show or hide the toolbar (page indicator "57 / 312", scrubber
  with the target page shown while dragging, go to page, fit width, gaps, save to library, back); the
  toolbar hides by itself after a few seconds without interaction.
- **FR-014**: The screen MUST stay on while the reader is open and in use (setting "Keep screen on
  while reading", on by default), and may turn off normally on every other screen.
- **FR-015**: Rotation, folding/unfolding and size changes MUST keep the page and offset in view.

**Limited resources (Android difference)**

- **FR-016**: Decoded pages kept in memory MUST follow the Windows rule (everything visible plus the
  preload area ahead and the area behind, 005 FR-008a) and in addition stay within a memory budget
  derived from what the device allows the app; when the budget is reached, pages farthest from the
  view are released first, and the preload area shrinks before visible pages are affected.
- **FR-017**: On a low-memory signal from the system the app MUST release everything it can rebuild
  (decoded pages outside the view, thumbnails and covers off screen, idle source engines) and
  continue without a visible interruption.
- **FR-018**: Very tall images MUST be decoded in parts or at a capped resolution so no single page
  can exceed a fixed share of the memory budget; thumbnails and covers MUST be decoded at their
  display size.
- **FR-019**: The preload depth setting MUST be 3–5 pages as on Windows, with default 3 on Android,
  plus at least one screen height ahead (005 FR-008).
- **FR-020**: The page cache MUST default to 512 MB (setting, as on Windows), MUST keep at least
  1 GB or 10 % of the storage free (whichever is smaller) by pausing cache writes, and MUST evict
  least-recently-used pages.
- **FR-021**: Without running downloads the app MUST NOT do any work or send any request while it is
  in the background.
- **FR-022**: Downloads MUST run in the background with a visible progress notification offering
  Pause/Resume and opening the Downloads screen on tap; they MUST continue with the screen off.
- **FR-023**: Settings MUST offer "Downloads on Wi-Fi only" (off by default); while it is on and the
  connection is metered, downloads wait with that reason and resume by themselves on Wi-Fi.
- **FR-024**: "Load covers from the network" for history entries MUST stay off by default, as on
  Windows.
- **FR-025**: When the system stops the app in the background, the app MUST restore the same
  screen on return: the navigation path, the reader at the same page and offset, a search with its
  query and filters (its results may be loaded again); reading progress and downloads MUST lose
  nothing.

**Downloads, library and storage (Android differences)**

- **FR-026**: Downloads MUST follow the Windows rules (queue, one per source unless the source
  allows parallel requests, pause/resume/cancel, stop on rate limit or sign-in required, reuse of
  cached pages, continue after a restart without fetching finished pages again, no partial file left
  on cancel; 006 FR-001 – FR-006).
- **FR-027**: Archives MUST use the archive format of 006 (one `.cbz`, stored entries, metadata
  format 1, written back by crash-safe updates); an archive written on either platform MUST open on
  the other with its metadata, progress and reader settings (Constitution V).
- **FR-028**: Library folders MUST be added and removed through the system folder picker and keep
  access across restarts; the first save without a library folder MUST ask the user to choose one
  (suggesting a "Hentai Reader" folder in the shared documents), so saved galleries outlive an
  uninstall of the app. Move-or-keep on removal and unavailable folders follow 006 FR-016, FR-017.
- **FR-029**: The Library MUST pick up archives added by other apps when it is opened, on
  pull-to-refresh and on start; deletion MUST ask for confirmation and then remove the archive from
  the device (there is no recycle bin). The page cache location MUST be selectable between the
  device's internal storage and a removable card when one exists, moved in the background as in
  006 FR-024, FR-025.

**Replaced Windows behaviours**

- **FR-030**: System Back (button or gesture) MUST replace Escape, Alt+Left and the mouse back
  button: it closes an open dialog, sheet or the pane first, then goes back a level within the
  current section, and on the section's first page closes the app.
- **FR-031**: Long press MUST replace Ctrl+click (the star clears the default query).
- **FR-032**: Keyboard shortcuts, mouse-wheel zoom, F11, window size and position, "open folder" and
  "show in folder" have no Android counterpart and are not required.
- **FR-033**: The E-Hentai sign-in page MUST open inside the app, allow only the source's login
  domains and open other links in the system browser (005 FR-028, FR-029).
- **FR-034**: Changing the interface language MUST take effect for the whole app and return to the
  same screen, without the user restarting it by hand.

**Shell, privacy and distribution**

- **FR-035**: First launch MUST require the adult-content confirmation before any source request or
  any disk write of this app's data; declining closes the app.
- **FR-036**: Cookies MUST be stored only on the device, protected by the device's credential
  storage, excluded from device and cloud backups, never logged or shown in full.
- **FR-037**: The app MUST be distributed as a signed installation package via GitHub Releases
  (Constitution "Platform & Distribution Constraints"); installing a newer package over an older one
  MUST keep all user data; the signing key is never in the repository.
- **FR-038**: The app MUST run on Android 8.0 and newer, on phones and tablets, in portrait and
  landscape.

**Localisation**

- **FR-039**: Every UI string MUST exist in en, de, fr, es, ja, uk with the same keys as Windows;
  plurals, dates, numbers and sizes follow the chosen locale; layouts fit long German and Japanese
  text on a narrow phone screen without clipping; source content is shown untranslated
  (Constitution IX).

### Key Entities

The entities are those of the Windows app (003, 005, 006): bundled source, source session, search
state, gallery view, reading session, reading progress, page image cache, source settings, account,
reader settings, library folder, saved gallery (archive), archive metadata, history entry, reader
settings per gallery, download, default query, cache location. Android adds or changes:

- **Library folder**: additionally holds the persisted access grant from the system folder picker
  and the reason it is unavailable (removed card, revoked access).
- **Download**: additionally "waiting for Wi-Fi" as a state reason.
- **Saved screen state**: the navigation path and per-screen state (search query and filters,
  reader page and offset) kept so the app can come back after the system stops it.
- **Memory budget**: the share of memory the app allows itself for decoded images, derived from the
  device, shrinking on low-memory signals.

## Success Criteria *(mandatory)*

### Measurable Outcomes

The reference device is a mid-range phone with 4 GB of memory and a 1080×2400 screen; the
low-end check uses a phone with 3 GB.

- **SC-001**: 100 % of contract tests pass on the Android host, and 100 % of recorded source test
  cases produce results identical to their snapshots.
- **SC-002**: From tapping the app icon to the start screen takes under 3 seconds on the reference
  device (after the first launch).
- **SC-003**: From choosing Read, the first page is visible within 2 seconds for 9 of 10 galleries
  on Wi-Fi or a good mobile connection.
- **SC-004**: While flinging at normal reading speed, 95 % of pages are loaded before they enter the
  view; over a full read of a 200-page E-Hentai gallery the content in view never jumps.
- **SC-005**: Reading a 500-page gallery, including rotations and a long webtoon, keeps the app's
  memory use below 400 MB, with 0 crashes and 0 out-of-memory terminations on the 3 GB device.
- **SC-006**: The reader reacts to touch within 100 ms and scrolls without visible stutter while
  pages load and a download runs.
- **SC-007**: With the app in the background and no download running, 0 requests are sent over
  30 minutes.
- **SC-008**: A gallery saved in the background with the screen off finishes completely; after the
  system or the user stops the app mid-download, 100 % of pages fetched before are reused.
- **SC-009**: 100 % of archives saved by Windows 0.4.0 open on Android with their metadata and
  progress, and 100 % of archives saved on Android open in Windows 0.4.0.
- **SC-010**: After the system stops the app in the background, returning to it shows the reader
  within one screen of where it was in 100 % of tries.
- **SC-011**: The page cache never grows beyond its limit and never leaves less free storage than
  the FR-020 reserve.
- **SC-012**: The installation package is under 40 MB.
- **SC-013**: Every screen in German and Japanese at 360 dp screen width shows no clipped text.
- **SC-014**: A signed-in user sees their E-Hentai image limit in settings within 5 seconds of
  opening them.

## Assumptions

- Everything not listed as an Android difference behaves as on Windows; the Windows specs are the
  reference for details (error kinds, retry rules, archive layout, history and library behaviour).
- Contract 1.2 already covers everything; no contract change is expected. If one becomes necessary,
  it lands on both platforms together (Constitution II, X).
- Android 8.0 (2017) as the minimum keeps nearly all phones in use while allowing background
  downloads with notifications and the system folder picker with lasting access.
- "Full screen only" means the app always hides the system bars (immersive), not that the app
  refuses to run in split-screen or a pop-up window; when the system shows it in a part of the
  screen, it fills that part.
- "The left pane hides" applies to all screen sizes, tablets included, to keep one layout.
- Physical keyboards, mice and stylus are not specifically supported beyond what the system gives
  for free.
- Theme (light/dark) follows the system, as on Windows.
- Without a recycle bin on Android, deleting a saved gallery is final after the confirmation.
- The defaults chosen for limited resources (preload 3, cache 512 MB, storage reserve, Wi-Fi-only
  off, covers from network off) are starting values the owner may tune after testing on a phone.
- Out of scope, as on Windows: script updates (008), favorites, tag blacklist, sync between
  devices, paged and right-to-left reading, app lock and incognito mode.
