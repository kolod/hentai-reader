# Contract change 1.1.1 → 1.2.0: `manifest.loginPage`

MINOR, backward compatible: sources declaring `"1.0"` or `"1.1"` run unchanged; the calling
convention of 1.2 is the one of 1.1.

## Addition to `SourceManifest` (`contract/host-api.d.ts`)

```ts
export interface SourceManifest {
  // …
  login: "none" | "optional" | "required";
  /**
   * 1.2: where a host can let the user sign in interactively (embedded browser) and how it
   * recognises a completed sign-in. Only meaningful when `login` is not "none".
   */
  loginPage?: LoginPage;
}

export interface LoginPage {
  /** Absolute https URL opened in the host's embedded browser; its host is within `domains`. */
  url: string;
  /** Cookie names whose presence (for `url`) means the sign-in is complete. Non-empty. */
  cookies: string[];
  /**
   * Further domains (each within `domains`) for which the host also stores the `cookies` above,
   * e.g. a mirror that shares the account.
   */
  shareWith?: string[];
}
```

## Host rules (normative, added to the contract text)

- A host MAY offer an embedded sign-in for sources with `loginPage`; it MUST NOT navigate the
  embedded browser to hosts outside `manifest.domains` (open them externally instead).
- On completion the host stores, in the source's cookie jar only, the cookies whose domain is
  within `manifest.domains`. The account is shared: the `loginPage.cookies` found on any of these
  domains are also stored for the domain of `url` and for every `shareWith` domain (so cookies
  pasted from the mirror sign in the main site too).
- Cookies obtained this way follow the existing rule: sent only to the source's domains, stored
  only locally.
- A host MAY also accept cookies typed or pasted by the user; the same storage rule applies.

## Validation (runner `check`, both hosts on load)

| Rule | Violation text |
|------|----------------|
| `loginPage.url` is absolute `https:` | `manifest.loginPage.url: must be an absolute https URL` |
| host of `url` matches `domains` | `manifest.loginPage.url: host <h> is outside manifest.domains` |
| `cookies` non-empty, unique names | `manifest.loginPage.cookies: must list at least one cookie name` |
| each `shareWith` matches `domains` | `manifest.loginPage.shareWith: <d> is outside manifest.domains` |
| `loginPage` with `login: "none"` | `manifest.loginPage: not allowed when login is "none"` |
| `loginPage` in a source declaring `< 1.2` | `manifest.loginPage: requires contract 1.2` |

Conformance sample: `contract/conformance/broken-login-page/` (url outside domains).

## E-Hentai source (`sources/ehentai`, 0.1.0 → 0.2.0)

```js
contract: "1.2",
loginPage: {
  url: "https://e-hentai.org/bounce_login.php?b=d&bt=1-1",
  cookies: ["ipb_member_id", "ipb_pass_hash"],
  shareWith: ["exhentai.org"],
},
```

No function changes; recorded cases unchanged.

Confirmed 2026-10-24 (T034): `bounce_login.php` answers 200 with the site's login form (posting to
`forums.e-hentai.org`); the forum login page itself answers 403 to non-browser clients, so it is not
used. A signed-in profile carries `ipb_member_id` and `ipb_pass_hash` (plus `igneous`, `sk`, `yay` on
exhentai.org, which the site sets itself once the shared cookies are present). Only names were read;
no values were written down.

## Versions and tags

`contract/package.json` 1.2.0, `HostInfo.ContractVersion` 1.2.0, runner host version 1.2; tags
`contract-v1.2.0`, `sources-ehentai-v0.2.0` after merge. `contract/CHANGELOG.md` and the contract
README "Calling convention" section gain a 1.2 entry.
