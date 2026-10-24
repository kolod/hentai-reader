# Contract: host bridge for `contract/host-glue.js`

`host-glue.js` (moved from `tools/runner/src/glue.js`, research R3) turns a private bridge object
into the contract globals. Every host — runner (QuickJS), Windows (Jint), later Android (QuickJS) —
implements this bridge and evaluates, in order: `contract/prelude.js` (global), `host-glue.js`
(global; its completion value is `bind(exports)`), the source (module). This is host-internal
plumbing, not contract surface: it can change without a contract version bump, but every host must
change with it in the same merge.

## Installation

Before evaluating `host-glue.js`, the host defines `globalThis.__hostBridge` with the members
below; the glue captures and deletes it. Optional `frozenNow: number` (ms since epoch) freezes
`Date` for offline replays.

## Envelopes

Every member returns (or resolves to) a JSON string `{"value": …}` or
`{"error": {"kind": "<SourceErrorKind>", "message": "…", "options": {…}?}}`. No exception crosses
the bridge. Transport failures that must abort the run (unmatched offline request, refused image
recording) are reported to the host's own call loop, and the envelope carries `Network`.

| Member | Kind | Arguments | Value |
|--------|------|-----------|-------|
| `http(meta, body?)` | async | `meta`: JSON `{method, url, headers, body (string), responseType, timeoutMs}`; `body`: ArrayBuffer for byte bodies | resolves to **object** `{env, bytes?}`; `env` value `{status, url, headers, text}` or `{status, url, headers, bytes: true}` with `bytes` an ArrayBuffer |
| `htmlSelect(markup, elementId, selector)` | sync | `elementId` −1 = parse `markup` as a document | `[{id, tag}]` |
| `htmlCall(op, elementId, arg)` | sync | `op` ∈ `text`, `ownText`, `html`, `outerHtml`, `attr` | string or null |
| `storageGet(key)` | async | | string or null |
| `storageSet(key, valueJson)` | async | `valueJson` = JSON string or `null` | null |
| `sleep(ms)` | async | | null |
| `log(level, text)` | sync | `log` / `warn` / `error` | (no envelope) |

## Invocation

The host calls the bound invoker `invoke(fn, argsJson)`; it returns a promise of a JSON string
`{"ok": true, "value": …}` or `{"ok": false, "error": {type, kind?, name?, message, …}}`.
`fn = "__describe"` returns `{manifest, functions}`. Arguments follow the contract calling
convention (1.0 vs 1.1) — the host builds them, the glue does not.

## Rules every host must share

- **Selector pre-check** — reject before querying, with `InvalidInput`:
  `:(contains|icontains|eq|gt|lt|first|last|even|odd|header|parent|selected|checkbox|radio|password|file|submit|reset|button|input|text|image|animated|hidden|visible)(?![\w-])`
  (case-insensitive).
- **Text normalisation** — collapse runs of `[ \t\n\r\f]` to one space and trim; keep NBSP.
- **Element selection** — descendants only, document order; handles valid until the current
  top-level call completes.
- **Headers** — response header names lower-case, repeated headers joined with `", "`,
  `set-cookie` never exposed.
- **Charset** — from `content-type`, default UTF-8.
- **Limits** — 60 s per call, 128 MiB; storage key 1–128 chars, value ≤ 1 MiB, ≤ 8 MiB per source;
  sleep 0–60 000 ms.
- **Classification of failures** — `ReferenceError: X is not defined` → `ScriptError`
  "forbidden capability: X"; limit hits → `ScriptError` with `details.limit` (`time` / `memory`).
