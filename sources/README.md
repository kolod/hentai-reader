# Source scripts

One folder per source. Site logic lives only here (Constitution I); the apps run these scripts
through the host API in `contract/`.

```text
sources/<id>/
├── <id>.js          # one ES module: manifest, search, getGallery, getPageUrl[, suggestTags]
├── CHANGELOG.md     # tags: sources-<id>-vX.Y.Z
├── redact.mjs       # optional: replaces real gallery text in recorded fixtures (runner only)
├── tests/           # <name>.case.json + <name>.expected.json snapshots
└── fixtures/        # recorded site responses (no images, no cookies, redacted text)
```

Fixtures are public. A source whose responses carry gallery titles, tags or names provides
`redact.mjs` (`export function redact(url, text)`): an allowlist of what the tests need, everything
else replaced with neutral placeholders. Recording applies it automatically; `check` fails if any
stored body would change when redacted again; `source-runner redact` re-applies it after the rules
change.

## Settings, accounts and logged-in recordings (contract 1.1)

A 1.1 source may declare `manifest.settings` (e.g. E-Hentai's `image_quality`, `site`) and the
`accountStatus` capability. Recording logged-in cases needs a cookie file passed with `--cookies`;
the redactor must drop every page part that can show account data, and before committing, search
the fixtures and snapshots for your own member id and user name **in the terminal only** — never
write those values into a file in the repository.

## Adding a source

1. Copy `contract/conformance/minimal` to `sources/<id>/`, rename the script to `<id>.js`, set
   `manifest.id`, `domains` and `politeness`.
2. Explore with `npm run runner -- run sources/<id> <fn> … --live --verbose`.
3. Write `tests/*.case.json`, then `npm run runner -- test sources/<id> --record --update`.
4. Review the snapshots against what the site shows; commit script, cases, snapshots, fixtures.
5. `npm run runner -- check sources/<id>` and `npm run runner -- test sources/<id>` must pass —
   a script is never published to `sources-dist` otherwise (Constitution VII).

No app code changes are needed (Constitution VIII).
