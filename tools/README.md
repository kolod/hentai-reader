# Tools

## `runner/` — source-runner

Runs source scripts outside the apps, inside QuickJS (WebAssembly) with a reference host that
implements the contract. Full reference: `specs/001-source-contract/contracts/runner-cli.md`.

Requires Node.js 24+. From the repository root:

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

Contract 1.1 sources: `--setting id=value` (repeatable) on `run`; test cases may carry
`"settings": {…}`. Logged-in runs and recordings: `--cookies cookies-<name>.txt` (Netscape format;
`cookies*.txt` is gitignored and never committed). Runner delta for 1.1:
`specs/002-ehentai-source/contracts/runner-cli-1.1.md`.

`redact <sourceDir>` re-applies a source's `redact.mjs` to its recorded fixtures.

`scripts/live-dimension-check.ts [sourceDir] [galleries] [id=value …]` verifies against the live
site that page sizes match the images actually served (exact sizes exactly, estimated sizes within
2 % aspect). It reads only the first 64 KiB of each image and stores nothing.
