# Quickstart: validate the Windows installer

Run from the repository root in PowerShell 7 on Windows 10 1903+ / 11 x64.

## Prerequisites

- .NET SDK per `global.json`, Node 22.18+ (for `npm test`)
- Inno Setup 7.1.0 in `C:\Program Files\Inno Setup 7`, or pass `-BootstrapTools` to download the
  pinned copy into `.tools/` ([tools.json](../../windows/installer/tools.json))

## 1. Build (User Story 2)

```powershell
pwsh windows/build/Build-Installer.ps1 -SmokeTest
```

Expected: exit code 0; `artifacts/installer/` holds `HentaiReader-Setup-<v>-x64.exe`,
`HentaiReader-<v>-x64-portable.zip`, `verification-x64.json` (`"passed": true`), `manifest-x64.txt`
and `summary.txt` (version, sizes, "unsigned", smoke test passed). Steps and outputs:
[contracts/build-installer.md](contracts/build-installer.md).

## 2. Fail closed (US2 scenario 4)

Each of these must stop the build with a message naming the check and leave no installer or ZIP:

| Change (temporary) | Expected message |
|--------------------|------------------|
| Drop a `test.pdb` into `artifacts/publish/win-x64` and run `Test-Distribution.ps1` directly | `Forbidden file: test.pdb (rule **/*.pdb)` |
| Remove `de-DE` from the app's `Strings/` | `pri-languages: missing de-DE` |
| Edit one byte of a script in the publish folder | `bundled-scripts: Sources/hitomi/hitomi.js differs from sources/hitomi/hitomi.js` |
| Set `<Version>` to `9.9.9` only for the installer (`/DAppVersion=9.9.9` mismatch) | `product-version` failure |

## 3. Install, upgrade, downgrade, uninstall (US1, US3, US4)

Manual check on a machine (or VM) without developer tools:

1. Run the installer with defaults → installer in the Windows display language; welcome page shows
   the adult notice; no UAC prompt; Start menu entry "Hentai Reader"; app starts and shows its own
   adult confirmation.
2. Confirm, change the UI language, open a source. Close the app.
3. Build a higher version (`<Version>` +1 patch) and install it over the first → no questions about
   scope; app starts in the chosen language without the adult confirmation (SC-004).
4. Run the lower-version installer → "A newer version … is already installed", nothing changes.
5. Start the app, run the installer again → asked to close the app.
6. Uninstall from "Installed apps" → program folder and Start menu entry gone;
   `%LOCALAPPDATA%\HentaiReader` still present (SC-005).
7. Repeat step 1 choosing "all users" → UAC prompt; installed under `%ProgramFiles%\Hentai Reader`.

## 4. Unattended (US5)

```powershell
.\HentaiReader-Setup-<v>-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /CURRENTUSER /LOG="$env:TEMP\hr-setup.log"
$LASTEXITCODE   # 0
```

Run a lower version silently → exit code 1. With the app running → exit code 1.

## 5. Size (SC-006)

`summary.txt`: installer size ≤ publish size × 1.1 (expected ≈ 45 MB vs ≈ 185 MB folder).

## 6. CI

A pull request shows the `installer` job green, with the installer, ZIP and reports attached as
artifacts.
