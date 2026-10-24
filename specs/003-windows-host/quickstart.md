# Quickstart & Validation: Windows App Shell and Source Host

## Prerequisites

- Windows 10 1903+ / 11, .NET 10 SDK, Windows App SDK workload (Visual Studio 2022/2026 with
  "WinUI application development", or the `Microsoft.WindowsAppSDK` NuGet package via CLI).
- Repository root; Node only needed for the runner comparisons below.

## 1. Parity (US1 — SC-001)

```text
dotnet test windows/tests/HentaiReader.Host.Tests
```

Expected: all `Contract.*` (15), all `Case.*` (Hitomi 18, E-Hentai 35, minimal 7, settings 6),
conformance and `Resources.*` tests pass. Cross-check: `npm test` still passes (runner unchanged
apart from the moved `host-glue.js`).

## 2. Run the app (US2, US3, US5)

```text
dotnet run --project windows/src/HentaiReader.App -p:Platform=x64
```

1. Fresh profile (delete `%LOCALAPPDATA%\HentaiReader`): the adult-content confirmation appears
   first; with a network monitor, no source request happens before confirming.
2. Pick Hitomi → front feed; search `female:glasses`, language English → results within ~5 s
   (SC-003); scroll through 3+ pages smoothly (SC-004).
3. Open filters on E-Hentai → only sort, type, minimum rating, pages, language are offered.
4. Open an E-Hentai gallery → one thumbnail per page, each showing its own page (SC-006); click a
   tag → search for it.

## 3. Languages (US4 — SC-005)

Settings → each of the six languages: the app restarts on the same source; every string
translated; check plurals ("1 сторінка / 2 сторінки / 5 сторінок"); no clipped text at 1024 px
width and 150 % scaling.

## 4. Portable build (FR-022)

```text
dotnet publish windows/src/HentaiReader.App -c Release -r win-x64
```

Copy the publish folder to another machine/folder without the SDK; it starts and works.
