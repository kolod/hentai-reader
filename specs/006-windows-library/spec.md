# Feature Specification: Windows Library and History

**Feature Branch**: `006-windows-library`

**Created**: 2026-10-25

**Status**: Draft

**Input**: User description: "Windows library and history (006-windows-library, replaces
006-cbz-downloads in the roadmap). Builds on the 005 online reader." History of opened galleries;
a library of saved galleries readable offline in the same reader; "Save to library" from the
gallery screen and the reader, one uncompressed archive per gallery with metadata (incl. reading
progress and the reader's zoom); several library folders; cache location with background
migration; start page setting; a saved default query per source (star button). From the owner's
review of 005 and [product-overview.md](../product-overview.md) sections 3–6.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Save a gallery and read it offline (Priority: P1)

On a gallery's screen (or in the reader) the user chooses **Save to library**. The app downloads
every page through the source, with the same politeness, retries and limits as online reading,
and writes one archive file for the gallery into the library folder. Progress is visible; the
user can keep browsing and reading meanwhile. When it is done, the gallery appears in the
**Library** screen and opens in the same reader without any network connection.

**Why this priority**: Saving galleries is the main thing the app cannot do today; without it
there is no library to show and nothing works offline.

**Independent Test**: Save a 40-page gallery, disconnect the network, open it from the Library and
read it to the end in the reader.

**Acceptance Scenarios**:

1. **Given** a gallery's screen, **When** the user chooses Save to library, **Then** a download
   starts, its progress ("12 / 40") is shown on the gallery screen and in a downloads list, and the
   user can leave the screen without stopping it.
2. **Given** a finished download, **When** the user opens the Library, **Then** the gallery is
   listed with cover, title, source, page count and reading progress.
3. **Given** a saved gallery and no network, **When** the user opens it, **Then** all pages are
   shown in the reader with the same strip, zoom, gaps, keys and progress behaviour as online.
4. **Given** pages of the gallery already in the online page cache, **When** it is saved, **Then**
   those pages are taken from the cache and not requested again.
5. **Given** a running download, **When** the source signals a rate limit, **Then** the download
   stops (not retried in a loop), the reason and waiting time are shown, and the user can resume it
   later; pages already downloaded are kept.
6. **Given** a gallery already in the library, **When** its screen is opened, **Then** Save to
   library is replaced by **In library** (opens the saved copy) and no second archive is created.

---

### User Story 2 - Continue from history (Priority: P1)

Every gallery opened in the reader is remembered. A **History** screen lists them, newest first,
with cover, title, source, "page N of M" or "finished", and when they were last read. Opening an
entry continues reading where the user stopped, with the zoom and width mode the user used for
that gallery.

**Why this priority**: Finding "that gallery I was reading yesterday" is today only possible by
searching again; history is cheap and used every day.

**Independent Test**: Read three galleries partly, restart the app, open History, open the second
entry → the reader opens at the saved page with the same zoom.

**Acceptance Scenarios**:

1. **Given** galleries opened in the reader, **When** the user opens History, **Then** they are
   listed newest first with progress and the time of last reading.
2. **Given** a history entry, **When** the user opens it, **Then** the reader opens at the saved
   position; if the gallery is in the library the saved copy is used, otherwise it is read online.
3. **Given** a gallery the user zoomed to 130 % in reading-width mode, **When** it is opened again
   (from history, the gallery screen or the library), **Then** it starts at 130 % in that mode;
   other galleries keep the defaults.
4. **Given** a history entry, **When** the user removes it, **Then** it disappears (its saved
   archive, if any, is not touched); **Clear history** removes all entries after a confirmation.
5. **Given** an online gallery that no longer exists on the site, **When** it is opened from
   history, **Then** the source's "not found" message is shown and the entry can be removed.

---

### User Story 3 - Manage the library (Priority: P2)

The Library screen shows all saved galleries from all library folders. The user can search by
title, filter by source and read state, sort by date added, last read or title, and delete a
saved gallery. Archives copied into a library folder by hand (for example made on another device)
appear in the library too.

**Why this priority**: Needed once the library grows past a few dozen galleries; the core value
(saving and offline reading) already works without it.

**Independent Test**: Put 3 saved archives and 1 foreign comic archive into a library folder, open
the Library → 4 entries; search, filter and sort them; delete one and confirm the file is gone.

**Acceptance Scenarios**:

1. **Given** saved galleries, **When** the user types part of a title, **Then** only matching
   galleries are listed; source, read/unread filters and the three sort orders combine with it.
2. **Given** an archive made by this app on another device, **When** it is copied into a library
   folder, **Then** it is listed with its title, tags, source, page sizes and progress from its own
   metadata, without the app being restarted.
3. **Given** a comic archive with images but without this app's metadata, **When** it is in a
   library folder, **Then** it is listed with the file name as title and opens in the reader
   (pages in file-name order).
4. **Given** a saved gallery, **When** the user deletes it and confirms, **Then** its archive is
   moved to the Windows Recycle Bin and it disappears from the library; its history entry stays.
5. **Given** an archive that cannot be read (damaged, not an archive), **When** the library is
   scanned, **Then** it is listed as unreadable with its file name and does not stop the scan.

---

### User Story 4 - Several library folders (Priority: P2)

In Settings the user adds library folders (for example one on a large external disk), chooses which
one receives new saves, and removes folders. Removing a folder that still holds saved galleries
offers to move them into another library folder or just to stop showing them (files stay where
they are).

**Why this priority**: Libraries outgrow one disk; it matters later than saving itself.

**Independent Test**: Add a second folder, make it the target, save a gallery → the archive is in
the second folder; remove the first folder choosing "move" → its archives are now in the second
folder and still listed.

**Acceptance Scenarios**:

1. **Given** Settings → Library, **When** the user adds a folder, **Then** galleries already in it
   are listed in the library.
2. **Given** two library folders, **When** the user marks one as the target for new saves,
   **Then** the next save goes there.
3. **Given** a folder with saved galleries, **When** the user removes it and chooses Move,
   **Then** its archives are moved into the chosen remaining folder in the background with
   progress, and stay readable; choosing Keep files only removes the folder from the list.
4. **Given** a library folder that is temporarily unavailable (external disk unplugged), **When**
   the library is opened, **Then** its galleries are shown as unavailable instead of being dropped,
   and come back when the folder is reachable again.

---

### User Story 5 - Downloads keep going and survive a restart (Priority: P2)

Several galleries can be queued. The downloads list shows each one's state (queued, downloading,
paused, stopped by a limit, failed, done) and lets the user pause, resume and cancel. A download in
progress when the app is closed continues from where it stopped after the next start.

