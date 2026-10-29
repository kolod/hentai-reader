# Feature Specification: Windows Reader

**Feature Branch**: `005-windows-reader`

**Created**: 2026-10-24

**Status**: Draft

**Input**: User description: "005-windows-reader. The reader is not a separate application: the search
window is replaced by the reader window." Expanded from [product-overview.md](../product-overview.md)
sections 3 and 6: seamless vertical-strip online reader for Hitomi and E-Hentai inside the existing
Windows app; source settings; E-Hentai/ExHentai login by cookies (typed or captured from an in-app
login page); login state and remaining image limit; all new strings in six languages.

## Clarifications

### Session 2026-10-24

- Q: What kind of full screen does F11 give? → A: Borderless full screen: the window covers the
  whole monitor it is on, without title bar and taskbar, but it stays a normal window (no exclusive
  full-screen mode as in games): Alt+Tab, other windows, notifications and display changes behave
  as usual.
- Q: Where does F11 work? → A: Everywhere in the app (browsing, gallery, settings, reader); the
  window state survives moving between them. In the reader, full screen additionally hides the
  navigation pane and the reader toolbar (toolbar reappears on pointer/keys, FR-017).
- Q: How are pages of different width shown (cover or translator notes from another scan, a
  two-page spread instead of one page)? → A: Every page is shown at exactly the column width; the
  height follows the page's own aspect ratio. File resolution never changes the displayed width
  (lower-resolution pages are scaled up, higher ones down), so the strip edges stay straight;
  spreads therefore appear at half the text size of single pages (accepted; zoom covers it).
- Q: How many pages may be visible at once? → A: Any number: on a monitor rotated to portrait
  with landscape pages (CG sets, spreads) three or more pages are in view together. Preloading and
  the in-memory set are therefore sized by screen height, not only by a page count, and the
  monitor may be rotated while reading.

### Session 2026-10-25 (owner's review of the first build)

- Q: Is the navigation pane shown in the reader? → A: Never: the reader always takes the whole
  window; the pane comes back with the gallery (FR-016).
- Q: What width does the reader start at? → A: The window width ("fit width", setting, on by
  default); W or the toolbar button returns to it, any zoom step leaves it; with the setting off
  the start is the maximum reading width (FR-011, FR-015).
- Q: Gaps between pages? → A: A fixed gap is a setting (0–48 px, default 8 px), toggled with G
  while reading; galleries tagged webtoon open without it (FR-004b; Constitution 1.1.0, IV).
- Q: What does a click on a gallery thumbnail do? → A: Opens the reader at that page (FR-019a).
- Q: Is the window remembered? → A: Size, position and maximized state are restored at start when
  that place is still on a display (FR-035).
- Q: What if an image server stalls? → A: After 20 s the source is asked for a new location
  (`RetryHint`) without waiting for an error, up to 2 times, then the page fails with Retry
  (FR-020a).
- Q: Can the search filter several languages? → A: Yes, any of the chosen languages (contract 1.2
  `multi` language filter); E-Hentai searches them with its "any of" terms, Hitomi (no such search
  on the site) filters its all-language feed by the per-language lists (FR-036).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Read a gallery in one seamless strip (Priority: P1)

From a gallery's details the user chooses Read. The main window switches from browsing to the
reader: all pages of the gallery form one continuous vertical strip without gaps or visible seams.
The user scrolls with the mouse wheel, touchpad, scroll bar or keyboard; pages that are not loaded
yet already occupy their place, so the strip never jumps while images arrive.

**Why this priority**: Reading is the purpose of the app; browsing without reading is only half a
product.

**Independent Test**: Open a Hitomi gallery and an E-Hentai gallery, read from the first to the last
page by scrolling and by keyboard, watching that no page shifts the content in view.

**Acceptance Scenarios**:

1. **Given** a gallery's details, **When** the user chooses Read, **Then** the same window shows the
   reader at the first page within 2 seconds on a typical connection, and the browsing view is no
   longer shown (no new window opens).
2. **Given** the reader, **When** pages are still loading, **Then** each not-yet-loaded page shows a
   placeholder of its expected size, and the content currently in view does not move when images
   above or within it finish loading.
