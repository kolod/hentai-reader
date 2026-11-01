# Contract: Android strings shared with Windows (FR-002, FR-039)

Extends [003 l10n-keys](../../003-windows-host/contracts/l10n-keys.md). The Windows `.resw` files
are the single source of shared strings; Android generates its copy from them.

## Files

| File | Origin |
|------|--------|
| `android/app/src/main/res/values/strings_shared.xml` | generated from `Strings/en-US/Resources.resw` |
| `values-de`, `values-fr`, `values-es`, `values-ja`, `values-uk` `/strings_shared.xml` | generated from `de-DE`, `fr-FR`, `es-ES`, `ja-JP`, `uk-UA` |
| `values*/strings_android.xml` | hand-written Android-only keys, same naming rules |

Generator: `npm run l10n:android` (`tools/l10n/resw-to-android.ts`); `-- --check` exits non-zero when
a generated file would change. Generated files start with a "do not edit" comment.

## Conversion rules

| `.resw` | `strings.xml` |
|---------|---------------|
| `Key` | `<string name="Key">` (keys never contain dots; a dotted XAML key would be skipped and reported) |
| family `Key_one`, `Key_few`, `Key_many`, `Key_other` (+ `zero`, `two`); a family needs both `_one` and `_other` in English, so vocabulary keys such as `Namespace_other` stay strings | `<plurals name="Key">` with `<item quantity="…">` |
| `{0}`, `{1}` | `%1$s`, `%2$s`, also in plural items: as on Windows, numbers are formatted for the locale by the app and passed as strings (the count only selects the plural item) |
| `'`, `"` | `\'`, `\"` |
| leading `@` or `?` | escaped `\@`, `\?` |
| `%` not part of a placeholder | `%%` |
| `&`, `<`, `>` | XML entities |
| newline, tab, `\` | `\n`, `\t`, `\\` |
| leading or trailing space | the whole value in double quotes |

A key that exists only in a non-English file is an error (English is the source of keys). A
missing translation is not written; Android then falls back to `values/`, but the parity test
fails (Constitution IX).

## Android-only keys (initial list)

`Nav_OpenMenu`, `Reader_FirstPage`, `Reader_LastPage`, `Settings_KeepScreenOn`,
`Settings_KeepScreenOn_Hint`, `Settings_WifiOnly`, `Settings_WifiOnly_Hint`,
`Settings_CacheLocation_Internal`, `Settings_CacheLocation_Card`, `Library_GrantAccess`,
`Library_NoAccess`, `Library_FolderMissing`, `Library_ChooseFolder_Prompt`,
`Library_DeleteFromDevice_Body`, `Downloads_WaitingWifi`, `Downloads_StoppedBySystem`,
`Downloads_NotificationsOff`, `Notify_Channel_Downloads`, `Notify_Progress` (plural family
"{0} / {1}"), `Notify_MoreQueued` (plural), `Notify_Pause`, `Notify_Resume`, `Notify_Done`,
`Notify_Stopped`, `Storage_ReserveNotice`.

Each exists in all six languages before release (machine translation as a draft is acceptable).

## Formatting

Numbers, dates, relative times and sizes use the chosen app locale (`Formatter.formatShortFileSize`
with a locale-wrapped context, `DateUtils`/`java.time` with that locale). Language names in the
language filter use `Locale.forLanguageTag(code).getDisplayName(itself)`, as Windows uses the native
name.
