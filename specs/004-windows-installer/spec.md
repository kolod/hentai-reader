# Feature Specification: Windows Installer

**Feature Branch**: `004-windows-installer`

**Created**: 2026-10-24

**Status**: Draft

**Input**: User description: "Make an installer similar to the one made for llama-cpp-gui
(C:\Users\alexa\Projects\Ai\llama-cpp-gui, feature 003-installer)."

## Clarifications

Decisions taken over from the reference installer (llama-cpp-gui `specs/003-installer`), where they
were clarified with the same user:

- Installation scope: the user chooses during installation — current user only (default, no
  administrator rights) or all users (administrator rights).
- Self-update: not part of this feature, but the installer must allow it later (a newer installer
  run unattended over an existing installation upgrades it in place).
- Installer language: fully localized into the app's six UI languages, following the Windows
  display language, changeable at the start of the installation.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Install the application (Priority: P1)

A user downloads one installer file, runs it, and gets a working Hentai Reader with a Start menu
entry. Nothing else (runtime, framework) has to be installed first.

**Why this priority**: Today the app is only a folder of files to unpack; an installer is the normal
way to hand a Windows application to anyone else, and the constitution names it as a distribution
form.

**Independent Test**: On a clean Windows 10 or 11 x64 machine without developer tools, run the
installer with the defaults, start the app from the Start menu, pass the adult content
confirmation, open a source, and close the app.

**Acceptance Scenarios**:

1. **Given** a clean supported machine, **When** the user runs the installer and accepts the
   defaults, **Then** the installer is shown in the Windows display language (one of the six, else
   English), installs for the current user without asking for administrator rights, creates a Start
   menu entry, and the app starts.
2. **Given** the scope choice, **When** the user selects "all users", **Then** Windows asks for
   administrator rights and the app is installed for every user of the computer.
3. **Given** the app is installed, **When** the user opens Windows "Installed apps", **Then** the app
   is listed with name, version, publisher, and icon and can be uninstalled from there.
4. **Given** the installer's welcome page, **When** it is shown, **Then** it states that the app
   shows content for adults only; the app's own adult content confirmation still appears on first
   start (the installer does not answer it).
5. **Given** an unsupported system (Windows older than 10 version 1903, or not x64), **When** the
   installer starts, **Then** it refuses with a clear message and changes nothing.

---

### User Story 2 - One command builds a clean, verified installer (Priority: P1)

The maintainer runs one documented command and gets the installer. It contains only the release
build and only files needed at run time; the command fails instead of producing an installer when
anything is wrong, and it keeps working after the app, its dependencies, or the build tools are
updated.

**Why this priority**: An installer that silently ships debug files, misses a language or a source
script, or stops building after an update would undermine every release.

**Independent Test**: Run the command on a clean clone; inspect the content listing and the
verification report; repeat after updating a dependency version.

**Acceptance Scenarios**:

1. **Given** a clean clone, **When** the maintainer runs the installer build command, **Then** an
   installer is produced with the app version in its file name and metadata.
2. **Given** the produced installer, **When** its content is listed, **Then** it contains no debug
   symbol files, no debug build, no test assemblies, no build intermediates, no recorded test
   fixtures, and no cookie or credential files.
3. **Given** the produced installer, **When** its content is listed, **Then** it contains the app,
   all six UI languages, every bundled source script, and the third-party notices.
4. **Given** any failed check (tests, warnings, forbidden file found, required file missing, version
   mismatch), **When** the build command runs, **Then** it stops with a non-zero result and a message
   naming the check, and no installer is placed in the output folder.
5. **Given** a commit that changes dependencies, build configuration, or installer configuration,
   **When** the repository's automated checks run, **Then** the installer build and its verification
   run too, so a broken installer is detected before a release.

---

### User Story 3 - Upgrade to a newer version (Priority: P2)

A user with an older version runs the installer of a newer one. The app is replaced; everything the
user has stays: language and other preferences, adult content confirmation, per-source settings and
stored values, and site sign-in sessions.

**Why this priority**: Every release after the first is an upgrade; losing a site sign-in or
preferences on each update would be a constant annoyance.

**Independent Test**: Install version N, change the UI language and sign in to a source, install
version N+1, confirm language and sign-in are unchanged.

**Acceptance Scenarios**:

1. **Given** version N with user data, **When** the user installs N+1, **Then** N+1 starts with the
   same preferences, source data, and sign-in sessions and does not ask for the adult confirmation
   again.
2. **Given** the app is running, **When** the user starts the installer, **Then** the user is asked
   to close the app first; the installer does not replace files of a running app.