3. **Given** an E-Hentai gallery whose page sizes are first known only approximately, **When** the
   exact size of a page becomes known, **Then** the correction is applied before that page enters the
   view, or, if it lies above the view, without moving the content in view.
4. **Given** the reader, **When** the user presses Space or Page Down, Page Up, Home or End,
   **Then** the strip moves one screen down, one screen up, to the first page or to the last page.
5. **Given** a wide window, **When** the reader is shown, **Then** the strip is centred and no wider
   than the reading-width setting; on narrow windows it fills the width.
6. **Given** the reader, **When** the user scrolls at normal speed, **Then** the next pages (as many as
   the preload setting, 3–5) are already loaded before they come into view.
7. **Given** a gallery mixing page resolutions and proportions (a low-resolution cover, a notes page,
   a two-page spread), **When** it is read, **Then** every page has exactly the strip width, none is
   cropped or stretched out of proportion, and the strip edges are straight.

---

### User Story 2 - Back to browsing exactly where it was (Priority: P1)

The reader replaces browsing in the same window. When the user leaves the reader, they return to
the gallery's details and from there to the search with the same results, filters and scroll
position as before.

**Why this priority**: The user explicitly asked for a single window; losing the search after every
gallery would make the single-window design worse than separate windows.

**Independent Test**: Search with filters, scroll down 100+ results, open a gallery, read, go back
twice, and check that the results, filters and scroll position are unchanged and no request was
repeated.

**Acceptance Scenarios**:

1. **Given** the reader opened from a gallery, **When** the user goes back (Back button, Escape when
   not in full screen, mouse back button, Alt+Left), **Then** the gallery's details are shown as they
   were.
2. **Given** the gallery's details reached from a search, **When** the user goes back, **Then** the
   search shows the same query, filters, loaded results and scroll position, without loading them
   again.
3. **Given** the reader, **When** the user switches to another source in the navigation, **Then**
   the reader closes, its reading position is saved, and that source's browsing is shown.

---

### User Story 3 - Resume where I stopped (Priority: P2)

The app remembers how far the user read each gallery. Opening the gallery again offers to continue
at that point.

**Why this priority**: Galleries often have hundreds of pages; starting over each time is a major
annoyance, but reading works without it.

**Independent Test**: Read a gallery to page 40, close the app, start it again, open the gallery and
continue; the reader opens at page 40.

**Acceptance Scenarios**:

1. **Given** a gallery read up to some page, **When** its details are shown again (also after an app
   restart), **Then** they show the progress ("page 40 of 120") and a Continue action next to Read
   from start.
2. **Given** Continue, **When** the reader opens, **Then** it is positioned at the saved page with the
   same offset within the page.
3. **Given** a gallery read to the last page, **When** its details are shown, **Then** it is marked as
   read and Read starts from the beginning.

---

### User Story 4 - Navigate a long gallery (Priority: P2)

In a long gallery the user can see where they are and jump: a page indicator and a scrubber to
drag to any page, a "go to page" command, zoom and full screen.

**Why this priority**: Makes long galleries usable; not needed for short ones.

**Independent Test**: In a 300+ page gallery jump to page 250 by scrubber and by number, zoom in and
out, enter and leave full screen.

**Acceptance Scenarios**:

1. **Given** the reader, **When** the user scrolls, **Then** the current page number and total are
   visible ("57 / 312").
2. **Given** the scrubber, **When** the user drags it, **Then** the target page number is shown while
   dragging and the strip jumps to that page on release; pages near the target load first.
3. **Given** Go to page, **When** the user enters a valid number, **Then** the strip jumps there;
   an invalid number is rejected with a message.
4. **Given** the reader, **When** the user zooms with Ctrl+wheel or Ctrl+Plus/Minus/0, **Then** the
   strip width changes around the point under the pointer (keyboard: around the view centre), and
   Ctrl+0 returns to fit width; the zoom stays for the rest of the session.
