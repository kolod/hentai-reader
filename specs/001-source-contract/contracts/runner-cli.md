# Contract: `source-runner` CLI

Console runner in `tools/runner`. Invoked as `npm run runner -- <command> …` from the repo root
(bin name `source-runner`). All output is human-readable; `--json` prints machine-readable JSON.

## Commands

| Command | Purpose | Spec |
|---------|---------|------|
| `run <sourceDir> <fn> [args…]` | Call one source function and print the result | FR-020 |
| `test <sourceDir>` | Run all test cases of a source | FR-025 |
| `check <sourceDir>` | Conformance check (static + runtime) | FR-019 |
| `contract-test` | Run the host contract suite against the runner's host | FR-018, FR-021 |

### `run`

```text
source-runner run sources/hitomi search '{"text":"female:glasses","filters":{}}' null
source-runner run sources/hitomi getGallery '"1234567"'
source-runner run sources/hitomi getPageUrl @gallery.json 0
source-runner run sources/hitomi suggestTags '"fem:gla"'
```

- Arguments are JSON values; `@file.json` reads a JSON file.
- Result is validated against the contract schema before printing; a violation fails the run.

### `test`

```text
source-runner test sources/hitomi [--case <name>] [--update]
```

- Runs every `tests/*.case.json`; compares with `tests/<name>.expected.json`.
- Prints pass/fail per case and a structural diff on mismatch.
- `--update` rewrites snapshots for the selected cases (review the diff in Git).

### `check`

```text
source-runner check sources/hitomi
```

1. Static: `tsc --noEmit` with `checkJs` against `contract/host-api.d.ts`.
2. Runtime: script loads; `manifest` matches schema; `contract` version supported; required
   exports present; `suggestTags` present iff declared.
3. Reports each violation as `<function or manifest>.<field path>: <problem>`.

### `contract-test`

```text
source-runner contract-test [--case <name>]
```

Runs every `contract/tests/<name>/` against the runner's reference host, with politeness
enabled (timing assertions need it).

## Modes (for `run` and `test`)

| Flag | Network | Politeness delays | Fixtures |
|------|---------|-------------------|----------|
| *(default)* `--offline` | none; unmatched request = failure naming it | skipped | read |
| `--live` | real | enforced | untouched |
| `--record` | real | enforced | written (sanitised) |

## Other options

| Option | Meaning |
|--------|---------|
| `--storage <file.json>` | Seed per-source storage (`{ "key": "value" }`); default empty |
| `--cookies <file>` | Seed the cookie jar in `--live`/`--record` (file must be gitignored, `cookies*.txt`) |
| `--json` | Machine-readable output |
| `--verbose` | Print every host call (requests, storage) and script console output |

## Exit codes

| Code | Meaning |
|------|---------|
| 0 | Success / all cases passed |
| 1 | Test failures or conformance violations |
| 2 | Usage error (bad arguments, missing files) |
| 3 | Script raised an error during `run` (kind printed) |
| 4 | Unmatched request in offline mode |
