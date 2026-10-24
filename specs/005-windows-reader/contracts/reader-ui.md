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
| F11 | toggle full screen (also hides navigation pane) | toggle full screen (pane stays) |

## Reader keys

| Key | Action |
|-----|--------|
| Space, Page Down | one screen down (screen height − 10 %) |
| Shift+Space, Page Up | one screen up |
| Down / Up | 10 % of a screen |
| Home / End | first / last page |
| Ctrl+G | go to page (dialog, accepts 1…pageCount) |
| Ctrl+Plus / Ctrl+Minus / Ctrl+0 | zoom in / out by 10 % / fit width (100 %) |
| Ctrl+wheel | zoom around the pointer |
| F11 | full screen |

## Toolbar (auto-hides after 2 s without pointer near the top or bottom edge)

Back · title · page indicator `"{n} / {total}"` · Go to page · zoom out / zoom value / zoom in ·
Full screen · account status (sources with `accountStatus`). Vertical scrubber at the right edge
shows the target page while dragging; release jumps.

## Page slot states (automation name `Page {n} of {total}`)

| State | Shown |
|-------|-------|
| Placeholder / Resolving / Downloading | neutral background of the expected size, small progress ring after 300 ms |
| Loaded | the image, exactly strip width |
| Failed | message + **Retry** button inside the slot |
| Limited (source) | banner above the strip: reason, waiting time if known, **Retry** |

## Settings additions

- **Reader**: Pages to preload (3–5), Maximum reading width (600–2000 px, step 100), Image cache
  limit (256 MB–10 GB) with current size and **Clear cache**.
- **Sources**: one expander per source with its declared settings; for `accountStatus` sources:
  state (signed in / signed out / unknown), image limit `{used} / {max}` and reset time when given,
  **Sign in** (embedded page, only with `loginPage`), **Paste cookies**, **Sign out**.
