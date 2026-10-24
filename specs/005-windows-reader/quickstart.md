# Quickstart: validate the Windows reader

PowerShell 7 on Windows 10 1903+ / 11 x64, repository root. Live sites are used for reading;
unit and contract tests are offline.

## 1. Automated

```powershell
npm test                                   # runner, contract suite (1.2), conformance incl. broken-login-page
npx tsc -p tools/runner/tsconfig.json
dotnet test --project windows/tests/HentaiReader.Host.Tests -c Release   # + StripGeometry, LoadPlanner, PageCache, ProgressStore, login cookie selection
pwsh windows/build/Build-Installer.ps1 -SmokeTest                      # distribution still verifies (WebView2 loader allowed)
```

## 2. Single window and back stack (US2, SC-004)

1. Hitomi: search `female:glasses`, set a filter, scroll down ~100 results, open a gallery, Read.
   → Reader appears in the same window; no new window.
2. Escape → gallery details unchanged; Back → search with the same query, filter, results and
   scroll position, no new search request (status text unchanged).

## 3. Reading (US1, SC-001–SC-003)

1. E-Hentai gallery with 100+ pages, Read → first page within 2 s.
2. Scroll steadily to the end. → No content jump when pages load or sizes are corrected; pages are
   loaded before they appear (note any placeholders seen).
3. Gallery with a cover from another scan, a notes page and a spread → every page exactly the strip
   width, straight edges, nothing cropped (FR-004a).
4. Keys: Space, Shift+Space, PgUp/PgDn, Home, End, Ctrl+G 50, Ctrl+wheel, Ctrl+0.

## 4. Many pages in view (SC-011)

Settings → Reader → maximum width 600, window maximized, zoom 50 %, a CG-set gallery with landscape
pages → ≥ 3 pages visible; scroll → pages ready before they appear; Task Manager memory < 1 GB.

## 5. Progress (US3, SC-005)

Read to page 40, close the app, start it, open the gallery → "40 / N" and Continue → reader at page
40 within one screen.

## 6. Failures and limits (US5, SC-006)

- Disconnect network while reading → failed pages show Retry; loaded pages stay; reconnect, Retry.
- Rate limit (offline unit test of 429/509 → Limited; live only if it happens) → banner, no further
  requests until Retry.

## 7. Full screen (FR-016)

F11 on the search page → borderless full screen, navigation visible; open Reader → navigation
hidden; Alt+Tab to another app works; Escape → windowed again with the previous size; F11 in
settings works too.

## 8. Settings and sign-in (US6, US7, SC-007)

1. Settings → Sources → E-Hentai → Sign in → embedded login page (forums.e-hentai.org); a link to
   another site opens in the system browser. After signing in: "Signed in", image limit if shown.
2. Site → ExHentai → allowed only when the account has access; browse and read.
3. Sign out → state changes; `sources/ehentai/cookies.bin` gone.
4. Paste cookies (`ipb_member_id=…; ipb_pass_hash=…`) → verified, signed in.
5. Reader settings: preload 5, width 800, cache limit 256 MB, Clear cache → size 0, progress kept.

## 9. Languages (SC-009)

uia/screenshot pass of reader toolbar, settings, login dialog and errors in de-DE and ja-JP at
1024 px width.
