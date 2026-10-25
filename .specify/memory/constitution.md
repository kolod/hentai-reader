<!--
Sync Impact Report
==================
Version change: 1.0.3 → 1.1.0
Bump rationale: MINOR — guidance of principle IV materially expanded: an optional, user-controlled
gap between pages is allowed; the strip stays seamless by default for continuous content.

Modified principles:
  - IV. Reading Smoothness First — "no padding and no visible seams" becomes: no padding around
    the strip and no visible seams; a fixed gap between pages MAY be shown as a user setting
    (on by default for page-by-page manga, off for continuous content such as webtoons), and
    it is part of the reserved layout, so it never makes the strip jump. Requested by the
    owner while testing specs/005-windows-reader.
Added sections: none
Removed sections: none
Templates requiring updates: none (templates do not restate the rule); specs/005-windows-reader
  spec.md FR-004 updated with the same feature.
Follow-up TODOs: none.

--- History ---
1.0.3 (2026-10-23): PATCH — records decisions already taken; no principle added or removed.

Modified principles:
  - I. Native UI, Shared Source Logic — Windows UI framework fixed to WinUI 3 (the choice the
    principle deferred to "the first Windows plan"; decided before it, together with building the
    Windows app before the Android app).
  - II. Thin, Sandboxed Source Scripts — the host API list now names `console` (debug log only)
    and `SourceError` (contract prelude), which 001's plan carried as justified exceptions.
Added sections: none
Removed sections: none
Templates requiring updates: none.
Follow-up TODOs: none.

1.0.2 (2026-10-23): PATCH — wording clarification; the rule's intent (no images, no personal
data) is unchanged.

Modified principles:
  - X. Version Control & Release Discipline — fixtures may contain any site response (HTML, JSON,
    scripts, binary index data), not only HTML/JSON; still never images, cookies, credentials or
    personal data; large binaries stored as requested byte ranges only. Needed for Hitomi
    (gg.js, galleries/{id}.js, .nozomi and galleriesindex ranges) — specs/001-source-contract.

1.0.1 (2026-10-23): PATCH — clarifications found while scoping the product
(specs/product-overview.md).

Modified principles:
  - II. Thin, Sandboxed Source Scripts — host API `http.get` clarified as `http.request`
    (GET/POST, headers, status, binary bodies); needed for Hitomi Range requests on .nozomi
    indexes and E-Hentai api.php/login POSTs.
  - IV. Reading Smoothness First — sources that expose exact dimensions only per page may reserve
    slots from an aspect estimate refined inside the preload window, with scroll anchoring.

1.0.0 (2026-10-23): initial ratification.

Modified principles (template slot → new title):
  - [PRINCIPLE_1_NAME] → I. Native UI, Shared Source Logic
  - [PRINCIPLE_2_NAME] → II. Thin, Sandboxed Source Scripts
  - [PRINCIPLE_3_NAME] → III. Verified Script Updates
  - [PRINCIPLE_4_NAME] → IV. Reading Smoothness First
  - [PRINCIPLE_5_NAME] → V. Open Storage Format
Added principles:
  - VI. Respect for Sources
  - VII. Testability
  - VIII. Simplicity
  - IX. Project Language & Localization
  - X. Version Control & Release Discipline
Added sections:
  - Platform & Distribution Constraints (was [SECTION_2_NAME])
  - Development Workflow & Quality Gates (was [SECTION_3_NAME])
Removed sections: none

