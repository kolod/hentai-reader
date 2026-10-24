# Research: Windows App Shell and Source Host

Phase 0 output for [plan.md](plan.md). Library facts verified with Context7 (Jint, AngleSharp,
Windows App SDK docs) on 2026-10-23.

## R1. Runtime and project layout

**Decision**: .NET 10 (LTS, installed SDK 10.0.402), C# 14. Three projects under `windows/`:
`HentaiReader.Host` (class library: engine, bridge, HTTP, HTML, storage, transports — no UI
dependency), `HentaiReader.App` (WinUI 3 app), `HentaiReader.Host.Tests` (xUnit: unit tests +
parity suite). One solution `windows/HentaiReader.slnx`.

**Rationale**: The host is UI-free, so the parity proof (FR-008) runs headless with `dotnet test`
on any Windows CI runner, and Android later mirrors the same split.

**Alternatives**: one app project with test hooks (parity would need the UI framework to load);
a separate console parity tool (duplicates what the test project already gives).

## R2. Script engine: Jint 4

**Decision**: Jint 4.x, one `Engine` per source session, owned by a dedicated worker thread.
- Limits: `LimitExecutionTime(60 s)`, `LimitMemory(128 MiB)`, `ObserveCancellation(token)`,
  `Constraints.RegexTimeout` — contract limits (R7 of 001). Limits are cooperative; host callbacks
  are async so they never block the engine thread.
- Source module: `engine.Modules.Add("source", code)` + `engine.Modules.Import("source")` →
  module namespace object (exports).
- Host promises: `engine.Tasks.RegisterPromise()` → `ManualPromise` whose resolve/reject may be
  called from any thread; settlement is queued and applied on the engine thread. The worker thread
  pumps with `engine.Tasks.ProcessTasks()` / `WaitForScheduledWorkAsync` until the call's promise
  settles → many requests in flight at once (FR-004 of 001).
- Bytes: `engine.Intrinsics.Uint8Array.Construct(ReadOnlySpan<byte>)` / `ArrayBuffer.Construct(byte[])`
  into the engine; `JsValue.AsArrayBuffer()` out.
- CLR interop stays **off** (Jint default: no `AllowClr`), so scripts cannot reach .NET.
- One call at a time per source session (the contract allows a host to serialise); a new search
  cancels the previous call through its token.

**Rationale**: Every contract need has a direct Jint primitive; no experimental features
(`TaskInterop` is not needed because promises are created explicitly).

### R2a. Found while implementing (2026-10-23)

- The stable Jint is **4.17**; `ManualPromise` settled from other threads is a Jint 5 feature
  (`RegisterPromise` is experimental in 4.x and `RegisterPromiseWithClrValue` internal). The
  Windows host therefore creates bridge promises **in JavaScript**: a small adapter
  (`windows/src/HentaiReader.Host/Engine/jint-bridge.js`) wraps synchronous native "start"
  functions in `new Promise`, and .NET completions are posted to the engine's own thread, where
  `settle(id, value)` resolves them. Only stable public APIs are used (`ClrFunction`, `Evaluate`,
  `Invoke`, `Modules`, `Advanced.ProcessTasks`).
- Binary bodies cross as base64 (into JS) and number arrays (out of JS); both are decoded on the
  engine thread by the adapter.
- **Parity result: no engine differences.** The first run of the parity suite passed every
  contract test (14) and every recorded case (Hitomi 18, E-Hentai 35, minimal 7, settings 6),
  deep-equal to the runner's snapshots; a deliberate snapshot change was detected. No source
  script needed changes for Jint.

## R3. One shared host shim (`glue.js`) for every host

**Decision**: Move the runner's `glue.js` to `contract/host-glue.js` and use it unchanged in the
Windows host (and later Android). Each host implements only the small private bridge it expects
(`http`, `htmlSelect`, `htmlCall`, `storageGet`, `storageSet`, `sleep`, `log`, `frozenNow`) with the
JSON-envelope protocol documented in [contracts/host-bridge.md](contracts/host-bridge.md).

**Rationale**: The contract globals (`http.request` shaping, element handles, `SourceError`
mapping, frozen clock, the invoke/describe protocol) are then literally the same code on every
engine — the biggest single lever for "identical results" (SC-001). Host code shrinks to I/O.

**Alternatives**: re-implement the globals natively per host (three diverging copies of the same
semantics). The shim is not contract surface: changing it does not bump the contract version.

