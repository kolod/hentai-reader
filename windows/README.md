# Windows app

C# + WinUI 3 (Windows App SDK, unpackaged, self-contained). Source scripts run in Jint with
AngleSharp for `html.select`; the host implements contract 1.2 and passes the same contract
tests and recorded source cases as the runner.

| Project | Purpose |
|---------|---------|
| `src/HentaiReader.Host` | Contract host: Jint engine thread, `http.request` (cookie jar, politeness, redirects), `html.select`, storage, sessions. No UI dependency. |
| `src/HentaiReader.App` | WinUI 3 shell: source list, search with declared filters and endless results, gallery details with page thumbnails, the reader, settings (language, reader, sources and sign-in), adult gate. |
| `tests/HentaiReader.Host.Tests` | Unit tests, contract suite, replay of every recorded source case, resource parity, plural rules, installer texts. |
| `installer/`, `build/` | Inno Setup script, installer texts in six languages, distribution rules, build and verification scripts (feature 004). |

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

## Install

Download `HentaiReader-Setup-<version>-x64.exe` and run it. The installer:

- asks whether to install **for you only** (default, no administrator rights, into
  `%LOCALAPPDATA%\Programs\Hentai Reader`) or **for all users** (administrator rights, into
  `%ProgramFiles%\Hentai Reader`); upgrades keep the earlier choice;
- speaks the Windows display language when it is English, German, French, Spanish, Japanese or
  Ukrainian (otherwise English); the language can be changed on the first page;
- upgrades an older version in place and refuses to replace a newer one;
- asks you to close Hentai Reader first if it is running.

Windows SmartScreen may warn that the installer is from an unknown publisher, because releases are
not code-signed yet: choose **More info → Run anyway**. On ARM64 Windows 11 the x64 build runs under
emulation.

Silent install (scripts, test machines): `HentaiReader-Setup-<v>-x64.exe /VERYSILENT /SUPPRESSMSGBOXES
/CURRENTUSER` (or `/ALLUSERS`); exit code 0 means success. All options and exit codes:
[specs/004-windows-installer/contracts/installer-cli.md](../specs/004-windows-installer/contracts/installer-cli.md).

Uninstall from Windows **Settings → Apps → Installed apps**. Your data folder (below) is kept; delete
it by hand to remove settings and site sign-ins.

## Portable build

`HentaiReader-<version>-x64-portable.zip` holds the same files as the installer. Unpack it anywhere
and run `HentaiReader.exe`; nothing is installed. It shares the data folder with an installed copy.

## Building the installer

```powershell
pwsh windows/build/Build-Installer.ps1 [-SmokeTest] [-BootstrapTools]
```

One command: Release build, `npm test` and `dotnet test`, self-contained publish, verification of
every published file against [installer/distribution-rules.json](installer/distribution-rules.json)
(no debug symbols, test files, fixtures or cookies; all six languages inside `HentaiReader.pri`;
bundled source scripts identical to `/sources`), Inno Setup compilation with a check of the packed
files, and the portable ZIP. `-SmokeTest` also installs, upgrades, repairs, tries a downgrade and
uninstalls on this machine (per user, temporary folders). Outputs and reports go to
`artifacts/installer/`; a failed run leaves no installer or ZIP. CI runs it on every push.

Needs Inno Setup 7.1.0 (pinned in [installer/tools.json](installer/tools.json)): `winget install
JRSoftware.InnoSetup.7`, or pass `-BootstrapTools` to download the pinned, hash-checked copy into
`.tools/`. Code signing: set `HENTAIREADER_SIGN_COMMAND` to a `signtool sign … $f` command line.
The app version is `<Version>` in [Directory.Build.props](Directory.Build.props).

## Reader

The reader replaces the gallery in the same window (Back, Alt+Left or the mouse back button
return to it with the search as it was). All pages form one strip, each exactly the column width;
slots are reserved from known or estimated sizes, so the strip never jumps.

| Key | Action |
|-----|--------|
| Up / Down | glide while held (eases in and out) |
| Space, Page Down / Shift+Space, Page Up | the same, faster |
| Home / End | first / last page |
| Ctrl+G | go to page |
| Ctrl+wheel, Ctrl+Plus / Ctrl+Minus | zoom around the pointer / in 10 % steps |
| W | fit width (100 % of the window width) |
| Ctrl+0 | default width from the settings |
| G | gaps between pages on / off (webtoons open without them) |
| F11 | borderless full screen (any screen); Escape leaves it in the reader |

A click on a thumbnail opens the reader at that page. Progress is saved per gallery and offered as
Continue. Failed pages show Retry in place; a stalled image (20 s) is requested again from the
source with a retry hint, up to twice. A rate limit stops all requests of that source until Retry.

**Sign-in (E-Hentai/ExHentai):** Settings → Sources → E-Hentai → Sign in (the site's own form in an
embedded WebView2 with a throw-away profile) or Paste cookies. Only the account cookies are kept,
encrypted, and sent only to the source's domains.

## Library and history

**Save to library** (gallery screen, reader toolbar) downloads every page through the source, with
the same politeness, retries and limits as reading; a rate limit or a sign-in requirement stops the
download and keeps its pages. Downloads are queued (one per source at a time), can be paused,
resumed and cancelled, and continue after a restart. Each saved gallery is one `.cbz` file (ZIP,
every entry stored, never recompressed) with `ComicInfo.xml` for other comic readers and
`hentai-reader.json` ([archive format 1](../contract/archive/README.md)); reading progress and the
gallery's zoom, width mode and gaps are written back into it when you leave the reader.

**Library** lists the saved galleries of all library folders (Settings → Library: add folders, choose
where new saves go, remove a folder moving its galleries or keeping the files). Archives copied into a
library folder by hand appear within seconds; comic archives from other programs are listed by file
name. **History** lists every gallery opened in the reader and continues it at the saved page with
its own zoom. Settings also choose the **start page** and the **page cache folder** (moved in the
background while you read); the star left of a source's search box saves that search as the source's
default.

## Data

Per user, in `%LOCALAPPDATA%\HentaiReader` (override with the `HENTAIREADER_DATA` environment
variable, used by tests; the default library folder is then `Library` inside it):

- `preferences.json` — UI language, adult confirmation, last source, start page, reader settings,
  source settings and default searches, page cache folder, window size and position
- `library.db` — SQLite: library folders, saved archives, history, reading progress, per-gallery
  reader settings, downloads (imports the 0.3 `progress.json` once)
- `covers/` — small cover thumbnails of history entries
- `cache/pages/` — page images of online reading (`index.json` + SHA-256 named files), size limit and
  folder in Settings
- `webview/` — the embedded sign-in browser's profile, deleted after each sign-in
- `sources/<id>/storage.json` — the source's `storage` values
- `sources/<id>/cookies.bin` — the source's cookie jar, encrypted with DPAPI for the current user

Nothing is written before the adult content confirmation.

## Languages

UI strings live in `src/HentaiReader.App/Strings/<tag>/Resources.resw` for en-US, de-DE, fr-FR,
es-ES, ja-JP and uk-UA. English holds every key (including every plural category any language
uses). Plural forms are `<Key>_<category>` chosen with the system ICU. The language can be changed
in Settings; the app restarts to apply it.