3. **Given** N+1 is installed, **When** the user runs the installer of N, **Then** it explains that a
   newer version is installed and changes nothing.

---

### User Story 4 - Uninstall (Priority: P2)

A user removes the app through "Installed apps". Program files and shortcuts are removed; the user's
data stays unless the user removes it deliberately.

**Why this priority**: A clean uninstall is expected on Windows; silently deleting sign-ins and
settings (and, later, downloaded galleries) would be harmful.

**Independent Test**: Uninstall and check that the program folder, shortcuts, and "Installed apps"
entry are gone while the data folder is untouched.

**Acceptance Scenarios**:

1. **Given** the app is installed, **When** the user uninstalls it, **Then** program files, the Start
   menu entry, and the "Installed apps" entry are removed.
2. **Given** the app has user data, **When** it is uninstalled, **Then** the data folder (preferences,
   source data, encrypted sign-in sessions) remains untouched.
3. **Given** the app is running, **When** the user uninstalls, **Then** the user is asked to close it
   first.

---

### User Story 5 - Unattended install (Priority: P3)

An advanced user or a test machine installs the app from a script without interaction.

**Why this priority**: Useful for repeat installs and automated smoke tests, and the basis for later
self-update; not needed by typical users.

**Independent Test**: Run the installer with its documented silent option and compare the result
with an interactive install with defaults.

**Acceptance Scenarios**:

1. **Given** a supported machine, **When** the installer runs with the silent option, **Then** it
   installs without any dialog and reports success or failure through its exit code.
2. **Given** an older version is installed, **When** a newer installer runs with the silent option,
   **Then** the installation is upgraded in place with its scope and data preserved.

---

### Edge Cases

- The installer is run again for the same version: it repairs/reinstalls without losing data.
- The program folder and the data folder are never the same folder, so removing the program can
  never remove user data.
- Not enough disk space: the installer reports required and available space and changes nothing.
- A damaged installation (files deleted by hand): installing again restores a working app.
- The installer is not signed by a known publisher, so Windows SmartScreen warns: the README explains
  the warning; signing is used whenever a certificate is configured.
- The user installs offline: installation needs no network access.
- The portable folder build and an installed copy are used on the same machine: both use the same
  per-user data folder, so preferences and sign-ins are shared; neither deletes the other's files.
- A file needed at run time is missing from the package (a language resource, a source script): the
  build verification fails before the installer is produced.

## Requirements *(mandatory)*

### Functional Requirements

**Installer choice**

- **FR-001**: The installer technology MUST be chosen by comparing at least: one-click per-user
  installers with built-in update support, classic setup wizards, Windows Installer packages, and
  Windows app packages, against this app's needs (unpackaged self-contained desktop app, per-user
  data, six UI languages, no administrator rights for normal use, Windows 10 1903+). The reference
  installer's research may be reused but the decision MUST be recorded in this feature's research
  document.
- **FR-002**: The installer MUST let the user choose the scope: current user only (default, no
  administrator rights) or all users (administrator rights). Upgrades MUST keep the scope of the
  existing installation.
- **FR-003**: Self-update is out of scope, but a newer installer run unattended over an existing
  installation MUST upgrade it in place (same scope, data preserved) and report the result through
  its exit code, so that self-update can be added later without changing the installer.

**Content of the distribution**

- **FR-004**: The installer MUST contain only the release build of the app.
- **FR-005**: The installer MUST NOT contain debug symbol files, test assemblies, build
  intermediates, development configuration, source code, recorded test fixtures, or any cookie,
  credential, or personal data file.
- **FR-006**: The installer MUST contain everything needed to run without installing any runtime or
  framework first (self-contained), including all six UI languages, every bundled source script, the
  app icon and assets, and the third-party notices.
- **FR-007**: The set of distributed files MUST be defined by an explicit rule (allow list or
  exclusion rules) kept in the repository, so that a new kind of file cannot enter the installer
  unnoticed.
- **FR-008**: The portable folder build MUST remain available alongside the installer and MUST obey
  the same content rules (FR-004–FR-007).

**Build and verification**

- **FR-009**: A single documented command MUST build, verify, and produce the installer from a clean
  clone.
- **FR-010**: The installer build MUST run the automated tests and the release build with warnings
  treated as errors before packaging.
- **FR-011**: After packaging, an automated verification MUST check the package content: no
  forbidden files (FR-005), all required files present (FR-006), x64 only, and version equal to the
  app version. Any failure MUST fail the build.
