# Research: Windows Installer

Resolves the technical unknowns of [plan.md](plan.md). It reuses the research of the reference
installer (llama-cpp-gui `specs/003-installer/research.md`, Inno Setup, 2026-10-18/19) wherever
the facts are the same, and records where Hentai Reader differs. Measurements: 2026-10-24,
`dotnet publish windows/src/HentaiReader.App -c Release -r win-x64 -p:Platform=x64` of `024b411`.

## App facts that constrain the choice

- Unpackaged WinUI 3 app (`WindowsPackageType=None`), self-contained .NET 10 + Windows App SDK
  2.5.1, x64 only, Windows 10 1903+ (`TargetPlatformMinVersion` 10.0.18362.0).
- Data in `%LOCALAPPDATA%\HentaiReader` (`preferences.json`, `sources/<id>/storage.json`,
  `sources/<id>/cookies.bin` DPAPI-encrypted); nothing is written next to the executable.
- Bundled source scripts are copied to `Sources/<id>/<id>.js` in the output and loaded from there.
- UI strings are compiled into `HentaiReader.pri` (no loose `.resw` in the output).
- No icon of its own yet (Windows shows the default executable icon); no third-party notices file.
- No version property yet: assemblies report `1.0.0`, while the tag `windows-v0.1.0` exists.
- No administrator rights needed for anything the app does.

## R1. Installer technology (FR-001)

- **Decision**: **Inno Setup 7.1.0**, the same pinned version as the reference
  (`JRSoftware.InnoSetup.7`, SHA-256 `0362a383…df2f`, signer Pyrsys B.V.); already installed on the
  development machine in `C:\Program Files\Inno Setup 7`. One installer `.exe` (x64).
- **Rationale**: the comparison in the reference holds unchanged for this app — Inno Setup is the
  only option that gives the install-time scope dialog (`PrivilegesRequired=lowest` +
  `PrivilegesRequiredOverridesAllowed=dialog`, `UsePreviousPrivileges=yes`), official translations
  of all six languages with UI-language detection and a language dialog, in-place upgrades by a
  fixed `AppId`, a documented silent mode with exit codes (basis for later self-update), and a
  text script compiled from the command line in CI. Inno Setup 7 still supports Windows 10 1903
  targets (`MinVersion=10.0.18362`).
- **Alternatives considered** (from the reference; reasons unchanged):

| Option | Why not |
|--------|---------|
| Velopack | Per-user only `Setup.exe`; its MSI is English-only (fails FR-025); its uninstaller deletes `%LocalAppData%\{packId}` (data-loss risk). |
| WiX (MSI) | Verbose per-component authoring, `.wxl` per language, no benefit for this app. |
| MSIX | Makes the app packaged: AppData virtualized, data folder moves, trusted certificate needed. |
| Portable ZIP only | No Start menu / "Installed apps" entry, no upgrades — kept as the second form (FR-008), not as the installer. |

## R2. Publish output and files that must not be distributed (FR-004…FR-008)

