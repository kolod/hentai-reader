# Contract: UI string keys (Windows now, Android later)

Constitution IX: identical keys on both platforms; English is the source of truth.

## Naming

`Area_Element[_Qualifier]` in PascalCase segments, e.g. `Search_Placeholder`,
`Error_RateLimited_Title`, `Gallery_PageCount`. XAML `x:Uid` keys add the property:
`Search_Box.PlaceholderText`.

## Plurals

`<Key>_<category>` with CLDR categories `zero`, `one`, `two`, `few`, `many`, `other`
(`Gallery_PageCount_one` = "{0} page", `_other` = "{0} pages"). Every family has `_other`.
Ukrainian supplies `one`, `few`, `many`, `other`; Japanese only `other`. On Android the same family
becomes `<plurals name="Gallery_PageCount">` with matching `quantity` items.

## Contract vocabulary

Labels for contract vocabulary (filters, sort values, gallery types, setting ids/values, error
kinds) use fixed keys derived from the vocabulary, so a new source never needs new strings:

| Vocabulary | Key pattern | Example |
|------------|-------------|---------|
| Filter id | `Filter_<id>` | `Filter_min_rating` |
| Sort value | `Sort_<value>` | `Sort_popular_week` |
| Gallery type | `Type_<value>` | `Type_artist_cg` |
| Language code | (not a key) — native name from `CultureInfo(code).NativeName` | |
| Setting id / value | `Setting_<id>` / `Setting_<id>_<value>` | `Setting_site_exhentai` |
| Error kind | `Error_<Kind>_Title`, `Error_<Kind>_Body` | `Error_LoginRequired_Body` |
| Tag namespace | `Namespace_<ns>`; unknown namespaces shown as-is | `Namespace_female` |

A key missing in a language falls back to English at runtime, but the key-parity test fails the
build, so releases always carry all six (Constitution IX).
