# Implementation Plan: E-Hentai / ExHentai Source

**Branch**: `002-ehentai-source` | **Date**: 2026-10-23 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/002-ehentai-source/spec.md`

## Summary

Release contract **1.1** (backward compatible): source settings passed as a trailing argument to
1.1 sources only, an optional account-status capability, sprite-crop for image requests,
`asian_porn`, and the normative rule that hosts fetch images with the source's cookies and
limiter. Extend the runner accordingly. Then build `sources/ehentai`: listing links +
`gdata` for cards (2 requests per result page), `n:<gid>` cursors, toplists for popular, gallery
pages for thumbnail-cell size estimates, viewer pages for exact sizes and `nl` retry, original
images via `fullimg` when logged in, `RateLimited` on every limit signal, ExHentai via the `site`
setting, and a structure-reducing redactor so fixtures carry no gallery text or personal data.
Research: [research.md](research.md).

## Technical Context

**Language/Version**: JavaScript ES2020 module (source); TypeScript on Node.js 22.18+ (runner, as in 001)

**Primary Dependencies**: none new (QuickJS-WASM, cheerio, ajv from 001)

**Storage**: fixtures and snapshots; settings/cookies only in host memory per run

**Testing**: runner `test` / `check` / `contract-test`, `node:test` for runner changes, live checks

**Target Platform**: developer machines and CI (offline)

**Project Type**: contract change + CLI extension + source script

**Performance Goals**: result page ≤ 2 requests and ≤ 5 s live (SC-002); offline suite < 30 s (SC-001)

**Constraints**: politeness 500 ms / 2 in flight; fixtures < 5 MB, no images, no personal data;
Hitomi 0.1.0 unchanged and passing (SC-006)

**Scale/Scope**: contract +6 types/fields, runner +~6 files touched, 1 script (~700 lines),
~30 test cases incl. logged-in and ExHentai

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Constitution v1.0.2.

| # | Principle | Status | Evidence |
|---|-----------|--------|----------|
| I | Shared source logic | ✅ | All site logic in `sources/ehentai/ehentai.js`; apps untouched |
| II | Thin, sandboxed scripts; contract versioned, all hosts at once | ✅ | No new host capability (settings are an argument, R1); contract 1.1.0 + CHANGELOG + tag; the runner is today's only host and is updated in the same merge; Android/Windows will implement 1.1 from the start |
| III | Verified updates | ➖ | Unaffected (feature 006) |
| IV | Reading smoothness | ✅ | Estimated sizes from thumbnail cells at `getGallery`; exact sizes from the viewer page inside the preload window (1.0.1 clause); SC-003 live check |
| V | Open storage | ✅ | Gallery ids carry site + token; original vs resampled recorded per page size |
| VI | Respect for sources | ✅ | 500 ms / 2; every limit signal → `RateLimited`; cookies only to EH/EX domains; account status on demand only; images fetched through the same limiter (contract note, R2) |
| VII | Testability | ✅ | Offline suite incl. logged-in/ExHentai recordings; contract test for settings; arity unit tests |
| VIII | Simplicity | ✅ | No new dependencies; one script for both sites (setting, not a second source) |
| IX | Localisation | ✅ | Setting ids/values are contract vocabulary → labels in app strings; site content untranslated |
| X | Version control, fixture hygiene | ✅ | Redacted fixtures (structure-reduced site responses, no personal data); cookies file gitignored; tags `contract-v1.1.0`, `sources-ehentai-v0.1.0` |

**Gate result**: PASS (pre-research and post-design). No Complexity Tracking entries.

## Project Structure

### Documentation (this feature)

```text
specs/002-ehentai-source/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── contract-1.1.md        # delta to contract/host-api.d.ts
│   └── runner-cli-1.1.md      # delta to 001 runner-cli.md
├── checklists/requirements.md
└── tasks.md                   # /speckit-tasks
```

### Source Code (repository root)

```text
contract/
├── host-api.d.ts              # + settings, accountStatus, crop, asian_porn, image-fetch note
├── package.json               # 1.1.0
├── CHANGELOG.md
├── tests/settings-passing/    # new contract test
└── conformance/
    └── settings/              # 1.1 sample: settings + accountStatus (minimal stays 1.0)

tools/runner/src/
├── source.ts                  # settings resolution (defaults ∪ user values), contract-level arity
├── engine.ts                  # unchanged API; callers pass the right arity
├── commands/run.ts            # --setting, getAccountStatus
├── commands/check.ts          # accountStatus ⇔ export, settings rules
├── commands/contract-test.ts  # expect.json "settings"
├── cases.ts                   # case "settings"
└── schema.ts                  # AccountStatus result type
tools/runner/scripts/live-dimension-check.ts   # aspect tolerance for estimated sizes
tools/runner/test/settings.test.ts             # arity + settings merge

sources/ehentai/
├── ehentai.js
├── redact.mjs
├── tests/*.case.json, *.expected.json
├── fixtures/
└── CHANGELOG.md
```

**Structure Decision**: Same layout as 001; contract change and runner support land before the
source in the same feature branch, merged to `main` together (Constitution X).