- **Measured** (win-x64 today): 527 files, 232.6 MB.
  - Debug symbols: `HentaiReader.pdb`, `HentaiReader.Host.pdb` (small, but forbidden).
  - Unused Windows App SDK components, as in the reference: `onnxruntime.dll` (21 MB),
    `DirectML.dll` (18 MB), `Microsoft.ML.OnnxRuntime.dll`, `Microsoft.Windows.AI.*`,
    `Microsoft.Asg.*`, `Microsoft.Windows.Search.*`, `Microsoft.Windows.SemanticSearch*`,
    `Microsoft.Windows.Widgets.*`, `Microsoft.Windows.Workloads.*`, `workloads*.json`.
  - `createdump.exe`.
  - 172 `.mui` files (WinUI's own strings in 86 locales).
- **Decision** (the reference's proven approach):
  1. Remove `.pdb` files and `createdump.exe` from `ResolvedFileToPublish` in the app project
     (developers keep symbols in `bin/`).
  2. `PackageReference ... ExcludeAssets="all"` for `Microsoft.WindowsAppSDK.AI`, `.ML`, `.Search`,
     `.Widgets` and `Microsoft.Windows.AI.MachineLearning` (exclusions do not flow to dependencies,
     so the last one is needed separately). Versions are those resolved today, pinned explicitly.
  3. Keep all `.mui` locales (1–2 % of the size; WinUI chooses them by the OS fallback chain).
- **Expected**: ≈ 460 files, ≈ 185 MB uncompressed; installer ≈ 45 MB with LZMA2 (to be measured;
  SC-006).
- **Alternatives considered**: replacing the `Microsoft.WindowsAppSDK` meta-package by its component
  packages (Foundation, WinUI, DWrite, InteractiveExperiences, Base, Runtime) — cleaner, and possible
  here because nothing else depends on the meta-package, but unproven for self-contained deployment
  and more versions to keep in step; may be revisited when the SDK is updated. Trimming is not
  supported for WinUI projections.

## R3. Distribution rules and verification (FR-007, FR-011)

- **Decision**: `windows/installer/distribution-rules.json`
  ([schema](contracts/distribution-rules.schema.json)) with:
  - `allowedExtensions` (closed): `.dll`, `.exe`, `.winmd`, `.pri`, `.mui`, `.json`, `.js`, `.png`,
    `.ico`, `.html`, `.md`.
  - `forbidden` globs: `**/*.pdb`, `**/*.xml`, `**/*Tests*.dll`, `**/xunit*.dll`,
    `**/Microsoft.Testing.*.dll`, `**/createdump.exe`, `**/onnxruntime*.dll`, `**/DirectML.dll`,
    `**/Microsoft.Windows.AI.*`, `**/Microsoft.Asg.*`, `**/workloads*.json`, `**/ref/**`, `**/obj/**`,
    and project-specific: `**/fixtures/**`, `**/*.fixture.json`, `**/cookies*`, `**/*.har`,
    `**/preferences.json`, `**/storage.json`.
  - `required`: `HentaiReader.exe`, `HentaiReader.dll`, `HentaiReader.Host.dll`,
    `HentaiReader.runtimeconfig.json`, `HentaiReader.deps.json`, `HentaiReader.pri`,
    `Sources/{source}/{source}.js` for every folder under `/sources` that holds a script,
    `Assets/AppIcon.ico`, `THIRD-PARTY-NOTICES.md`, `coreclr.dll`, `Microsoft.ui.xaml.dll`,
    `Jint.dll`, `AngleSharp.dll`.
  - `languages`: the six tags; checked inside `HentaiReader.pri`, because `.resw` files are compiled
    into it: the verifier dumps the PRI with `makepri.exe dump` from the pinned
    `Microsoft.Windows.SDK.BuildTools` package (NuGet cache) and requires a `Language` qualifier
    for each tag on `Resources/App_Title`.
  - Bundled scripts must be byte-identical to `/sources/<id>/<id>.js` (SHA-256), so a stale copy
    cannot ship.
- **Verifier** `windows/build/Test-Distribution.ps1`: rules above, PE machine of every native file
  `0x8664` (IL-only assemblies accepted), product version of `HentaiReader.exe` (part before `+`)
  equal to the app version. Writes `verification-x64.json`.
- After compiling, Inno's `OutputManifestFile` lists the packed files with SHA-256; the verifier
  checks it equals the verified folder (reference: format confirmed with Inno 6.7.3 and 7.1.0).
- The portable build is the same verified folder, zipped as `HentaiReader-<v>-x64-portable.zip`
  (FR-008), so both forms share one verification.

## R4. Running app during install, upgrade, uninstall (FR-019)

- **Decision**: the app creates named mutexes `HentaiReader.Running` and `Global\HentaiReader.Running`
  at startup (no single-instance enforcement); the installer declares
  `AppMutex=HentaiReader.Running,Global\HentaiReader.Running`. Inno asks the user (in the installer
  language) to close the app and refuses in silent mode (exit code 1, measured in the reference).
- **Alternatives considered**: Restart Manager `CloseApplications=yes` (closes the app without its
  own shutdown; nothing to lose today, but later downloads would be cut off).

## R5. Upgrade, repair, downgrade (FR-017, FR-020)

- **Decision**: fixed `AppId` GUID; same `AppId` upgrades in place; same version repairs. A Pascal
  `InitializeSetup` compares the installed `DisplayVersion` with the installer version and refuses a
  downgrade with the localized `NewerVersionInstalled` message (exit 1 when silent). The installer
  has no entries for `%LOCALAPPDATA%\HentaiReader`, so upgrades never touch user data.
- Stale files of older versions stay in the program folder (Inno does not delete them; deleting
  `{app}\*` is unsafe for a user-chosen folder). Harmless for .NET (loads by `deps.json`), **but not
  for source scripts**: the app loads every `Sources/*/*.js`, so a source removed in a newer version
  would keep running from a leftover file. **Decision**: `[InstallDelete] Type: filesandordirs;
  Name: "{app}\Sources"` — the folder belongs to the installer only and is rewritten on every
  install.

## R6. Install location, scope, uninstall (FR-002, FR-016, FR-018)

- **Decision**: `DefaultDirName={autopf}\Hentai Reader` → `%LOCALAPPDATA%\Programs\Hentai Reader`
  (current user) or `%ProgramFiles%\Hentai Reader` (all users); never the data folder
  (`%LOCALAPPDATA%\HentaiReader`, a different name and parent). Start menu shortcut always; desktop
  shortcut as an unchecked task. Uninstall removes only installed files, shortcuts, the uninstall
  entry and `{app}\Sources`; no `[UninstallDelete]` for data. The last uninstall page shows
  `DataKeptNotice` with the data folder path (interactive only).
- With all users, each Windows user keeps their own data folder.

## R7. Supported systems (FR-021)

- **Decision**: `MinVersion=10.0.18362`; `ArchitecturesAllowed=x64compatible`,
  `ArchitecturesInstallIn64BitMode=x64compatible`. Inno shows its localized messages for older
  Windows. On ARM64 Windows 11 the x64 build runs under emulation; allowed (`x64compatible`), the
  README notes it. (The reference's `WrongArchitecture` message is not needed: there is no second
  installer to point to.)

## R8. Installer texts (FR-024…FR-026)

- **Decision**: official Inno message files (`Default.isl`, `Languages\German.isl`, `French.isl`,
  `Spanish.isl`, `Japanese.isl`, `Ukrainian.isl`) plus `windows/installer/Messages/{en,de,fr,es,ja,uk}.isl`
  with the project's `[CustomMessages]`; `LanguageDetectionMethod=uilanguage`,
  `ShowLanguageDialog=yes`, `UsePreviousLanguage` default. The adult content notice goes into the
  welcome page text (`WelcomeLabel2` override per language) so it is shown before anything else.
- **Test**: `windows/tests/HentaiReader.Host.Tests/Resources/InstallerMessagesTests.cs` — same keys in
  all six files, no empty values, same `%n` placeholders, values identical to English only if
  listed in `Messages/identical-translations.txt`.

## R9. Version, reproducibility, tools (FR-009, FR-013)

- **Decision**:
  - Single version source: `<Version>` in `windows/Directory.Build.props`, read with
    `dotnet msbuild -getProperty:Version`; passed to `ISCC /DAppVersion=…`; file names
    `HentaiReader-Setup-<v>-x64.exe` and `HentaiReader-<v>-x64-portable.zip`. This feature sets it to
    `0.2.0` (installer = new feature after `windows-v0.1.0`).
  - `windows/installer/tools.json` pins Inno Setup (version, winget id, URL, SHA-256, signer). The
    .NET SDK is pinned by `global.json`, NuGet versions by the project files.
  - The build script locates `ISCC.exe` (parameter, `INNO_SETUP_PATH`, `.tools/`, `Program Files\Inno
    Setup 7`), reads its version by compiling a one-line probe with `/O-` (ISCC has no file version),
    fails on mismatch; `-BootstrapTools` downloads, verifies SHA-256 and installs it portably into
    `.tools/` (`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER /PORTABLE=1 /DIR=…`).
  - Output to `artifacts/installer/` (git-ignored), cleared at start; a failed build leaves no
    installer.
  - Paths: MakePri fails on deep paths (reference, ≈130 chars); the repository path here is short,
    the script refuses > 120 characters anyway.

## R10. Build pipeline and CI (FR-009…FR-012)

- **Decision**: `windows/build/Build-Installer.ps1` ([contract](contracts/build-installer.md)):
  version + tools → `dotnet build windows/HentaiReader.slnx -c Release -p:Platform=x64` (warnings
  are errors) → `dotnet test` → `dotnet publish` to `artifacts/publish/win-x64` → optional signing →
  `Test-Distribution.ps1` → `ISCC` + manifest check → portable ZIP → optional `-SmokeTest`.
- **Smoke test** `windows/build/Test-InstallerSmoke.ps1`: silent per-user install into a temporary
  folder with `HENTAIREADER_DATA` pointing to a temporary data folder holding a sentinel file and a
  `preferences.json` without adult confirmation; start the app, check it still runs after 10 s and
  that the data folder gained nothing but the sentinel (gate not passed → nothing stored);
  reinstall the same version (repair); a lower-version build must refuse with exit 1; silent
  install while the app runs must refuse with exit 1; silent uninstall; program folder and uninstall
  entry gone, sentinel still present.
- **CI**: `.github/workflows/ci.yml` gets an `installer` job (windows-latest) running
  `Build-Installer.ps1 -BootstrapTools -SmokeTest` on every push and pull request, uploading the
  installer, portable ZIP and reports as artifacts. Every change, not only dependency changes —
  simpler and covers FR-012 fully. It replaces the separate build/test steps of the `windows` job
  only if it fully contains them; the `windows` job stays for fast feedback.

## R11. Code signing (FR-014)

- **Decision**: optional `HENTAIREADER_SIGN_COMMAND` (a `signtool sign …` line with `$f`); signs
  `HentaiReader.exe`, `HentaiReader.dll`, `HentaiReader.Host.dll` and is passed to Inno as
  `SignTool` with `SignedUninstaller=yes`. Without it the build succeeds and the summary says
  "unsigned".

## R12. Exit codes (FR-022, FR-003)

- **Decision**: Inno's documented codes are the contract ([installer-cli.md](contracts/installer-cli.md));
  callers rely on 0 / non-zero and the log. Measured in the reference: 1 for downgrade refusal and
  for "app running" in silent mode; to be re-measured by the smoke test here.

## R13. App icon and third-party notices (FR-006, FR-015)

- **Decision**: `windows/src/HentaiReader.App/Assets/AppIcon.ico` (16–256 px, a simple original
  glyph, no third-party artwork) set as `ApplicationIcon` and as the window icon; used by Inno for
  `SetupIconFile` and the uninstall entry (`UninstallDisplayIcon={app}\HentaiReader.exe`).
  `windows/THIRD-PARTY-NOTICES.md` lists the shipped components and licenses (.NET runtime, Windows
  App SDK, Jint, Acornima, AngleSharp, CommunityToolkit.Mvvm) and is copied to the output root.
