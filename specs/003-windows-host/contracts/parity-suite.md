# Contract: Windows parity suite (FR-008)

`dotnet test windows/tests/HentaiReader.Host.Tests` runs, without UI and without network:

## 1. Host contract tests

For every `contract/tests/<name>/`: same procedure as `source-runner contract-test`
(001 `contracts/fixtures-and-tests.md`, 002 additions): politeness enforced, offline transport
with `echo`/`delayMs`, cookie seeds, `other.js` for storage isolation, `settings` for 1.1 tests,
then the `expect.json` assertions on the request log (`minGapMs` with 2 ms tolerance,
`maxInFlight`, `refusedUrls`, `cookies.sentTo/notSentTo`). One xUnit test per directory.

## 2. Source and conformance cases

For every directory `sources/*/` and `contract/conformance/*/` that has `tests/`:

- each `*.case.json` becomes one test: fresh source session, offline transport on the case's pool
  (`fixtures/` or `fixtures/<pool>/`), politeness skipped, frozen clock = pool `recordedAt`,
  storage seed, settings merged over manifest defaults (1.1 only);
- `@case` / `@case.path` references resolved from earlier cases' results (dependency order) or
  their snapshots;
- `expectError` cases pass iff a `SourceError` of that kind is thrown;
- otherwise the result must be **JSON-deep-equal** to `<name>.expected.json`.

Snapshots are never written by the Windows suite — the runner owns them (`--update`). A difference
is a host defect or an engine incompatibility to fix in the host (or, if the script relies on
engine-specific behaviour, in the script, re-verified on both).

## 3. Conformance samples

`broken-forbidden-capability` must fail with "forbidden capability: fetch";
`broken-contract-version` must be refused at load.

## 4. Resource key parity

Every `Strings/<tag>/Resources.resw` has exactly the keys of `Strings/en-US/Resources.resw`; every
plural key family has `_other`. (Constitution IX.)

## Reporting

Test names: `Contract.<test>`, `Case.<source>.<case>`, `Resources.<language>`. CI fails on any
failure (FR-009).