**Why this priority**: Large galleries take minutes and E-Hentai limits can interrupt them; losing
progress would make saving painful.

**Independent Test**: Queue two galleries, close the app mid-way, start it again → both continue
without downloading finished pages again; cancel one → no partial file remains.

**Acceptance Scenarios**:

1. **Given** two galleries saved one after the other, **When** the first is downloading,
   **Then** the second waits in the queue and starts when the first ends.
2. **Given** a download, **When** the user pauses it, **Then** no further page requests are made
   for it until Resume; pages already fetched are kept.
3. **Given** a download in progress, **When** the app is closed and started again, **Then** the
   download continues with the next missing page.
4. **Given** a download, **When** the user cancels it, **Then** its partial data is removed and the
   gallery is not in the library.
5. **Given** the E-Hentai image quality setting, **When** a gallery is saved, **Then** pages are
   downloaded in that quality, as for online reading.

---

### User Story 6 - Start where I want, with my usual search (Priority: P3)

In Settings the user chooses the start page: the last used source (as today), E-Hentai, Hitomi.la,
History or Library. On a source's search screen a **star** button left of the search box saves the
current query and filters as that source's default: whenever the source is opened, it starts with
that search. Ctrl+click on the star clears it; Settings lists the saved defaults and can remove
them.

**Why this priority**: Comfort features; they save a few clicks on every start.

**Independent Test**: Set the start page to History → the app opens on History. On Hitomi.la search
`language:english female:glasses`, press the star, restart and open Hitomi.la → that search runs.

**Acceptance Scenarios**:

1. **Given** start page Library, **When** the app starts, **Then** the Library screen is shown first.
2. **Given** a query and filters on a source, **When** the user presses the star, **Then** the star
   is shown filled and that query becomes the source's default.
3. **Given** a default query, **When** the user opens that source (start, navigation), **Then** the
   default search is run and shown; searches from tags and from the box work as before.
4. **Given** a default query, **When** the user Ctrl+clicks the star or removes it in Settings,
   **Then** the source opens with its normal front page again.
5. **Given** the query or filters were changed after saving, **When** the user looks at the star,
   **Then** it is shown empty (the current search is not the default) and pressing it replaces the
   default.

---

### User Story 7 - Move the page cache (Priority: P3)

In Settings the user chooses another folder for the online page cache (for example a faster or
bigger disk). Existing cached pages are moved in the background; reading continues meanwhile.

**Why this priority**: Only matters with large caches on a small system disk.

**Independent Test**: With 500 MB cached, change the cache folder while reading → reading continues
without errors; when the move ends, the old folder is empty and cached pages open from the new one.

**Acceptance Scenarios**:

1. **Given** a cache folder change, **When** the move runs, **Then** its progress is shown in
   Settings, already cached pages stay usable from wherever they are at that moment, and new pages
   go to the new folder.
