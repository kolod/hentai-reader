# Implementation Plan: Windows Installer

**Branch**: `004-windows-installer` | **Date**: 2026-10-24 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/004-windows-installer/spec.md`

## Summary

An Inno Setup 7 installer for the existing WinUI 3 app, modelled on the reference installer of
llama-cpp-gui: install-time scope choice (current user by default, or all users), six installer
languages following the Windows display language, in-place silent upgrades by a fixed `AppId` (the
basis for later self-update), downgrade refusal, and an uninstaller that never touches
`%LOCALAPPDATA%\HentaiReader`. One PowerShell command builds, tests, publishes, verifies the publish
folder against repository-kept distribution rules (no symbols, no unused Windows App SDK AI/ML
components, no fixtures or cookies; all six languages inside the PRI, every bundled source script
byte-identical to `/sources`), compiles the installer, checks the compiler's manifest against the
verified folder, zips the same folder as the portable build, and optionally runs an install /
repair / downgrade / uninstall smoke test. CI runs it on every change. Research:
[research.md](research.md).

## Technical Context

**Language/Version**: Inno Setup 7.1.0 script (Pascal Script for checks); PowerShell 7 build
scripts; C# 14 / .NET 10 for the small app changes and tests

**Primary Dependencies**: Inno Setup 7.1.0 (pinned in `windows/installer/tools.json`), official Inno
translations for the six languages; `makepri.exe` from the already pinned
`Microsoft.Windows.SDK.BuildTools` package (PRI language check)

**Storage**: none new; the app's data folder `%LOCALAPPDATA%\HentaiReader` is left untouched by
install, upgrade and uninstall

**Testing**: `dotnet test` (adds installer message parity tests); `Test-Distribution.ps1` (content
rules); `Test-InstallerSmoke.ps1` (real silent install/repair/downgrade/uninstall on the build
machine and in CI)

**Target Platform**: Windows 10 1903+ / Windows 11, x64 (runs emulated on ARM64 Windows 11)

**Project Type**: packaging for an existing desktop app (scripts + installer script + small app
changes)

**Performance Goals**: installer ≈ 45 MB, ≤ publish output + 10 % (SC-006); download → running app
< 2 min (SC-001); full build including tests within the CI job time limit (target < 10 min)

**Constraints**: no network during install (FR-023); no administrator rights for the default scope;
the build must fail closed (no installer left on any failure); tool versions pinned (FR-013)

**Scale/Scope**: 1 installer script, 6 custom message files, 3 build scripts, 1 rules file, ~5 app
project changes, 1 test class, 1 CI job

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Constitution v1.0.3.

| # | Principle | Status | Evidence |
|---|-----------|--------|----------|
| I | Native UI, shared source logic | ✅ | No UI or site logic added; installer is packaging only |
| II | Thin, sandboxed scripts | ✅ | Scripts unchanged; bundled copies verified byte-identical to `/sources` |
| III | Verified updates | ✅ | Built-in copy of every script ships (fallback); stale copies impossible (`{app}\Sources` rewritten each install); remote updates remain feature 008 and will live in the data folder |
| IV | Reading smoothness | ➖ | Not affected |
| V | Open storage | ➖ | Not affected; future CBZ library lives in the data folder, preserved by uninstall |
| VI | Respect for sources | ✅ | No network during install; smoke test starts the app without passing the adult gate, so it sends no request |
| VII | Testability | ✅ | Content verification, installer-message parity test, smoke test of install/upgrade/uninstall, all in CI |
| VIII | Simplicity | ✅ | One pinned external tool (Inno Setup); rules in one JSON file; portable ZIP reuses the verified folder |
| IX | Localisation | ✅ | Installer in all six languages (official Inno translations + project `[CustomMessages]` × 6, parity-tested) |
| X | Version control | ✅ | Everything under `/windows`; artifacts git-ignored; single version source; `windows-vX.Y.Z` tags; no signing keys or certificates in the repository (signing only via an environment command); `CHANGELOG` updated |

No violations; Complexity Tracking empty.

## Project Structure

### Documentation (this feature)

```text
specs/004-windows-installer/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── installer-cli.md                 # installer/uninstaller options, exit codes, custom texts
│   ├── build-installer.md               # Build-Installer.ps1 usage, steps, outputs, failure behaviour
│   └── distribution-rules.schema.json   # schema of windows/installer/distribution-rules.json
├── checklists/requirements.md
└── tasks.md                             # /speckit-tasks
```

### Source Code (repository root)

```text
windows/
├── Directory.Build.props                # + <Version>0.2.0</Version> (single version source)
├── THIRD-PARTY-NOTICES.md               # new; copied to the output root
├── installer/
│   ├── HentaiReader.iss                 # Inno Setup script
│   ├── distribution-rules.json          # allowed / forbidden / required, languages
│   ├── tools.json                       # pinned Inno Setup (version, URL, SHA-256, signer)
│   └── Messages/                        # en, de, fr, es, ja, uk .isl + identical-translations.txt
├── build/
│   ├── Build-Installer.ps1              # the one command (FR-009)
│   ├── Test-Distribution.ps1            # content verification (FR-011)
│   └── Test-InstallerSmoke.ps1          # install / repair / downgrade / running / uninstall
├── src/HentaiReader.App/
│   ├── HentaiReader.App.csproj          # ApplicationIcon, ExcludeAssets for unused SDK parts,
│   │                                    # publish removes .pdb and createdump.exe, notices copied
│   ├── Assets/AppIcon.ico               # new
│   └── App.xaml.cs                      # running mutexes for AppMutex; window icon
└── tests/HentaiReader.Host.Tests/
    └── Resources/InstallerMessagesTests.cs

.github/workflows/ci.yml                 # + installer job
.gitignore                               # + artifacts/, .tools/
```

**Structure Decision**: installer and build scripts live under `/windows` next to the app they
package (Constitution X layout); outputs go to the git-ignored `artifacts/` at the repository root.
The portable ZIP is produced by the same command from the same verified folder rather than by a
second pipeline.

## Post-design Constitution Re-check

Design (research R1–R13, contracts, data model) keeps every row above unchanged. The one
script-specific risk found during design — a leftover script of a removed source still being
loaded after an upgrade — is closed by R5 (`[InstallDelete]` of `{app}\Sources`). ✅

## Complexity Tracking

None.
