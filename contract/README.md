# Host API contract

The single source of truth for what a host offers source scripts and what every source script
provides (Constitution II). Android, Windows and the console runner all implement it.

| File | Purpose |
|------|---------|
| `host-api.d.ts` | Host capabilities, source exports, data shapes, error kinds |
| `prelude.js` | `SourceError`, evaluated by every host before a source |
| `package.json` | `version` **is** the contract version (tag `contract-vX.Y.Z`) |
| `tsconfig.scripts.json` | Static check for `// @ts-check` source scripts |
| `schema/` | JSON Schemas generated from the `.d.ts` (`npm run schema`, not committed) |
| `tests/` | Portable host contract suite |
| `conformance/` | `minimal` (1.0) and `settings` (1.1) reference sources + one-defect samples |

## Versioning

A source declares `manifest.contract: "MAJOR.MINOR"`. A host supporting `H.h` runs it only if
`MAJOR == H` and `MINOR <= h`. Any change to `host-api.d.ts` bumps the version and is implemented
on every host in the same merge (Constitution II, X).

## Calling convention (1.1)

Sources declaring `"1.0"` are called exactly as in 1.0. Sources declaring `"1.1"` receive the
current **settings** (user values merged over `manifest.settings` defaults) as the last argument
of every function, and `null` for an absent retry hint. 1.1 also adds the optional
`getAccountStatus` capability, `ImageRequest.crop` for sprite thumbnails, the `asian_porn` type,
and the rule that hosts fetch images with the source's cookies and politeness limiter.

## Contract tests for hosts

Each `tests/<name>/` holds `test.js` (a source module exporting `manifest` and `run()`, which
throws on failure), `fixtures/` (recorded responses, plus `echo` and `delayMs` helpers) and
`expect.json` (assertions on the host's request log: `minGapMs`, `maxInFlight`, `refusedUrls`,
`cookies`, `other`, `settings` for 1.1 tests). A host passes when `run()` resolves inside its own engine and every
assertion holds. Format details: `specs/001-source-contract/contracts/fixtures-and-tests.md`.

Reference host: `npm run runner -- contract-test`.
