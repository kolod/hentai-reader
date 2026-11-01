# Contract: Android parity suite (FR-003, FR-004, SC-001)

`./gradlew :host:test` (from `android/`) runs without UI, network or a device. It is the same
procedure as the Windows suite ([003 parity-suite](../../003-windows-host/contracts/parity-suite.md)),
on the desktop build of the QuickJS sources and the JNI shim that ship in the APK
([research R2, R11](../research.md)).

## Engine under test

- QuickJS: Bellard 2025-09-13 (f1139494), the commit the runner's quickjs-emscripten 0.32 vendors.
  `android/quickjs/QUICKJS_VERSION` records it. A version check test fails when it differs from the
  runner's variant README.
- Evaluation order: `contract/prelude.js` (global), `contract/host-glue.js` (global, completion
  value = `bind`), the source (module). The files are read from the repository at test time, and
  packed into the APK from the same paths at build time. No copies are kept under `android/`.
- Limits: 60 s per call (interrupt deadline), 128 MiB (`JS_SetMemoryLimit`); storage key 1–128
  chars, value ≤ 1 MiB, ≤ 8 MiB per source; sleep 0–60 000 ms.

## 1. Host contract tests

One JUnit test per `contract/tests/<name>/`, named `Contract.<name>`: politeness enforced, offline
transport with `echo` and `delayMs`, cookie seeds, `other.js` for storage isolation, `settings` for
1.1 tests, then the `expect.json` assertions on the request log (`minGapMs` with 2 ms tolerance,
`maxInFlight`, `refusedUrls`, `cookies.sentTo/notSentTo`).

## 2. Source and conformance cases

For every `sources/*/` and `contract/conformance/*/` with `tests/`, each `*.case.json` becomes
`Case.<source>.<case>`:

- a fresh source session; offline transport on the case's pool (`fixtures/` or `fixtures/<pool>/`);
  politeness skipped; frozen clock = pool `recordedAt`; storage seed; settings merged over manifest
  defaults (1.1 and later);
- `@case` and `@case.path` references resolved in dependency order from earlier results or their
  snapshots;
- `expectError` passes if and only if a `SourceError` of that kind is thrown;
- otherwise the result must be JSON-deep-equal to `<name>.expected.json`.

Snapshots are never written by this suite; the runner owns them.

## 3. Conformance samples

`broken-forbidden-capability` fails with "forbidden capability: fetch"; `broken-contract-version`
is refused at load; `broken-login-page`, `broken-missing-function` and `broken-wrong-shape` fail
exactly as on Windows.

## 4. Android-specific guards

- `Html.JsoupOnlySelectors`: each selector in the R3 list (`:containsOwn`, `:matches`, …) is refused
  with `InvalidInput` before querying.
- `Html.DescendantsOnly`: `select(el, "*")` never returns `el` itself.
- `Html.TextSemantics`: block boundaries and `<br>` do not add spaces; NBSP is kept; `html()` is not
  pretty-printed. The fixture markup is shared with the runner's `html.test.ts` cases.
- `Engine.Interrupt`: an endless loop ends after the deadline with `ScriptError` and
  `details.limit = "time"`; the next call on the same session works.
- `Engine.Memory`: allocation beyond the limit gives `ScriptError` with `details.limit = "memory"`;
  the engine is recreated for the next call.

## 5. Resource key parity

- `Strings.Generated`: for every language, `strings_shared.xml` contains exactly the keys and plural
  families of the matching `.resw`. The generated files are up to date (the generator in `--check`
  mode is also a CI step).
- `Strings.Android`: every `values-<lang>/strings_android.xml` has exactly the keys of
  `values/strings_android.xml`; every `<plurals>` has `other`.

## Reporting

JUnit XML; CI fails on any failure. A failing case prints the first differing JSON path.
