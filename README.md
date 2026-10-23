# Hentai Reader

Two near-identical apps — Android and Windows — that search, download, archive (CBZ), and read
manga from Hitomi.la and E-Hentai in a seamless infinite-scroll reader.

Adult content. Personal use only.

## Repository layout

| Path        | Contents                                              |
|-------------|-------------------------------------------------------|
| `android/`  | Android app (Kotlin, Jetpack Compose)                 |
| `windows/`  | Windows app (C#, WPF or WinUI 3)                      |
| `sources/`  | JS source scripts and `index.json`                    |
| `contract/` | Host API description (`.d.ts`) and contract tests     |
| `tools/`    | Console runner for source scripts                     |
| `specs/`    | Spec Kit specifications                               |

Site logic lives only in `sources/`; the apps provide the host API defined in `contract/`.
Released scripts are published by CI to the separate `sources-dist` repository.

## Rules

The project constitution is binding: [.specify/memory/constitution.md](.specify/memory/constitution.md).

- Work on feature branches (`001-short-name`); `main` always builds and passes tests.
- Commits: Conventional Commits with a component scope, e.g. `feat(android): ...`,
  `fix(sources/hitomi): ...`.
- Tags per component: `android-vX.Y.Z`, `windows-vX.Y.Z`, `contract-vX.Y.Z`,
  `sources-<source>-vX.Y.Z`.