## R4. HTML: AngleSharp

**Decision**: AngleSharp `HtmlParser` for documents; element handles kept in a per-call table
(released when the call ends, as in the runner); `QuerySelectorAll` on documents and elements.
AngleSharp supports `:has()` (and Selectors 4 `:is`/`:where`), **but also the non-standard
`:contains`** → the same forbidden-pseudo-class pre-check as the runner (shared regex in the
bridge doc) runs before every query. `text()`/`ownText()`: `TextContent` with ASCII-whitespace
collapsing (contract rule), never `InnerText`.

## R5. HTTP, cookies, politeness

**Decision**: `HttpClient` with `UseCookies = false`, `AllowAutoRedirect = false`, automatic
decompression; ports of the runner's `CookieJar` (domain/path, Set-Cookie, script-header merge)
and `Scheduler` (min interval between starts, max in flight) — one per source session, shared by
script requests **and image requests** (contract 1.1 image rule). Redirects followed by the host
with domain re-check, max 5. Offline transport = C# port of the runner's fixture matcher (method +
URL + range + body SHA-256, `echo`, `delayMs`, pools).

## R6. Localisation

- **Resources**: `.resw` per language under `HentaiReader.App/Strings/<tag>/Resources.resw`
  (en-US is the key source of truth); XAML via `x:Uid`, code via `ResourceLoader`.
- **Language override**: `Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride`
  set at startup before any resource loads; unpackaged apps do not persist it → stored in app
  preferences and re-applied each launch. Changing it at runtime only affects resources loaded
  afterwards → the app restarts itself (`AppInstance.Restart`) and reopens the last source (spec
  US4-2 amended).
- **Plurals** (Constitution IX "platform mechanisms"): `.resw` has no plural support and .NET has
  no public plural API, so the app calls the **Windows system ICU** (`icu.dll`, `uplrules_open` /
  `uplrules_select`, Windows 10 1903+) to get the CLDR category, then looks up
  `<Key>_<category>` (`_zero`, `_one`, `_two`, `_few`, `_many`, `_other`, matching Android's
  `<plurals>` quantities). Missing category → `_other`. Minimum OS raised to 1903.
- **Formatting**: `CultureInfo` of the chosen language for numbers/dates/sizes.
- **Key parity**: a unit test asserts every non-English `.resw` has exactly the English keys and
  every plural key has at least `_other` (missing translations fail CI — Constitution IX release
  rule; machine-translated drafts allowed).

## R7. Packaging

**Decision**: Unpackaged, self-contained (`WindowsPackageType=None`,
`WindowsAppSDKSelfContained=true`, `SelfContained=true`, `RuntimeIdentifier=win-x64`) → a portable
folder from `dotnet publish`, zipped. The installer (e.g. Inno Setup over the same folder) is
produced with the first public release; FR-022 amended.

**Rationale**: No MSIX signing certificate needed (Constitution X forbids committing keys; a
self-signed cert would just be friction), runs from any folder, matches "portable exe".

## R8. Images and sprite crop

**Decision**: An app-side `ImageLoader` fetches bytes through the source session (cookies, headers,
limiter), decodes with `BitmapImage.SetSourceAsync` and `DecodePixelWidth` (covers/thumbnails
decoded at display size), and keeps an in-memory LRU (≈ 150 MB budget). Sprite thumbnails: the
sprite is decoded **once** and shared; each cell is shown by placing the full image inside a
clipped container (`Clip` = cell rect, image offset by −x/−y, scaled by a `Viewbox`) — no per-cell
re-encode.

## R9. UI structure

**Decision**: Single window, `NavigationView` (source list as menu items, Settings footer) +
`Frame` with pages: `SourcePage` (search box, filter flyout built from the manifest, results
`ItemsView`/`ItemsRepeater` with a `UniformGridLayout`, incremental loading at 80 % scroll),
`GalleryPage` (metadata, tag groups as buttons, thumbnail grid), `SettingsPage` (language),
`AdultGateDialog` (`ContentDialog` before the first source call). MVVM with
`CommunityToolkit.Mvvm` (source generators; the only extra dependency).

## R10. CI

**Decision**: Add a `windows` job (`windows-latest`, .NET 10): `dotnet build` the solution (x64)
and `dotnet test windows/tests/HentaiReader.Host.Tests` (parity suite + unit tests + resource key
parity). Path filter: `windows/**`, `contract/**`, `sources/**`. The existing Linux job is
unchanged.