2. **Given** the new folder runs out of space or becomes unavailable during the move, **When** the
   move fails, **Then** the app keeps the old folder, shows why, and no cached page is lost.
3. **Given** the app is closed during a move, **When** it starts again, **Then** the move continues.

---

### Edge Cases

- Library folder on a disk that is unplugged while a download writes into it: the download stops
  with a clear message and resumes into that folder when it is back (or the user picks another).
- Not enough disk space for a save: the download stops before or when space runs out, with the
  reason; nothing half-written is listed in the library.
- The same gallery saved into two different folders (copied by hand): listed once per archive,
  each marked with its folder; progress is shared through the gallery's identity.
- The archive file name: built from the title, shortened and stripped of characters Windows does
  not allow; two galleries with the same title get distinct names.
- A saved gallery whose source is no longer available (script removed or broken): it still opens
  from the library; online actions (open on site, search tags) are disabled.
- E-Hentai gallery saved while signed out and pages needing sign-in: those pages fail with
  "sign-in required"; the download stops instead of skipping them.
- History with thousands of entries: the History screen stays responsive (loads progressively).
- Reading progress and zoom of a library gallery change while its archive is on a read-only or
  missing disk: kept in the app and written into the archive when it becomes writable.
- Start page set to a source that is unavailable: the app falls back to the first available source.
- Default query that the source now rejects (e.g. a removed filter value): the error is shown as
  for any search and the star can clear it.
- A user-placed archive is renamed or moved between library folders while the app runs: it keeps
  its history and progress (identified by its metadata, not its path).

## Requirements *(mandatory)*

### Functional Requirements

**Saving (downloads)**

- **FR-001**: The gallery screen and the reader toolbar MUST offer **Save to library** for galleries
  not yet in the library, and **In library** (open the saved copy) for those already saved.
- **FR-002**: A save MUST fetch every page through the source exactly as online reading does (page
  locations from the source, source cookies, politeness limits, retry with a retry hint), reuse
  pages already in the online page cache, and use the source's image quality setting.
- **FR-003**: A rate-limit or sign-in-required signal MUST stop the download (Constitution VI),
  show the reason and waiting time when known, and keep the pages already fetched; the user resumes
  it explicitly.
- **FR-004**: Saves MUST be queued; one download per source runs at a time, others wait; the user
  can pause, resume and cancel each; a list of downloads with state and progress MUST be reachable
  from the navigation.
- **FR-005**: Downloads MUST survive closing the app: on the next start unfinished downloads
  continue (paused ones stay paused) without fetching finished pages again.
- **FR-006**: A cancelled download MUST leave no partial file in any library folder.

**Archive format**

- **FR-007**: Each saved gallery MUST be exactly one comic archive file (ZIP container, images
  stored without recompression, file extension `.cbz`) holding all page images in reading order
  and one metadata file (Constitution V).
- **FR-008**: The metadata inside the archive MUST include: source and gallery identity, title(s),
  tags, language, type, uploader and upload date when known, page count, each page's size, the date
  it was saved, the reading progress, and the reader's zoom, width mode and gap state for this
  gallery. It MUST be documented so another reader (and the Android app later) can use it.
- **FR-009**: Reading progress and reader settings of a library gallery MUST be kept in the app
  and written into its archive when the user leaves the reader or closes the app (not on every
  scroll); a failed write is retried later and never damages the archive.
- **FR-010**: Page images MUST be stored as delivered by the source (no conversion); file names
  inside the archive MUST sort in reading order.

**Library**

- **FR-011**: A **Library** screen MUST list every saved gallery from all library folders with
  cover, title, source, page count, progress and folder; search by title, filters by source and
  read state, sort by date added, last read and title.
- **FR-012**: Opening a library gallery MUST use the 005 reader unchanged in behaviour, reading
  pages from the archive, with no network access needed.
- **FR-013**: Archives placed into a library folder outside the app MUST be picked up while the app
  runs and on start; archives with this app's metadata show it; other comic archives are listed by
  file name with pages in file-name order; unreadable files are listed as unreadable.
- **FR-014**: Deleting a library gallery MUST ask for confirmation and move its archive to the
  Windows Recycle Bin.

**Library folders**

- **FR-015**: Settings MUST let the user add and remove library folders and choose the target
  folder for new saves; the first run uses a default folder under the user's documents.
- **FR-016**: Removing a folder that holds saved galleries MUST offer Move (into another library
  folder, in the background with progress) or Keep files (folder just leaves the library).
- **FR-017**: Galleries in an unavailable folder MUST be shown as unavailable, not removed, and
  come back when the folder is reachable.

**History**

- **FR-018**: Every gallery opened in the reader MUST be recorded in history with cover, title,
  source, page count, progress, last read time and the reader's zoom, width mode and gap state.