5. **Given** the reader, **When** the user presses F11 or chooses Full screen, **Then** the window
   covers the whole monitor without navigation, title bar or taskbar, while Alt+Tab to other
   windows keeps working; F11 leaves full screen and restores the window as it was (Escape only
   navigates — amended 2026-10-29, see FR-003).
6. **Given** browsing, a gallery's details or settings, **When** the user presses F11, **Then** the
   window becomes borderless full screen with the navigation still shown; opening the reader keeps
   full screen and hides the navigation; going back shows it again, still in full screen.

---

### User Story 5 - Failures and limits while reading (Priority: P2)

A page that fails to load can be retried where it is. When the site limits requests (E-Hentai image
limit, HTTP 429/509), loading stops and the user is told why instead of the app hammering the site.

**Why this priority**: Network failures and E-Hentai limits are common; the constitution requires
stopping on limits.

**Independent Test**: Simulate a failing image and a rate-limit response (offline fixtures) and check
the in-place retry and the stop message.

**Acceptance Scenarios**:

1. **Given** a page whose image fails to load, **When** the failure happens, **Then** the page slot
   shows the error and a Retry action in place; the rest of the strip keeps working.
2. **Given** Retry on a failed page, **When** the user chooses it, **Then** the source is asked for a
   new image location for that page (for E-Hentai, an alternative server) and the page loads in
   place.
3. **Given** the site signals a rate limit, **When** it happens, **Then** no further pages of that
   source are requested, a message explains the limit (with the waiting time when the site gives
   one), and loading resumes only when the user chooses Retry.
4. **Given** the network is gone, **When** pages fail, **Then** already loaded pages remain readable
   and the failed ones offer Retry.

---

### User Story 6 - Source settings and E-Hentai login (Priority: P2)

Each source has a settings page built from what the source declares (for E-Hentai: image quality
resampled/original, site e-hentai/exhentai). For E-Hentai the user can sign in, by pasting cookies or
by signing in on the site's own login page inside the app, and sees whether they are signed in and
how much of the image limit is used.

**Why this priority**: Signing in raises E-Hentai image limits and is required for ExHentai; without
it, long reading sessions hit the limit quickly.

**Independent Test**: Open E-Hentai settings, sign in through the in-app login page, see "signed in"
and the image limit, switch site to ExHentai, browse and read; sign out and see the state change.

**Acceptance Scenarios**:

1. **Given** a source's settings page, **When** it is shown, **Then** it lists exactly the settings the
   source declares, with translated labels and the current values; changes apply to the next request.
2. **Given** E-Hentai settings, **When** the user signs in on the in-app login page, **Then** the
   cookies the site sets are kept for E-Hentai only, the page closes, and the state shows signed in.
3. **Given** E-Hentai settings, **When** the user pastes cookie values instead, **Then** they are
   validated by checking the account status and the result is shown (signed in or rejected).
4. **Given** a signed-in E-Hentai account, **When** the settings or the reader show account status,
   **Then** the remaining image limit and its reset time are shown where the site provides them.
5. **Given** sign-out, **When** the user chooses it, **Then** the stored cookies of that source are
   deleted and the state shows signed out.
6. **Given** site ExHentai without access, **When** the user selects it, **Then** the app explains that
   ExHentai needs a signed-in account with access and does not send requests that would fail.

---

### User Story 7 - Reader settings (Priority: P3)

The user can set how many pages are preloaded (3–5), the maximum reading width, and the size of
the image cache, and can clear the cache.

**Why this priority**: Defaults work for most users; tuning is a convenience.

**Independent Test**: Change each setting and observe the effect; clear the cache and see its size
drop to zero.

**Acceptance Scenarios**:

1. **Given** reader settings, **When** the user changes preload depth, **Then** the reader loads that
   many pages ahead from then on.
2. **Given** the cache size limit, **When** the cache grows beyond it, **Then** the least recently
   used images are removed.
3. **Given** Clear cache, **When** the user chooses it, **Then** cached images are deleted (reading
   progress and settings stay) and the shown size becomes zero.

---

### Edge Cases

- Gallery removed or unavailable when Read is chosen: the reader is not opened; the error is shown on
  the details with Retry.
