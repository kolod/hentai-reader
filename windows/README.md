# Windows app

C# + WinUI 3 (Windows App SDK, unpackaged, self-contained). Source scripts run in Jint with
AngleSharp for `html.select`; the host implements contract 1.1.1 and passes the same contract
tests and recorded source cases as the runner.

| Project | Purpose |
|---------|---------|
| `src/HentaiReader.Host` | Contract host: Jint engine thread, `http.request` (cookie jar, politeness, redirects), `html.select`, storage, sessions. No UI dependency. |
| `src/HentaiReader.App` | WinUI 3 shell: source list, search with declared filters and endless results, gallery details with page thumbnails, settings (language), adult gate. |
| `tests/HentaiReader.Host.Tests` | Unit tests, contract suite, replay of every recorded source case, resource parity and plural rules. |

## Prerequisites

- Windows 10 1903 (build 18362) or later, x64
- .NET SDK 10 (pinned by `global.json` at the repository root)

## Build, run, test

```sh
dotnet build windows/HentaiReader.slnx -c Release -p:Platform=x64
dotnet test --project windows/tests/HentaiReader.Host.Tests -c Release
dotnet run --project windows/src/HentaiReader.App -p:Platform=x64
```

Adding a new `Strings/<tag>/Resources.resw` is not picked up by an incremental build; rebuild with
`--no-incremental` once.

## Portable build

```sh
dotnet publish windows/src/HentaiReader.App -c Release -r win-x64 -p:Platform=x64 -o out/windows
```

The output folder runs from any location without installation (`HentaiReader.exe`). The built-in
source scripts are copied to `Sources/<id>/<id>.js`.

## Data

Per user, in `%LOCALAPPDATA%\HentaiReader` (override with the `HENTAIREADER_DATA` environment
variable, used by tests):

- `preferences.json` — UI language, adult confirmation, last source
- `sources/<id>/storage.json` — the source's `storage` values
- `sources/<id>/cookies.bin` — the source's cookie jar, encrypted with DPAPI for the current user

Nothing is written before the adult content confirmation.

## Languages

UI strings live in `src/HentaiReader.App/Strings/<tag>/Resources.resw` for en-US, de-DE, fr-FR,
es-ES, ja-JP and uk-UA. English holds every key (including every plural category any language
uses). Plural forms are `<Key>_<category>` chosen with the system ICU. The language can be changed
in Settings; the app restarts to apply it.