- **FR-012**: The installer build and verification MUST run in the repository's automated checks for
  every change to dependencies, build configuration, or installer configuration.
- **FR-013**: Installer builds MUST be reproducible: the same commit and tool versions produce an
  installer with the same file list and version; packaging tool versions MUST be pinned in the
  repository.
- **FR-014**: When a code-signing certificate is configured, the installer and the app executable
  MUST be signed; when none is configured, the build MUST still succeed and report that the output is
  unsigned.

**Install, upgrade, uninstall behavior**

- **FR-015**: The installed app MUST appear in Windows "Installed apps" with name, version, publisher,
  and icon, and MUST have a Start menu entry.
- **FR-016**: The program folder MUST be separate from the per-user data folder.
- **FR-017**: Upgrades MUST preserve all user data (preferences including the adult content
  confirmation and UI language, per-source settings and stored values, encrypted sign-in sessions).
- **FR-018**: Uninstall MUST remove program files, shortcuts, and the "Installed apps" entry and MUST
  NOT remove the data folder.
- **FR-019**: Install, upgrade, and uninstall MUST NOT proceed while the app is running; the user MUST
  be asked to close it.
- **FR-020**: The installer MUST NOT downgrade an installed newer version without explicit user
  action.
- **FR-021**: The installer MUST refuse to run on unsupported systems (Windows older than 10 version
  1903, or not x64) with a clear message.
- **FR-022**: The installer MUST support an unattended (silent) mode with a meaningful exit code.
- **FR-023**: Installation MUST NOT require network access.
- **FR-024**: The installer's welcome text MUST state that the app shows adult content only; it MUST
  NOT record the app's adult content confirmation.

**Localization**

- **FR-025**: All user-visible installer and uninstaller text (wizard pages, scope choice, adult
  content notice, errors, prompts to close the app) MUST be available in English, German, French,
  Spanish, Japanese, and Ukrainian. The language MUST follow the Windows display language when
  supported, otherwise English, and the user MUST be able to change it at the start of installation.
- **FR-026**: Texts written for this installer (beyond the standard translated messages of the
  installer technology) MUST be kept in the repository for all six languages and checked for
  completeness by the automated tests, like the app resources (Constitution IX).

### Key Entities

- **Installer package**: the single x64 installer; version (equal to the app version), file list,
  signing state.
- **Distribution rule set**: the repository-kept definition of which files may and must be in a
  package (shared by installer and portable build).
- **Verification report**: result of the content checks for one package (pass/fail per check,
  offending or missing files).
- **Installation**: an installed instance; program folder, scope, version, Start menu entry,
  "Installed apps" entry; separate from the per-user data folder.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: On a clean supported machine, a user goes from the downloaded installer to a running
  app in under 2 minutes without installing anything else.
- **SC-002**: Every produced installer contains 0 debug symbol files, 0 test assemblies, 0 test
  fixtures, and 0 cookie or credential files, verified automatically on every build.
- **SC-003**: After any dependency or build-tool update, a broken installer build is detected by the
  automated checks of that change before a release, in 100% of cases.
- **SC-004**: Upgrading from the previous version keeps 100% of preferences, source data, and
  sign-in sessions.
- **SC-005**: After uninstall, no program files, shortcuts, or running app processes remain, and
  100% of the user's data is still present.
- **SC-006**: The installer is no larger than the self-contained release output plus 10%.

## Assumptions

- Target systems are Windows 10 version 1903 or later and Windows 11, x64 only, matching the app
  (feature 003). ARM64 is out of scope until the app itself is built for it.
- The app stays unpackaged; the installer does not change where it stores data
  (`%LOCALAPPDATA%\HentaiReader`, per Windows user, also with an all-users installation).
- Bundled source scripts are part of the program files and are replaced on upgrade; scripts updated
  later through the distribution repository (roadmap `script-updates`) and downloaded galleries
  (roadmap `cbz-downloads`) will live in the data folder and are therefore preserved by FR-017/FR-018.
- The app version comes from one place in the repository and is used for the app, the installer
  metadata, and the installer file name.
- The repository's existing automated checks (with a Windows job since feature 003) are extended;
  publishing installers as release assets and announcing versions are outside this feature.
- No code-signing certificate is available at first; the README explains the SmartScreen warning.
- The installer language choice does not change the app's UI language; the app keeps following the
  Windows language preferences or its own setting.
- Uninstall keeps user data by default; removing it is a manual step documented in the README.
- This feature is taken before the reader feature in the roadmap; the roadmap is renumbered
  accordingly.