- A page whose size the source cannot give at all: a portrait placeholder (2:3) is used and corrected
  as for estimated pages.
- Pages that differ from the rest of the gallery (a cover or translator notes from another scan at a
  different resolution, a two-page spread in place of a single page, a very tall webtoon page): all
  are shown at the strip width with their own proportions (FR-004a), never cropped; the strip edges
  stay straight. A spread's text is smaller than on single pages; the user can zoom. Very tall
  images are decoded in parts or at a capped resolution so memory stays bounded.
- Animated images (GIF/animated WebP): shown animated; only the pages in or near view animate.
- The window is resized or the monitor is rotated (landscape ↔ portrait) while reading: the position
  (page and offset) is kept; the preload and memory areas adapt to the new screen height at once.
- Many pages visible together (landscape pages on a portrait monitor, small zoom): all visible pages
  load before any page ahead; none of them stays a placeholder longer than its own download takes.
- Language change (app restart) while reading: the app reopens on the same source; the reading
  position is saved so Continue works.
- Rapid scrubbing across hundreds of pages: requests for pages that left the target area are
  cancelled; politeness limits are never exceeded.
- Cache folder unavailable or full disk: reading continues without the disk cache; a one-time notice
  says so.
- Cookies pasted for the wrong site or expired: account status shows signed out with an explanation;
  nothing is sent to other domains.
- The in-app login page tries to open other sites: only the source's own login domains are allowed;
  others open in the system browser.
- The same gallery opened from two sources (unlikely) or two times: progress is per source + gallery
  id.

## Requirements *(mandatory)*

### Functional Requirements

**Reader in the main window**

- **FR-001**: Choosing Read (or Continue) on a gallery's details MUST show the reader in the same main
  window in place of browsing; no additional window is opened.
- **FR-002**: Leaving the reader MUST return to the gallery's details, and leaving those to the search,
  with query, filters, loaded results and scroll position unchanged and without repeating requests.
- **FR-003**: Back MUST be available by the Back button, Escape, Alt+Left and the mouse back button.
  *Amended 2026-10-29:* Escape only navigates and never changes full screen (F11 alone does): it goes
  back a level within the current section (a navigation item: a source, History, Library, Downloads,
  Settings), and on the section's first page it closes the app without confirmation, so a few
  presses always quit. An open dialog or flyout takes Escape first.

**Strip**

- **FR-004**: All pages of a gallery MUST form one vertical strip without visible seams between
  pages; the only space between them is the optional gap of FR-004b.
- **FR-004b**: A fixed gap between pages (setting 0–48 px, default 8 px, 0 = never) MUST be part of
  the strip geometry, so it never makes the strip jump; G toggles it while reading, keeping the
  page in view; galleries tagged `webtoon` open without it.
- **FR-004a**: Every page MUST be shown at exactly the strip width, whatever the resolution or
  proportions of its image: lower-resolution pages are scaled up and higher ones down; each page's
  height follows its own aspect ratio, so no page is cropped or distorted. Two-page spreads, covers
  scanned differently and inserted notes pages are therefore shown at the strip width too.
- **FR-005**: Every page MUST occupy its expected size before its image loads, using the exact size
  where the source gives it and the estimate otherwise (Constitution IV).
- **FR-006**: An exact size learned for an estimated page MUST be applied before the page enters the
  view; a correction for a page above the view MUST NOT move the content in view.
- **FR-007**: Images MUST be decoded for the displayed width (not the original size), and memory for
  pages far from the view MUST be released.
- **FR-008**: The reader MUST keep loaded ahead of the view at least N pages, N from the preload
  setting (3–5, default 4), **and** at least one full screen height, whichever covers more; behind
  the view at least one page and half a screen. Any number of pages may be visible at once (e.g.
  landscape pages on a portrait monitor); all of them are loaded first, then the ones ahead. Pages
  near a jump target load first.
- **FR-008a**: The set of decoded pages kept in memory MUST follow the same rule (everything visible
  plus the preload area ahead and the area behind), so it grows with the number of visible pages and
  shrinks when fewer pages fit on screen; pages outside it are released and come back from the disk
  cache (FR-023) when needed.
