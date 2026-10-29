# Contract: Library, History, Downloads UI

User-facing behaviour; UI Automation checks use the AutomationIds and the screen marker of 005
(navigation `ItemStatus` = page AutomationId).

## Navigation

Pane, top to bottom: **History** (`HistoryView`), **Library** (`LibraryView`), **Downloads**
(`DownloadsView`, with a badge = number of active downloads), separator, sources, footer Settings.
Back from the reader returns to the screen it was opened from (history, library or gallery).

## History (`HistoryView`)

- List, newest first: cover, title, source, "Page {n} of {total}" / "Finished", relative time of
  last reading ("2 hours ago"), a library mark when a saved copy exists.
- Click / Enter: open the reader at the saved position (saved copy if any, else online through the
  gallery). Context menu / Delete key: **Remove from history**. Toolbar: **Clear history**
  (confirmation dialog).
- Empty state text when there is no history. Loads progressively (5 000 entries, SC-006).

## Library (`LibraryView`)

- Grid of cards (cover from the archive, title, source, page count, progress bar, folder name when
  more than one folder exists). Unreadable / unavailable archives shown greyed with the reason.
- Toolbar: search box (title, filters while typing), source filter (All + each source + "Local"
  for foreign archives), state filter (All / Unread / In progress / Finished), sort (Date added,
  Last read, Title).
- Click: open the reader at the saved position. Context menu: **Open folder** (Explorer with the
  file selected), **Delete** (confirmation; to the Recycle Bin).
- Empty state with a button to Settings → Library folders.

## Gallery screen and reader additions

- Gallery: **Save to library** button next to Read/Continue; while downloading it shows
  "Saving {done} / {total}" with Pause / Cancel; when saved: **In library** (opens the saved copy).
- Reader toolbar: **Save to library** icon (same states) for online galleries; hidden when reading a
  saved copy.
- The reader opens with the gallery's remembered zoom, width mode and gaps (FR-020).

## Downloads (`DownloadsView`)

- One row per download: title, source, state (Queued, Downloading, Paused, Limited — with reason and
  waiting time, Failed — with reason, Done), progress "{done} / {total}", buttons Pause / Resume /
  Cancel / Open (when done). **Clear finished** removes done rows.

## Search screen addition

- **Star** button left of the search box (`DefaultQueryButton`): filled when the current query and
  filters equal the source's default, empty otherwise. Click: save current as default.
  Ctrl+click: clear the default. Tooltip explains both.

## Settings additions

- **General → Start page**: Last used source (default) · each available source · History · Library.
- **Library**: list of folders (path, galleries count, available/unavailable), **Add folder**,
  **Target for new saves** (radio), **Remove** → dialog *Move galleries to {folder}* / *Keep files*
  / Cancel; move progress inline.
- **Reader**: existing items + **Page cache folder** with **Change…**; while a move runs: progress
  "{moved} of {total}" and **Cancel move**.
- **Sources**: per source, the saved **Default search** (text and filters summary) with **Remove**.

## Strings

All new strings in en-US, de-DE, fr-FR, es-ES, ja-JP, uk-UA; plurals for counts (pages, galleries,
downloads); relative times and dates by the UI culture.
