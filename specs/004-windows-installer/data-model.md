# Data Model: Windows Installer

No runtime data of the app changes. The entities below are build artefacts and installation state.

## App version

- Source: `<Version>` in `windows/Directory.Build.props` (SemVer `MAJOR.MINOR.PATCH`).
- Used for: assembly/product version of `HentaiReader.exe`, `AppVersion` of the installer, the
  "Installed apps" `DisplayVersion`, file names, `windows-vX.Y.Z` tag.
- Rule: the product version of the published `HentaiReader.exe` (part before `+`) equals it; the
  build fails otherwise.

## Distribution rule set (`windows/installer/distribution-rules.json`)

Schema: [contracts/distribution-rules.schema.json](contracts/distribution-rules.schema.json).

| Field | Type | Meaning |
|-------|------|---------|
| `schemaVersion` | 1 | Rules format |
| `languages` | string[6] | UI language tags that must be present in `HentaiReader.pri` |
| `allowedExtensions` | string[] | Closed list; any other extension fails |
| `forbidden` | glob[] | Paths that must not exist (relative to the publish root, `/` separators) |
| `required` | pattern[] | Paths that must exist; `{source}` expands to every `/sources/<id>` holding `<id>.js` |
| `nativeMachine` | `{ "x64": "0x8664" }` | Expected PE machine of native files |
| `priResourceProbe` | string | Resource name whose language candidates are checked (`Resources/App_Title`) |

Validation: a path matching both `required` and `forbidden` is a rules error (fails the build).

## Verification report (`artifacts/installer/verification-x64.json`)

```json
{
  "version": "0.2.0",
  "architecture": "x64",
  "publishDir": "artifacts/publish/win-x64",
  "fileCount": 460,
  "totalBytes": 193000000,
  "checks": [
    { "name": "allowed-extensions", "passed": true, "offending": [] },
    { "name": "forbidden", "passed": true, "offending": [] },
    { "name": "required", "passed": true, "missing": [] },
    { "name": "pri-languages", "passed": true, "missing": [] },
    { "name": "bundled-scripts", "passed": true, "mismatched": [] },
    { "name": "native-machine", "passed": true, "offending": [] },
    { "name": "product-version", "passed": true, "found": "0.2.0" },
    { "name": "installer-manifest", "passed": true, "missing": [], "extra": [], "changed": [] }
  ],
  "signed": false,
  "testsSkipped": false,
  "passed": true
}
```

`passed` is the AND of all checks. On failure every check still runs where possible, so one report
names all problems.

## Installer package

| Attribute | Value |
|-----------|-------|
| File | `artifacts/installer/HentaiReader-Setup-<version>-x64.exe` |
| `AppId` | fixed GUID (never changes; upgrades depend on it) |
| `AppName` / `AppPublisher` | `Hentai Reader` / `kolod` |
| Content | exactly the verified publish folder (manifest check) |
| Signing | signed iff `HENTAIREADER_SIGN_COMMAND` is set |

## Portable package

`artifacts/installer/HentaiReader-<version>-x64-portable.zip` — the verified publish folder as is;
no installer state; uses the same data folder as an installed copy.

## Installation (on a user's machine)

| Field | Current user | All users |
|-------|--------------|-----------|
| Program folder | `%LOCALAPPDATA%\Programs\Hentai Reader` | `%ProgramFiles%\Hentai Reader` |
| Uninstall entry | `HKCU\...\Uninstall\{AppId}_is1` | `HKLM\...\Uninstall\{AppId}_is1` |
| Start menu | user Programs | common Programs |
| Data folder | `%LOCALAPPDATA%\HentaiReader` (per Windows user in both cases; never written by the installer) | same |

State transitions:

```text
(none) --install--> Installed(v)
Installed(v) --install v--> Installed(v)            repair
Installed(v) --install w>v--> Installed(w)          upgrade, same scope and folder
Installed(v) --install u<v--> Installed(v)          refused, exit 1
Installed(v) --uninstall--> (none)                  data folder kept
any --install/uninstall while app runs--> unchanged asked to close; silent: exit 1
```

## Custom installer messages (`windows/installer/Messages/<lang>.isl`)

Six files with identical `[CustomMessages]` keys (see
[contracts/installer-cli.md](contracts/installer-cli.md#user-visible-texts)); `%1`… placeholders
identical per key; values equal to English only when listed in `identical-translations.txt`.
