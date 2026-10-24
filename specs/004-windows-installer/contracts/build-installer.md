# Contract: `windows/build/Build-Installer.ps1`

The single documented command that builds, verifies and packages the Windows distribution
(FR-009).

## Usage

```powershell
pwsh windows/build/Build-Installer.ps1 [-SkipTests] [-SmokeTest] [-BootstrapTools]
                                       [-InnoSetupPath <path-to-ISCC.exe>] [-OutputDir artifacts/installer]
```

| Parameter | Default | Meaning |
|-----------|---------|---------|
| `-SkipTests` | off | Skip `npm test` and `dotnet test`. Local use only; CI never passes it; the report records it |
| `-SmokeTest` | off | Run `Test-InstallerSmoke.ps1` after packaging (installs and uninstalls on this machine, per user, temporary folders) |
| `-BootstrapTools` | off | Download Inno Setup from `windows/installer/tools.json`, verify SHA-256, install portably into `.tools/` |
| `-InnoSetupPath` | auto | Explicit `ISCC.exe`; otherwise `INNO_SETUP_PATH`, `.tools/`, then `Program Files\Inno Setup 7` |
| `-OutputDir` | `artifacts/installer` | Output folder; cleared at start |

Environment: `HENTAIREADER_SIGN_COMMAND` (optional, research R11).

## Steps (stop at the first failure)

1. Refuse a repository path longer than 120 characters; clear `-OutputDir` and `artifacts/publish`.
2. Read the version (`dotnet msbuild windows/Directory.Build.props -getProperty:Version`); locate
   `ISCC.exe`; check its engine version against `tools.json`; locate `makepri.exe` in the pinned
   `Microsoft.Windows.SDK.BuildTools` package.
3. `dotnet build windows/HentaiReader.slnx -c Release -p:Platform=x64`; unless `-SkipTests`:
   `npm test` and `dotnet test --project windows/tests/HentaiReader.Host.Tests -c Release --no-build`.
4. `dotnet publish windows/src/HentaiReader.App -c Release -r win-x64 -p:Platform=x64
   -o artifacts/publish/win-x64`.
5. Optional signing of `HentaiReader.exe`, `HentaiReader.dll`, `HentaiReader.Host.dll`.
6. `Test-Distribution.ps1 -Path artifacts/publish/win-x64 -Version <v>` (rules file, PRI languages,
   scripts vs `/sources`, PE machine, product version).
7. `ISCC windows/installer/HentaiReader.iss /DAppVersion=<v> /DSourceDir=… /O<OutputDir>`, then
   the manifest check (packed files = verified files, by relative path and SHA-256).
8. Zip the verified folder to `HentaiReader-<v>-x64-portable.zip`.
9. With `-SmokeTest`: `Test-InstallerSmoke.ps1 -Installer <installer> -Version <v>`.

## Outputs (`-OutputDir`)

| File | Content |
|------|---------|
| `HentaiReader-Setup-<v>-x64.exe` | Installer |
| `HentaiReader-<v>-x64-portable.zip` | Portable build |
| `verification-x64.json` | Verification report ([data model](../data-model.md#verification-report-artifactsinstallerverification-x64json)) |
| `manifest-x64.txt` | Files packed by the compiler |
| `summary.txt` | Version, file count, sizes (publish, installer, ZIP), signing state, skipped steps, smoke result |

## Failure behaviour

- Exit code 0 only when every step passed.
- On failure: non-zero exit code, a message naming the step and check (for example
  `Forbidden file: HentaiReader.pdb (rule **/*.pdb)`), the reports written so far, and **no
  installer and no ZIP** in `-OutputDir` (deleted if already produced).