Templates requiring updates:
  - ✅ .specify/templates/tasks-template.md — tests no longer blanket-optional (Principle VII);
       monorepo path conventions (Principle X); localization/CHANGELOG polish tasks (IX, X)
  - ✅ .specify/templates/plan-template.md — no change needed; "Constitution Check" gates are
       derived from this file by /speckit-plan
  - ✅ .specify/templates/spec-template.md — no change needed; no mandatory section conflicts
  - ⚠ README.md — does not exist yet; create when the monorepo skeleton is set up
  - ⚠ .specify/templates/commands/*.md — directory not present; nothing to check

Follow-up TODOs: none. Ratification date set to the date of first adoption (2026-10-23).
-->

# Hentai Reader Constitution

Hentai Reader is a pair of near-identical applications — one for Android, one for Windows — that
search, download, archive, and display manga from Hitomi.la and E-Hentai in a seamless,
infinite-scroll reader.

## Core Principles

### I. Native UI, Shared Source Logic

- The Android app MUST be written in Kotlin with Jetpack Compose. The Windows app MUST be
  written in C# with WinUI 3 (Windows App SDK).
- Each UI MUST be implemented separately and MUST follow its own platform's conventions; no
  cross-platform UI toolkit is used.
- Site logic (search, gallery metadata, page URLs) MUST NOT be implemented in Kotlin or C#. It
  lives exclusively in the JavaScript source scripts.

**Rationale**: Native UIs give the best feel and performance on each platform, while keeping site
logic in one place means a site change is fixed once, not twice.

### II. Thin, Sandboxed Source Scripts

- Source scripts MUST be written in JavaScript and run in QuickJS (Android) and Jint (Windows).
- A script knows only the structure of its site and returns data. It MUST NOT have direct access
  to the file system, the network, or the UI.
- Scripts obtain every capability exclusively through the host API: `http.request` (GET/POST,
  request headers, response status/headers, text or binary body), `html.select`, `json.parse`,
  `storage.get` / `storage.set`, `sleep`, plus `console` (writes only to the host's debug log)
  and the `SourceError` type defined by the shared contract prelude.
- HTML parsing MUST be performed by the host (Jsoup on Android, AngleSharp on Windows); the
  script receives CSS-selector results, never a DOM of its own.
- Every source MUST implement the same contract: `search(query, page)`, `getGallery(id)`,
  `getPageUrl(gallery, index)`.
- The host API contract MUST be described once, in a TypeScript declaration file (`.d.ts`) under
  `/contract`, which is the single source of truth for both platforms. Every contract change
  MUST be versioned and implemented on both platforms at the same time.
- Exception: the Hitomi source MAY execute the site's original `gg.js` inside the engine instead
  of re-implementing its logic by hand.

**Rationale**: A narrow, declared host API keeps scripts small, portable across two engines, and
unable to harm the user's device, while one contract file prevents the platforms from drifting.

### III. Verified Script Updates

- Source scripts MUST be updatable without releasing a new app version, from the `sources-dist`
  distribution repository, whose `index.json` lists each script's version and SHA-256.
- A script whose SHA-256 does not match `index.json` MUST NEVER be executed.
- Each app MUST ship a bundled copy of all scripts and fall back to it when an update is
  unavailable or fails verification.

**Rationale**: Sites change faster than apps are released; hash verification ensures remotely
delivered code is exactly what CI published, and the bundled copy keeps the app working offline.

### IV. Reading Smoothness First

Reading smoothness is the number-one UX priority.

- The page strip MUST have no padding around it and no visible seams between pages. A fixed gap
  between pages MAY be offered as a user setting: it MUST be part of the reserved layout (never
  a cause of jumps), MUST be switchable while reading, and continuous content (e.g. galleries
  tagged webtoon) MUST open without it.
- The width and height of every page MUST be stored in metadata BEFORE the image is loaded; space
  for each page MUST be reserved up front so the strip never jumps.
- Where a source exposes exact dimensions only per page (E-Hentai), the slot MAY be reserved from
  an aspect-ratio estimate (e.g. its thumbnail), provided the exact size is obtained while the
  page is still in the preload window, before it becomes visible. A correction above the
  viewport MUST NOT move the visible content (scroll anchoring).
- Images MUST be decoded at screen width, not at original resolution; 3–5 pages ahead MUST be
  preloaded.
- Networking, image decoding, and script execution MUST NEVER run on the UI thread.

**Rationale**: Layout shifts, seams, and stutter are the most visible defects of a reader; known
dimensions and off-thread work eliminate them by construction.

### V. Open Storage Format

- Each gallery MUST be stored as exactly one CBZ file (ZIP, STORED method, images never
  recompressed).
- Metadata (tags, page dimensions, reading progress) MUST be kept in the local database AND
  duplicated in a metadata file inside the archive, so every archive is self-contained.
- The CBZ layout and metadata schema MUST be identical on both platforms; an archive created on
  one platform MUST open on the other.
- When a source offers both WebP and AVIF, WebP MUST be preferred.

**Rationale**: An open, self-describing archive outlives the app, can be read by third-party
readers, and moves freely between devices; STORED avoids wasted CPU on already-compressed images.
WebP has broader, faster decoder support than AVIF on both target platforms.

### VI. Respect for Sources

- Every source MUST enforce delays between requests and a concurrency limit, both configurable in
  that source's script.
- Failed requests MUST be retried with exponential backoff. On rate-limit signals (E-Hentai image
  limits, HTTP 429/509) downloading MUST stop rather than be forced.
- Cookies and login data MUST be stored only locally and MUST be sent only to their own source's
  domain.

**Rationale**: Aggressive clients get users banned and burden the sites the app depends on;
scoping credentials per domain prevents leaking them to third parties.

### VII. Testability

- Source scripts MUST be testable independently of the apps, via the console runner in `/tools`
  using host-API stubs and recorded HTML/JSON fixtures.
- The Android and Windows host-API implementations MUST pass the same contract test suite from
  `/contract`.
- A source script MUST NOT be published to `sources-dist` unless its tests pass.

**Rationale**: Scripts are the part that breaks most often; fast, app-independent tests catch site
changes early, and shared contract tests guarantee identical script behavior on both engines.

### VIII. Simplicity

- No unnecessary dependencies and no abstractions "for the future". Adding a new source MUST mean
  adding a new script, with no changes to app code.
- Platform-specific code MUST exist only where it is unavoidable: UI, background downloads,
  storage.

**Rationale**: Two apps double the cost of every layer; keeping platform code minimal keeps the
pair maintainable by one person.

### IX. Project Language & Localization

- All project documentation MUST be in English: constitution, specs, plans, tasks, README, code
  comments, commit messages, the host-API contract, and source-script documentation.
- The UI MUST support 6 languages: English (`en`, base and fallback), German (`de`), French
  (`fr`), Spanish (`es`), Japanese (`ja`), Ukrainian (`uk`).
- No UI strings may be hard-coded: Android uses `strings.xml`, Windows uses `.resx`.
- String keys MUST be identical on both platforms; the English file is the single source of
  truth for keys. A missing translation MUST fall back to English automatically.
- Plurals MUST use the platform's plural mechanisms (Ukrainian has several plural forms,
  Japanese has none); no manual "1 item / N items" logic.
- Dates, numbers, and file sizes MUST be formatted according to the user's locale.
- The language MUST be selectable in app settings, independently of the system language.
- Fonts and layouts MUST render Japanese (CJK) text and long German strings correctly, without
  truncation.
- Source content (gallery titles, tags) MUST NOT be translated; it is shown as-is.
- A new language or new UI string MUST NOT ship in a release until translations exist for all 6
  languages (machine translation is acceptable as a draft).

**Rationale**: English docs keep the project accessible to tooling and contributors; shared keys
and platform-native localization keep both apps consistent and correct in every supported locale.

### X. Version Control & Release Discipline

- The project MUST be one monorepo with this layout:

  ```text
  /android   — Android app
  /windows   — Windows app
  /sources   — JS source scripts and index.json
  /contract  — host API description (.d.ts) and contract tests
  /tools     — console script runner
  /specs     — Spec Kit specifications
  ```

- `main` MUST always be in a working state: every commit on `main` builds and passes tests.
- All work MUST happen on feature branches (Spec Kit format `001-short-name`); merging into
  `main` only after tests pass.
- Commit messages MUST be in English, in Conventional Commits format with a component scope:
  `feat(android): ...`, `fix(sources/hitomi): ...`, `docs: ...`.
- One commit = one logical change. A host-API contract change and its implementations on both
  platforms MUST land in `main` together.
- Each component MUST be versioned independently with SemVer tags: `android-vX.Y.Z`,
  `windows-vX.Y.Z`, `contract-vX.Y.Z`, `sources-<source>-vX.Y.Z`.
- Source scripts are developed in the monorepo (`/sources`) and published for in-app updates to
  the separate `sources-dist` repository, which contains only built scripts and `index.json`.
- `sources-dist` MUST be updated only by CI from tagged commits on `main`; manual commits to it
  are forbidden. `index.json` (versions and SHA-256) is generated at publish time.
- The apps MUST download script updates exclusively from `sources-dist`.
- The repository MUST NEVER contain: signing keys (keystores, certificates), cookies, logins or
  passwords, local IDE settings, build artifacts, downloaded galleries or images.
- Test fixtures MUST contain only site responses (HTML, JSON, scripts, binary index data) and
  MUST NEVER contain images, cookies, credentials, or other personal data. Large binary
  resources are stored only as the byte ranges actually requested.
- `.gitignore` MUST cover Android, .NET, and Node from the first commit.
- A separate CHANGELOG MUST be kept per component and updated with every release.

**Rationale**: A single repo keeps contract, scripts, and both hosts in lockstep; CI-only
publishing and strict secret hygiene make the update channel trustworthy.

## Platform & Distribution Constraints

- Android builds are distributed as APKs via GitHub Releases (not Google Play). Windows builds
  are distributed as an installer and/or a portable `.exe`.
- The application is for personal use; its content is intended for adult users only.
- These constraints mean store policies do not apply, but they also mean the app itself is
  responsible for update integrity (Principle III) and signing-key hygiene (Principle X).

## Development Workflow & Quality Gates

- Every feature follows the Spec Kit flow (specify → plan → tasks → implement) on its own
  `NNN-short-name` branch.
- Merge gates for `main`:
  1. Both apps build (for every component touched).
  2. Source-script tests pass in the console runner against recorded fixtures (Principle VII).
  3. Host-API contract tests pass on both platforms whenever `/contract` or a host
     implementation changes (Principles II, VII).
  4. All UI strings exist in all 6 languages with identical keys on both platforms
     (Principle IX).
  5. No secrets, build artifacts, or images are committed (Principle X).
- Releases: tag the component, update its CHANGELOG; source releases are published to
  `sources-dist` by CI only.

## Governance

- This constitution supersedes all other project practices. Where another document conflicts
  with it, the constitution wins.
- Every specification and plan MUST be checked against these principles (the plan's
  "Constitution Check" gate). Any violation MUST be explicitly justified in the plan's
  Complexity Tracking table; unjustified violations block the plan.
- Amendments are made by editing this file on a feature branch, with a Sync Impact Report and
  propagation to dependent templates, and merged like any other change.
- Versioning policy (SemVer):
  - MAJOR — a principle is changed incompatibly or removed.
  - MINOR — a new principle or section is added, or guidance is materially expanded.
  - PATCH — clarifications, wording, typo fixes.
- Each amendment updates **Last Amended**; **Ratified** stays the date of original adoption.

**Version**: 1.1.0 | **Ratified**: 2026-10-23 | **Last Amended**: 2026-10-25