- **FR-019**: A **History** screen MUST list entries newest first, open an entry at its saved
  position (the library copy when there is one, otherwise online), remove single entries and
  clear all after confirmation.
- **FR-020**: A gallery MUST open with its remembered zoom, width mode and gap state, whether from
  history, the gallery screen or the library; galleries without a record use the defaults (005).

**Start page and default queries**

- **FR-021**: Settings MUST offer the start page: last used source (default), E-Hentai, Hitomi.la,
  History or Library; an unavailable source falls back to the first available one.
- **FR-022**: Each source's search screen MUST have a star button left of the search box: pressed,
  it saves the current query and filters as the source's default; it is shown filled while the
  current search equals the default; Ctrl+click clears the default.
- **FR-023**: Opening a source with a default query MUST run that search; Settings MUST list the
  defaults per source with a remove action.

**Cache location**

- **FR-024**: Settings MUST let the user choose the folder of the online page cache; existing cached
  pages are moved in the background with progress, stay usable during the move, and the move
  continues after a restart.
- **FR-025**: A move that fails (no space, folder unavailable) MUST keep the previous folder, lose
  no cached page and show the reason.

**General**

- **FR-026**: History and Library MUST be reachable from the navigation pane next to the sources.
- **FR-027**: Every new user-visible string MUST exist in the six UI languages with plural forms
  where counts are shown (Constitution IX).
- **FR-028**: Nothing of this feature may be written to disk before the adult content
  confirmation.

### Key Entities

- **Library folder**: a folder the user added; whether it is the target for new saves; available or
  not.
- **Saved gallery (archive)**: one `.cbz` file in a library folder; gallery identity (source + id),
  title, cover, page list with sizes, tags, saved date; readable state (ok, unreadable, folder
  unavailable).
- **Archive metadata**: the self-describing record inside each archive (identity, descriptive
  fields, page sizes, progress, reader settings), versioned so later versions and the Android app
  can read it.
- **History entry**: gallery identity, title, cover, source, page count, progress, last read time,
  reader settings, link to a saved copy if any.
- **Reader settings per gallery**: zoom, width mode (fit width / reading width), gaps on/off.
- **Download**: gallery identity, target folder, state (queued, downloading, paused, limited,
  failed, done, cancelled), pages done / total, reason of a stop.
- **Default query**: per source: query text and filter values.
- **Cache location**: current folder and, during a move, the target folder and progress.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A saved gallery opens from the Library with its first page shown within 1 second,
  with the network disconnected.
- **SC-002**: Saving a 100-page gallery whose pages are all in the online cache finishes within
  10 seconds without any image request to the site.
- **SC-003**: After the app is closed during a download and started again, 100 % of the pages
  fetched before are reused (no page downloaded twice).
- **SC-004**: An archive saved by the app opens in at least one common third-party comic reader
  with all pages in order, and its metadata file is readable as plain text.
- **SC-005**: The Library with 1 000 saved galleries in two folders is listed, searchable and
  scrollable within 3 seconds of opening it; search results update while typing.
- **SC-006**: History with 5 000 entries opens within 1 second and the chosen entry resumes at its
  saved page and zoom.
- **SC-007**: During a cache move of 1 GB, reading continues with no failed page and no visible
  stall caused by the move.
- **SC-008**: An archive copied by hand into a library folder appears in the Library within
  5 seconds while the app runs.
- **SC-009**: The app opens on the chosen start page, and a source with a default query shows its
  results, in the same time as opening that screen by hand (no extra wait).
- **SC-010**: All new screens and dialogs show no clipped text in German and Japanese at a 1024 px
  wide window.

## Assumptions

- Archive format: `.cbz` (ZIP with stored, uncompressed entries) as Constitution V requires; the
  owner's "zip without compression" is the same container with the comic-book extension, which
  third-party readers recognise. Archives named `.zip` placed by hand are read too.
- One download per source at a time (sources' politeness limits apply anyway); downloads from
  different sources may run in parallel.
- History keeps entries until the user removes them (no automatic expiry); covers in history and
  library come from the archive or the image cache, not new requests where possible.
- The reader settings remembered per gallery are zoom, width mode and gaps; preload depth and cache
  size stay global.
- Default library folder: `Documents\Hentai Reader` of the current user; it can be changed or
  removed like any other folder (at least one folder always remains).
- Deleting goes to the Recycle Bin, so the app needs no undo of its own.
- Galleries are identified by source + gallery id; an archive without this app's metadata is
  identified by its content (so renaming or moving it keeps its history).
- Favorites, tag blacklist, library sync between devices and Android are out of scope here
  (roadmap); Android will read the same archives later (Constitution V).
- Builds on 005: reader, page cache, progress store, source sessions with cookies and limits,
  settings screen and navigation.
