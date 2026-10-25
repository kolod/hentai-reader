# Contract: Reader UI behaviour

User-facing behaviour the reader guarantees; UI Automation tests rely on the names below.

## Navigation in the main window

```text
SourcePage (cached) ──Read/Continue──▶ ... GalleryPage (cached) ──▶ ReaderPage
        ◀──────────── Back ─────────────          ◀──── Back ────
```

| Input | Reader | Other pages |
|-------|--------|-------------|
| Back button, Alt+Left, mouse back button | back to gallery | back (existing) |
| Escape | full screen → leave full screen; otherwise back to gallery | — |
| F11 | toggle full screen (also hides the toolbar; the pane is never shown in the reader) | toggle full screen (pane stays) |

## Reader keys

| Key | Action |
|-----|--------|
| Space, Page Down | fast glide down while held (7.1 column widths/s; eases in, eases out after release) |
| Shift+Space, Page Up | the same upwards |
| Down / Up | glide while held (2.1 column widths/s, so the same pages per second in any orientation and zoom; eases in, eases out after release) |
| Home / End | first / last page |
| Ctrl+G | go to page (dialog, accepts 1…pageCount) |
| Ctrl+Plus / Ctrl+Minus | zoom in / out by 10 % |
| Ctrl+0 | default width (fit width or reading width, from settings), 100 % |
| W | fit width (100 % of the window width) |
| G | gaps between pages on / off (only when the gap setting is above 0) |
| Ctrl+wheel | zoom around the pointer |
| F11 | full screen |

## Toolbar (auto-hides after 2 s without pointer near the top or bottom edge)

Back · title · page indicator `"{n} / {total}"` · Go to page · zoom out / zoom value / zoom in ·
Fit width (lit only at exactly the window width) · Gaps · Full screen · account status (sources with `accountStatus`). Vertical scrubber at the right edge
shows the target page while dragging; release jumps. The system scroll bars are hidden in the reader (the scrubber
replaces them; wheel, keys and touch still scroll).

## Page slot states (automation name `Page {n} of {total}`)

| State | Shown |
|-------|-------|
| Placeholder / Resolving / Downloading | neutral background of the expected size, small progress ring after 300 ms |
| Loaded | the image, exactly strip width |
| Failed | message + **Retry** button inside the slot |
| Limited (source) | banner above the strip: reason, waiting time if known, **Retry** |

## Gallery

A click on a page thumbnail opens the reader at that page.

## Settings additions

- **Reader**: Pages to preload (3–5), Maximum reading width (600–2000 px, step 100), Pages fill the
  window width (on/off, default on), Gap between pages (0–48 px, step 4, default 8; hint about G
  and webtoons), Image cache
  limit (256 MB–10 GB) with current size and **Clear cache**.
- **Sources**: one expander per source with its declared settings; for `accountStatus` sources:
  state (signed in / signed out / unknown), image limit `{used} / {max}` and reset time when given,
  **Sign in** (embedded page, only with `loginPage`), **Paste cookies**, **Sign out**.