- **FR-009**: Loading, decoding and source script calls MUST NOT run on the UI thread; scrolling MUST
  stay responsive while pages load.
- **FR-010**: Page images MUST be requested through the source (`getPageUrl`, with the source's
  cookies and politeness limits); the reader MUST NOT construct site URLs itself.
- **FR-011**: The reader MUST start at the window width ("fit width", setting, on by default).
  With the setting off, the strip is centred with a maximum width (setting, default 1000 px at
  100 % scaling) and narrower windows use the full width.

**Navigation**

- **FR-012**: Keyboard: Up/Down arrows scroll continuously while held, Space / Page Down and
  Shift+Space / Page Up the same but faster, all easing in and out (no fixed page steps); Home first
  page, End last page.
- **FR-013**: The current page and total MUST be visible; a scrubber MUST allow jumping to any page,
  showing the target page while dragging.
- **FR-014**: Go to page MUST accept a page number and reject invalid input with a message.
- **FR-015**: Zoom by Ctrl+wheel (around the pointer), Ctrl+Plus/Minus and toolbar buttons, relative
  to the starting width; Ctrl+0 returns to the default width (FR-011); W and the toolbar "fit
  width" button return to the window width, the button being lit only at exactly that width. Zoom
  lasts for the session.
- **FR-016**: Full screen via F11 anywhere in the app (browsing, gallery, settings, reader) and a
  reader toolbar command; F11 leaves it, and Escape leaves it in the reader. It stays on when moving
  between screens. The reader never shows the navigation pane (full screen or not); in full screen
  it also hides the reader toolbar (FR-017); on other screens the navigation stays. Full screen is
  borderless: the window covers the whole monitor it is on, hiding its title bar and the taskbar,
  and remains an ordinary window (no exclusive full-screen mode): Alt+Tab, switching to other
  windows, notifications and resolution or monitor changes work normally, and leaving full screen
  restores the previous window size, position and maximized state. Entering and leaving happen
  without the system resize animation, so the strip is laid out once.
- **FR-017**: The reader toolbar (page indicator, scrubber, go to page, zoom, full screen, back) MUST
  hide while reading and reappear on pointer movement near it or on a key press.

**Progress**

- **FR-018**: The reading position (page and offset within the page) MUST be saved per source and
  gallery when leaving the reader, closing the app, and periodically while reading.
- **FR-019**: A gallery's details MUST show saved progress and offer Continue and Read from start; a
  gallery read to the end is marked read.
- **FR-019a**: A click on a page thumbnail in the gallery MUST open the reader at that page.

**Failures and limits**

- **FR-020**: A page that fails MUST show the error and Retry in its own slot; Retry asks the source
  for a new image location with the retry hint (contract `RetryHint`).
- **FR-020a**: An image that has not arrived within 20 s MUST be abandoned and the source asked for
  a new location with the retry hint, automatically up to 2 times; then the page fails as in
  FR-020.
- **FR-021**: A rate-limit signal MUST stop all further requests of that source until the user chooses
  Retry, with an explanation and the waiting time when known (Constitution VI).
- **FR-022**: Loaded pages MUST remain readable when later requests fail.

**Cache**

- **FR-023**: Loaded page images MUST be kept in a per-user on-disk cache with a size limit (default
  1 GB, setting) and least-recently-used eviction; a cached page MUST NOT be requested from the site
  again.
- **FR-024**: The cache MUST be clearable from settings, showing its current size; clearing MUST NOT
  touch progress, settings or sign-ins.
- **FR-025**: The cache MUST live in the app's per-user data area and never in the program folder.

**Source settings and accounts**

- **FR-026**: Each source MUST have a settings page listing exactly the settings its manifest declares,
  with labels and values from the six-language vocabulary; changes apply to the next request.
- **FR-027**: For sources that declare account status, the settings page MUST show signed-in state and,
  where the site provides it, the image limit (used / max, reset time); the reader MUST show the same
  status where it matters (e.g. after a rate limit).
