# Contract: Installer and uninstaller

## Installer `HentaiReader-Setup-<version>-x64.exe`

Interactive by default. Pages: language selection, welcome (with the adult content notice), scope
(current user / all users), destination, optional desktop shortcut, ready, progress, finish (with
"Launch Hentai Reader" checked).

| Option | Meaning |
|--------|---------|
| `/CURRENTUSER` | Install for the current user without elevation (default scope) |
| `/ALLUSERS` | Install for all users (elevates) |
| `/SILENT` | Progress window only, no questions |
| `/VERYSILENT /SUPPRESSMSGBOXES` | No windows; message boxes answered with their defaults |
| `/DIR="path"` | Override the destination folder |
| `/LANG=uk` | Force the installer language (`en`, `de`, `fr`, `es`, `ja`, `uk`) |
| `/NORESTART` | Never restart |
| `/LOG="file"` | Write a setup log |

Upgrade: run a newer installer; scope, folder and installer language of the existing installation
are reused. The `Sources` subfolder of the program folder is replaced on every install.

### Exit codes

| Code | Meaning |
|------|---------|
| 0 | Installed, upgraded or repaired |
| 1 | Setup did not start: a newer version is installed (downgrade refused), or Hentai Reader is running (silent mode) |
| 2 | Cancelled by the user before installation started |
| other non-zero | Other failures; see the `/LOG` file |

Callers (including a future in-app updater) MUST rely only on 0 / non-zero and the log file.

## Uninstaller (`unins000.exe` in the program folder; also from "Installed apps")

| Option | Meaning |
|--------|---------|
| `/SILENT`, `/VERYSILENT /SUPPRESSMSGBOXES` | As above |
| `/LOG="file"` | Write an uninstall log |

Removes the program folder (including `Sources`), shortcuts and the uninstall entry. Never removes
`%LOCALAPPDATA%\HentaiReader` (preferences, source data, encrypted sign-in sessions).

## User-visible texts

Standard texts come from the official Inno Setup translations. Project texts are
`[CustomMessages]` in `windows/installer/Messages/<lang>.isl`, the same keys in all six files:

| Key | English text |
|-----|--------------|
| `AppDescription` | Search, browse and read manga from Hitomi.la and E-Hentai |
| `AdultNotice` | Hentai Reader shows content for adults only. Install it only if you are of legal age in your country. |
| `NewerVersionInstalled` | A newer version of Hentai Reader (%1) is already installed. This installer contains version %2 and will not replace it. |
| `LaunchApp` | Launch Hentai Reader |
| `DesktopIcon` | Create a desktop shortcut |
| `DataKeptNotice` | Your settings and site sign-ins in %1 were kept. Delete this folder to remove them. |

`AdultNotice` is appended to the welcome page text. `DataKeptNotice` is shown on the last uninstall
page (interactive only).
