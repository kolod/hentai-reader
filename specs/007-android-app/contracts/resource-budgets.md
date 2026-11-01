# Contract: resource budgets (FR-016 – FR-025, SC-005, SC-007, SC-011)

Fixed numbers the implementation and tests use. Values marked *tunable* may change after testing on
a phone, together with this file.

## Memory

| Item | Rule |
|------|------|
| Decoded-image budget | clamp(`ActivityManager.memoryClass` × 0.5 MiB, 64 MiB, 192 MiB) *tunable* |
| After `onTrimMemory(RUNNING_LOW)` or above | budget × 0.5 until the app is next shown |
| `onTrimMemory(UI_HIDDEN)` | release everything except the visible pages; thumbnails and covers off screen |
| Largest single decode | budget / 8; above that, decode at a lower scale or as tiles |
| Tiling threshold | a page taller than 2 × screen height at display scale, *tunable* |
| Tile size | screen width × 1/2 screen height at display scale |
| Release order | behind (farthest first) → ahead beyond 1 page + 1 screen (farthest first) → the rest ahead; visible never |
| Thumbnails and covers | decoded at cell size; in-memory LRU 16 MiB, shared |
| Source engines | 128 MiB limit each (contract); close after 2 min idle or on `RUNNING_LOW` |
| Target | app PSS < 400 MB while reading a 500-page gallery (SC-005) |

## Preload

Ahead of the view: max(`PreloadPages` (3–5, default 3), pages covering 1 screen height); behind:
1 page + ½ screen (005 FR-008). The budget may shrink "ahead" down to 1 page + 1 screen, never below.

## Storage

| Item | Rule |
|------|------|
| Page cache limit | default 512 MiB, range 128–10240 MiB |
| Free-space reserve | min(1 GiB, 10 % of the cache volume); below it, cache writes are skipped and a one-time notice is shown |
| Download staging | checked against the same reserve plus the gallery's expected size before start; stops with "not enough space" otherwise (006 edge case) |
| Covers | ≤ 300 px wide JPEG, quality 85 |

## Background, battery, data

| Item | Rule |
|------|------|
| Background work without downloads | none: no timers, polling, prefetch or engine kept busy (SC-007: 0 requests in 30 min) |
| Download service | runs only while the queue has `queued`/`downloading` items; stops itself when idle |
| Wi-Fi only (off by default) | downloads wait while the active network is metered |
| Network covers for history | off by default (`NetworkCovers`) |
| Animated images | animate only within 1 screen of the view |
| Keep screen on | only in the reader, released 10 min after the last touch |
| Re-fetch | a page in the cache or in a staged download is never requested again |

## Startup

Cold start to the start screen < 3 s on the reference phone (SC-002): the source engine of the start
source is created in parallel with the first composition; library scan and download resume start
after the first frame.
