# Tools

## `runner/` — source-runner

Runs source scripts outside the apps, inside QuickJS (WebAssembly) with a reference host that
implements the contract. Full reference: `specs/001-source-contract/contracts/runner-cli.md`.

Requires Node.js 22.18+. From the repository root:

```text
npm ci
npm run schema
npm run runner -- run sources/hitomi search '{"text":"female:glasses","filters":{"language":"en"}}' null
npm run runner -- test sources/hitomi
npm run runner -- check sources/hitomi
npm run runner -- contract-test
```

| Mode | Network | Politeness | Fixtures |
|------|---------|------------|----------|
| `--offline` (default) | none | skipped | replayed |
| `--live` | real | enforced | untouched |
| `--record` | real | enforced | written, sanitised |

Exit codes: `0` ok, `1` failures or violations, `2` usage error, `3` script error,
`4` unmatched request in offline mode.

`scripts/live-dimension-check.ts` verifies against the live site that page sizes returned by a
source match the images actually served (SC-006). It reads only image headers and stores nothing.
