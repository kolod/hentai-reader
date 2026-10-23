# Quickstart & Validation: E-Hentai / ExHentai Source

Prerequisites: 001 quickstart (Node 22.18+, `npm ci`, `npm run schema`). For live and recording
steps: an E-Hentai account cookie file `cookies-eh.txt` (Netscape format; gitignored); for
ExHentai steps an account with ExHentai access.

## 1. Contract 1.1 and backward compatibility (SC-006, SC-007)

```text
npm run runner -- contract-test                      # includes settings-passing
npm run runner -- check contract/conformance/minimal # still a 1.0 source
npm run runner -- test sources/hitomi                # 18/18 unchanged on the 1.1 host
```

## 2. Offline E-Hentai suite (SC-001, SC-005)

```text
npm run runner -- check sources/ehentai   # types, manifest, settings, redaction idempotence, audit
npm run runner -- test sources/ehentai    # all cases, < 30 s, repeat 10× → identical
```

## 3. Live spot checks (SC-002)

```text
npm run runner -- run sources/ehentai search '{"text":"","filters":{}}' null --live --verbose
npm run runner -- run sources/ehentai search '{"text":"female:glasses","filters":{"type":["doujinshi","manga"],"min_rating":4}}' null --live
npm run runner -- run sources/ehentai getAccountStatus --live --cookies cookies-eh.txt
npm run runner -- run sources/ehentai search '{"text":"","filters":{}}' null --live --cookies cookies-eh.txt --setting site=exhentai
```

Expected: ≤ 2 requests per result page in `--verbose`, ≤ 5 s; account status shows the limit;
ExHentai works with access and fails `LoginRequired` without it.

## 4. Page sizes (SC-003)

```text
node tools/runner/scripts/live-dimension-check.ts sources/ehentai 10
```

Expected: every estimated size within 2 % of the real aspect ratio; every exact size from
`getPageUrl` equal to the served image.

## 5. Limits (SC-004)

Offline cases `page-limit-509`, `page-ip-ban` raise `RateLimited`; the runner request log shows no
image request after the limited response.
