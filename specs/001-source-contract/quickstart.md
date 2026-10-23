# Quickstart & Validation: Source Contract, Script Runner & Hitomi Source

Run-guide proving the feature end-to-end. Command details: [contracts/runner-cli.md](contracts/runner-cli.md).

## Prerequisites

- Node.js 22.18+ (LTS) (`node --version` → `v22.18` or newer)
- From repo root: `npm ci`

## 1. Contract and reference host (US1, US2 — SC-003, SC-004)

```text
npm run runner -- contract-test
```
Expected: every contract test passes; exit code 0.

```text
npm run runner -- check contract/conformance/broken-missing-function
npm run runner -- check contract/conformance/broken-wrong-shape
npm run runner -- check contract/conformance/broken-forbidden-capability
npm run runner -- check contract/conformance/broken-contract-version
```
Expected: each exits with code 1 and names its one violation (SC-003: 4 of 4).

```text
npm run runner -- check sources/hitomi
```
Expected: no violations; exit code 0.

## 2. Offline Hitomi suite (US3, US4 — SC-001, SC-002)

Disconnect from the network (or block it), then:

```text
npm run runner -- test sources/hitomi
```
Expected: all cases pass in < 30 s; exit code 0. Repeat 10× — identical output each time.

Spot checks:

```text
npm run runner -- run sources/hitomi search '{"text":"","filters":{"sort":"popular_week"}}' null
npm run runner -- run sources/hitomi getGallery '"<id from a fixture case>"'
```
Expected: a page of summaries with a `nextCursor`; a gallery whose every page has
`"size": "exact"`.

Negative check — a request without a recording:

```text
npm run runner -- run sources/hitomi getGallery '"1"'
```
Expected: exit code 4 and a message naming the unmatched request.

## 3. Live checks (US5 — SC-005, SC-006)

```text
npm run runner -- run sources/hitomi search '{"text":"","filters":{}}' null --live --verbose
```
Expected: results within 5 s; `--verbose` log shows request starts ≥ 100 ms apart and ≤ 4 in
flight.

SC-006: run the live dimension check script listed in tasks (20 random galleries: every page
image loads with the returned request and its decoded size equals the returned width/height).

## 4. Fixture hygiene (US5 — SC-007)

```text
npm run runner -- test sources/hitomi --case <name> --record
git diff --stat sources/hitomi/fixtures
```
Expected: offline replay of the recorded case passes; fixtures directory < 5 MB; no `image/*`
bodies; no `cookie`, `set-cookie` or `authorization` in `fixtures/index.json`.

## 5. Extensibility (SC-008)

Copy `contract/conformance/minimal` to a scratch `sources/<new-id>`, change `manifest.id`,
run `check` and `test` on it. Expected: works with no change to `tools/` or `contract/`.
