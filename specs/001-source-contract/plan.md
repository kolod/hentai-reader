# Implementation Plan: Source Contract, Script Runner & Hitomi Source

**Branch**: `001-source-contract` | **Date**: 2026-10-23 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/001-source-contract/spec.md`

## Summary

Define the versioned host/source contract once (`contract/host-api.d.ts`, contract 1.0.0), build a
Node-based console runner that executes source scripts inside QuickJS (WebAssembly) with a
reference host, offline fixture replay, live/record modes, snapshot test cases and a portable
host contract test suite, and implement the Hitomi source on top of it: front feed, popular,
tag/text search via `.nozomi` and B-tree indexes, exact page dimensions, and image URLs computed
with the site's own `gg.js`. Design decisions: [research.md](research.md).

## Technical Context

**Language/Version**: TypeScript on Node.js 22.18+ (LTS) (runner; native type stripping, no build);
JavaScript ES2020 ES modules (source scripts, contract tests)

**Primary Dependencies**: `quickjs-emscripten` (script engine), `cheerio` (HTML + CSS selectors),
`ajv` (schema validation); dev: `typescript` (`tsc --noEmit` for `@ts-check` scripts),
`ts-json-schema-generator` (schemas from the `.d.ts`)

**Storage**: Files only — fixtures (`sources/*/fixtures`), test snapshots; runner storage and
cookie jar are in-memory per run

**Testing**: `node:test` for runner internals; `source-runner test` (snapshot cases over
fixtures) for sources; `source-runner contract-test` for the host contract suite

**Target Platform**: Developer machines (Windows, Linux, macOS) and CI (Linux)

**Project Type**: CLI tool + contract package + script (no app UI in this feature)

**Performance Goals**: Offline Hitomi suite < 30 s (SC-001); live search page ≤ 5 s, gallery ≤ 3 s
(SC-005)

**Constraints**: Offline mode makes zero network calls; deterministic (frozen clock); fixtures
< 5 MB per source, no images/cookies (SC-007, Constitution X); scripts see only contract globals

**Scale/Scope**: 1 contract (~25 types), 1 runner (4 commands, 3 modes), 1 source script,
~15 contract tests, ~20 Hitomi test cases

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Constitution v1.0.2.

| # | Principle | Status | Evidence |
|---|-----------|--------|----------|
| I | Native UI, shared source logic | ✅ | No UI here; all Hitomi logic lives in `sources/hitomi/hitomi.js`, none in runner code |
| II | Thin, sandboxed scripts | ✅ (see Complexity Tracking) | Scripts run in QuickJS with only `http.request`, `html.select`, `json.parse`, `storage`, `sleep` (+ `console`, `SourceError`); host parses HTML; contract in one `.d.ts`, versioned 1.0.0; Hitomi evaluates original `gg.js` |
| III | Verified script updates | ➖ N/A | `sources-dist` publishing is feature 006; single-file module format (R2) keeps one hash per source |
| IV | Reading smoothness | ✅ | Contract carries per-page `width/height/size` and `PageImage.width/height` refinement; Hitomi returns `exact` |
| V | Open storage format | ✅ | `Page.fileName`, global id `(source, id)` prepared for CBZ; WebP preferred in Hitomi |
| VI | Respect for sources | ✅ | `politeness` in manifest enforced by host; `RateLimited` kind; cookies scoped to `domains`; undeclared domains refused |
| VII | Testability | ✅ | Runner + stubs + fixtures; portable contract tests; `test` exit status gates publishing |
| VIII | Simplicity | ✅ | 3 runtime deps; no build step; no bundler; one file per source; new source = new folder |
| IX | Language & localisation | ✅ | All docs/code English; filter ids/values are a contract vocabulary so UI labels stay in app string resources (R8); source content untranslated |
| X | Version control | ✅ | Monorepo paths respected; `contract-vX.Y.Z` / `sources-hitomi-vX.Y.Z` tags; CHANGELOGs for contract and hitomi; fixtures sanitised per 1.0.2; generated schema gitignored |

**Gate result (pre-research)**: PASS. **Post-design re-check**: PASS — design artifacts introduce
no new violations.

## Project Structure

### Documentation (this feature)

```text
specs/001-source-contract/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── host-api.d.ts          # design draft → becomes /contract/host-api.d.ts
│   ├── runner-cli.md
│   └── fixtures-and-tests.md
├── checklists/requirements.md
└── tasks.md                   # /speckit-tasks
```

### Source Code (repository root)

```text
package.json                   # npm workspaces: contract, tools/runner; script "runner"
package-lock.json

contract/
├── package.json               # version = contract version (1.0.0)
├── host-api.d.ts              # single source of truth
├── prelude.js                 # SourceError, evaluated before every source
├── tsconfig.scripts.json      # checkJs config for sources (lib: es2020 only, no DOM)
├── schema/                    # generated JSON Schemas (gitignored)
├── tests/<name>/              # portable host contract tests (test.js, fixtures/, expect.json)
├── conformance/
│   ├── minimal/               # smallest valid source (SC-008 template)
│   └── broken-*/              # one violation each (SC-003)
└── CHANGELOG.md

tools/runner/
├── package.json               # bin: source-runner
├── src/
│   ├── cli.ts                 # argument parsing, commands, exit codes
│   ├── engine.ts              # QuickJS context, module load, promise pump, limits, frozen clock
│   ├── host/
│   │   ├── http.ts            # request, politeness scheduler, domain check, cookie jar
│   │   ├── html.ts            # cheerio-backed select + element handles
│   │   ├── storage.ts
│   │   └── index.ts           # installs globals into the context
│   ├── transport/
│   │   ├── offline.ts         # fixture matching
│   │   ├── live.ts            # Node fetch
│   │   └── recorder.ts        # sanitising writer
│   ├── schema.ts              # ajv validation against generated schemas
│   ├── commands/{run,test,check,contract-test}.ts
│   └── diff.ts
└── test/                      # node:test unit tests for runner internals

sources/hitomi/
├── hitomi.js                  # ES module: manifest, search, getGallery, getPageUrl, suggestTags
├── tests/*.case.json, *.expected.json
├── fixtures/index.json, fixtures/bodies/*
└── CHANGELOG.md
```

**Structure Decision**: Monorepo layout from Constitution X. The contract package owns the
`.d.ts`, prelude, contract tests and conformance samples; the runner is a Node workspace under
`tools/`; the Hitomi source is self-contained data + one script under `sources/`. `.gitignore`
gains `contract/schema/`.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| `console` global beyond Principle II's capability list | Script authors need debug output; it writes only to the host's debug log and grants no I/O to the script | No logging makes `SiteChanged` investigations guesswork; routing logs through `storage` is a misuse |
| `SourceError` global from a shared `prelude.js` | Typed errors (FR-017) must be constructed identically on QuickJS and Jint | Plain thrown objects lose stack traces and `instanceof`; per-host error classes diverge |
| Node runner is a third host implementation (besides Android/Windows) | Constitution VII requires app-independent testing; QuickJS-in-WASM keeps engine parity with Android | Kotlin/JVM runner sharing Android host code: pulls Gradle/JVM into 001 and native QuickJS bindings for desktop are uncertain (R1) |

These are additions, not contradictions: no listed capability is removed or widened. If a later
amendment lists them in Principle II, this table entry can be dropped.
