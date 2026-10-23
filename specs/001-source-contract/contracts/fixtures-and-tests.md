# Contract: Fixtures, Test Cases, Contract Tests

Platform-neutral data formats. The runner reads/writes them now; Android and Windows harnesses
replay the same files later.

## Source layout

```text
sources/<id>/
├── <id>.js                  # the source module
├── CHANGELOG.md
├── tests/
│   ├── <name>.case.json
│   └── <name>.expected.json # snapshot
└── fixtures/
    ├── index.json
    └── bodies/<sha256-16>.<html|json|js|bin|txt>
```

## `fixtures/index.json`

```json
{
  "format": 1,
  "recordedAt": "2026-10-24T10:00:00Z",
  "entries": [
    {
      "request": {
        "method": "GET",
        "url": "https://ltn.example/index-all.nozomi",
        "headers": { "range": "bytes=0-99" },
        "bodySha256": null
      },
      "response": {
        "status": 206,
        "headers": { "content-type": "application/octet-stream", "content-range": "bytes 0-99/11234560" },
        "body": "bodies/3f2a9c0d11e4b7a8.bin"
      }
    }
  ]
}
```

- **Match key**: `method` + full `url` + `range` header (if any) + SHA-256 of request body (POST).
  Duplicate keys are not allowed; recording replaces an existing entry.
- **Stored response headers** (allowlist): `content-type`, `content-range`, `content-length`,
  `location`, `retry-after`, `last-modified`, `etag`. `set-cookie` is never stored.
- **Never stored**: request `cookie`/`authorization` headers, image bodies (detected by
  `content-type: image/*` or magic bytes → recording fails loudly), personal data
  (Constitution X).
- **Large binaries**: only the requested byte ranges exist as bodies.
- `recordedAt` is the frozen clock for offline runs (`Date.now()` inside the engine).
- Body files are byte-exact (`.gitattributes`: `**/fixtures/** -text`).

## `tests/<name>.case.json`

```json
{
  "description": "US3-2: tag search with exclusion and language",
  "call": {
    "fn": "search",
    "args": [{ "text": "female:glasses -male:muscle", "filters": { "language": "en" } }, null]
  },
  "storage": { "gg:js": "…" },
  "expectError": null
}
```

- `fn`: `search` | `getGallery` | `getPageUrl` | `suggestTags`.
- `args`: JSON arguments; a string `"@<case-name>"` substitutes the snapshot result of another
  case (e.g. `getPageUrl` uses the `Gallery` from a `getGallery` case).
- `storage`: optional seed for per-source storage.
- `expectError`: `null`, or `{ "kind": "NotFound" }` — then no snapshot is written and the case
  passes iff a `SourceError` of that kind is thrown.
- Snapshot `<name>.expected.json` holds the exact result; also validated against the schema.

## Contract tests — `contract/tests/<name>/`

```text
contract/tests/<name>/
├── test.js        # ES module: export const manifest; export async function run()
├── fixtures/      # same format as above, plus "echo" responses
└── expect.json    # host-side assertions
```

- `run()` uses only host APIs and throws (any error) on failure; returning = pass for the
  script-side part.
- **Echo response**: `"response": { "echo": true }` → host returns `200` with a JSON body
  describing the request it actually sent (method, url, headers incl. cookies, body hash). Lets a
  script assert cookie scoping and header merging.
- **`expect.json`** — assertions the harness checks on its own request log:

```json
{
  "minGapMs": 200,
  "maxInFlight": 2,
  "refusedUrls": ["https://not-declared.example/"],
  "cookies": { "seed": { "a.example": "k=v" }, "sentTo": ["a.example"], "notSentTo": ["b.example"] }
}
```

Required coverage (FR-018): text and binary responses; POST with string and byte bodies;
Range requests; non-2xx without throw; redirect following; undeclared domain refused; cookie
domain scoping and merging; storage get/set/delete and isolation between two sources; `sleep`;
`html.select` on string and element, every supported selector form, forbidden pseudo-class
rejected; `json.parse`; `SourceError` construction and `instanceof`; politeness gap and
concurrency enforcement.
