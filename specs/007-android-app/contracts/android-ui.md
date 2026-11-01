# Contract: Android UI (FR-007 – FR-015, FR-030 – FR-034)

Screens, behaviour and strings are those of the Windows app
([005 reader-ui](../../005-windows-reader/contracts/reader-ui.md),
[006 library-ui](../../006-windows-library/contracts/library-ui.md)). This contract fixes only the
Android differences.

## Shell

| Element | Android |
|---------|---------|
| Navigation | Modal drawer, closed by default on every screen size. Items in the Windows order: sources, History, Library, Downloads (with count badge), Settings |
| Open drawer | ☰ button at the start of each screen's top bar; left-edge swipe **only** with 3-button navigation (left system-gesture inset = 0) |
| Close drawer | item chosen, scrim tap, Back, drag towards the edge |
| Reader | no drawer, no top bar; nothing is composed for them |
| System bars | hidden on every screen; swipe from an edge shows them transiently over the content (no relayout); hidden again by the system |
| Cutout | strip content may extend under it (`shortEdges`); bars, buttons, text, drawer and dialogs are inset by `safeDrawing` + `displayCutout` |
| Theme | follows the system (light/dark), Material 3, dynamic color **off** (same palette on every phone) |
| Orientation | portrait and landscape, no recreation on rotation or fold |

## Back order (FR-030)

1. open dialog, bottom sheet or menu → close it;
2. open drawer → close it;
3. reader → gallery details (position saved);
4. any other screen with a previous screen in the section → that screen, state unchanged;
5. first screen of the section → finish the app.

Predictive back animation is enabled for steps 3–5.

## Search screen

- Top bar: ☰, search field (source syntax), ☆/★ star **left of the field** (tap: save as default;
  **long press**: clear the default; filled only when the current search equals the default), filter
  button.
- Filters open in a bottom sheet with exactly the declared filters (multi-language chips for
  `multi` language filters). Closing without a change does not reload (Windows 0.4.0 fix).
- Results: adaptive grid of cards (minimum card width 160 dp; 2 columns on a 360 dp phone),
  infinite append, end and empty states, per-kind error with Retry and "copy details".

## Gallery screen

Metadata, tags grouped by namespace (tap a tag → search), thumbnail grid (sprite crop); actions
Read, Continue ("page 40 of 120"), Save to library / In library, Open in browser. A thumbnail tap
opens the reader at that page.

## Reader

| Windows input | Android |
|---------------|---------|
| wheel, scroll bar, arrows, Space/PgUp/PgDn | drag and fling (platform decay), edge overscroll glow/stretch |
| Home / End | toolbar buttons "first page" and "last page" |
| Ctrl+wheel, Ctrl+Plus/Minus | pinch around the fingers, 0.5–3.0 × the starting width |
| Ctrl+0, W | toolbar "fit width" (lit only at exactly the screen width) |
| Left/Right, Shift+wheel | horizontal drag when the strip is wider than the screen |
| G | toolbar "gaps" toggle |
| F11 / full screen command | none (always full screen) |
| pointer near edge shows toolbar | **tap** on the strip toggles the toolbar; it hides 3 s after the last interaction |
| Escape, Alt+Left, mouse back | system Back |

- Toolbar: bottom bar with page indicator "57 / 312" and scrubber (target page bubble while
  dragging, jump on release); top bar with back, title, go to page, fit width, gaps, save to
  library, first and last page. Both inset by safe areas.
- Double tap: not used (avoids a delay on every single tap).
- Screen stays on while the reader is shown and the last touch was less than 10 min ago
  (`KeepScreenOn`, default on).
- Failed page: in-slot message + Retry; rate limit: banner with waiting time and Retry; account
  status shown in the banner for sources that declare it.

## History, Library, Downloads

- History: list, newest first; swipe or long press → remove; menu → Clear history (confirmation).
- Library: search field, source and read filters, three sorts (menu), pull-to-refresh rescans;
  long press → delete (confirmation: "The file will be deleted from the device"), show folder name
  per card; unavailable folders' galleries greyed with the reason and an action ("Grant access",
  "Remove folder").
- Downloads: list with state and progress; Pause/Resume/Cancel per item; banner when notifications
  are not permitted, and when waiting for Wi-Fi.

## Download notification

Channel "Downloads" (low importance, no sound). One ongoing notification while the queue is active:
title = current gallery, text = "12 / 40" (+ "and 2 more queued"), determinate progress, actions
Pause / Resume; tap opens Downloads. Final notification on finish or stop by a limit (dismissible).
Strings come from `strings_android.xml`.

## Settings (Android differences)

| Section | Android |
|---------|---------|
| Language | the six languages + "System"; applied at once (activity recreated on the same screen) |
| Reader | preload 3–5 (default 3), fit width, reading width (applies when the screen is wider), gap, **keep screen on** |
| Cache | size limit (default 512 MB), Clear cache, **location**: Internal / SD card (shown only when a card is mounted), move progress |
| Library | folders added with the system picker; target; remove with Move / Keep files; per-folder state |
| Downloads | **Wi-Fi only** (default off) |
| Sources | as Windows: declared settings, E-Hentai account, sign in (in-app page), paste cookies, sign out |
| Start page, default searches | as Windows |

## Layout rules

- Reference width 360 dp, font scale up to 1.3: no clipped text in any language (SC-013). Long
  labels wrap; buttons use icons + text where space allows, icons + content descriptions otherwise.
- Touch targets ≥ 48 dp.