- **FR-028**: Sign-in MUST be possible by pasting cookie values and by signing in on the site's login
  page shown inside the app; captured cookies MUST be stored only for that source and only for its
  declared domains.
- **FR-029**: The in-app login page MUST allow only the source's login domains; other links open in the
  system browser.
- **FR-030**: Sign-out MUST delete the source's stored cookies.
- **FR-031**: Choosing ExHentai without an account that has access MUST be explained and MUST NOT
  trigger requests that are known to fail.
- **FR-032**: Cookies MUST remain local, encrypted for the Windows user as today, and never be logged
  or shown in full after entry.

**Reader settings**

- **FR-033**: Settings MUST offer preload depth (3–5), fit width, maximum reading width, gap between
  pages, cache size limit and Clear cache.

**Search**

- **FR-036**: The language filter MUST allow several languages at once, matching galleries in any
  of them, for both sources; the result order stays the site's.

**Window**

- **FR-035**: The main window MUST reopen with its last size, position and maximized state (also
  after the restart for a language change); a saved place no longer on any display is ignored.
  *Amended 2026-10-29:* a window closed in full screen reopens in full screen, entered on top of the
  restored size, position and maximized state, so F11 returns to them.

**Localization**

- **FR-034**: Every new user-visible string MUST exist in the six UI languages with plural forms where
  numbers are shown (Constitution IX).

### Key Entities

- **Reading session**: the open gallery in the reader; pages with expected sizes, load state per page
  (placeholder, loading, loaded, failed), current position, zoom.
- **Reading progress**: per source + gallery id: last page, offset within it, total pages, read
  flag, last read time.
- **Page image cache**: per-user set of cached images keyed by source, gallery and page (and quality),
  with size and last use.
- **Source settings**: values of the settings a source declares (already stored per source).
- **Account**: per source: cookies (encrypted), signed-in state, image limit as last reported.
- **Reader settings**: preload depth, maximum width, cache size limit.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: From choosing Read, the first page is visible within 2 seconds for 9 of 10 galleries on
  a typical broadband connection.
- **SC-002**: While scrolling at normal reading speed, 95 % of pages are fully loaded before they enter
  the view.
- **SC-003**: Over a full read of a 200-page E-Hentai gallery, the content in view never jumps because
  of a page loading or a size correction (0 observed jumps).
- **SC-004**: Returning from the reader to the search restores the same results and scroll position in
  under 0.5 seconds with no new search request.
- **SC-005**: Reopening a gallery after an app restart resumes within one screen of where reading
  stopped in 100 % of tries.
- **SC-006**: After a rate-limit signal, the app sends 0 further requests to that source until the user
  retries.
- **SC-007**: A signed-in user sees their E-Hentai image limit in settings within 5 seconds of opening
  them.
- **SC-008**: Reading a 500-page gallery keeps the app's memory use below 1 GB and the reader
  responsive (input reaction under 100 ms).
- **SC-009**: Every new screen passes the six-language layout review without clipped text.
- **SC-010**: Scrolling back to an already read page at normal speed never shows a visible
  placeholder; the page comes from the disk cache without a request to the site.
- **SC-011**: On a monitor rotated to portrait (e.g. 2160×3840) reading a gallery of landscape pages,
  with three or more pages in view, 95 % of pages are loaded before they enter the view and memory
  stays within SC-008.

## Assumptions

- Online reading only; reading downloaded CBZ archives comes with the downloads feature and reuses
  this reader.
- Single strip mode only (vertical, top to bottom); paged and right-to-left modes are out of scope
  (product overview).
- The contract already provides everything the reader needs (exact/estimated sizes, size refinement
  from the page image, retry hints, account status); no contract change is expected.
- The in-app login page uses the system web view component available on supported Windows versions;
  if it is missing, cookie pasting remains available.
- Theme (light/dark) follows Windows; a theme setting is outside this feature.
- Tag blacklist, default language filter and script update settings from the product overview belong
  to later features.
- The cache default of 1 GB and reading width of 1000 px are starting values, adjustable in settings.
- The app already allows one instance per Windows session; the reader lives in that single window.
