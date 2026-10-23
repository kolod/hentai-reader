# Contract 1.1 — delta to `contract/host-api.d.ts`

Backward compatible (MINOR). Everything below is added; nothing in 1.0 changes meaning.
Implementation edits `contract/host-api.d.ts` directly — this file is the design only.

## Settings

```ts
/** Well-known vocabulary — labels in app string resources (Constitution IX). */
export type SettingDefinition =
  | { id: "image_quality"; values: ("resampled" | "original")[]; default: "resampled" | "original" }
  | { id: "site"; values: ("e-hentai" | "exhentai")[]; default: "e-hentai" | "exhentai" };

export interface SettingValues {
  image_quality?: "resampled" | "original";
  site?: "e-hentai" | "exhentai";
}

export interface SourceManifest {
  // … all 1.0 fields …
  /** Optional (1.1). Each id at most once; default ∈ values. */
  settings?: SettingDefinition[];
  capabilities: {
    suggestTags: boolean;
    /** Optional (1.1): source exports getAccountStatus. */
    accountStatus?: boolean;
  };
}
```

## Source functions (1.1 sources only)

```ts
export interface SourceModule {
  manifest: SourceManifest;
  search(query: SearchQuery, cursor: string | null, settings: SettingValues): Promise<SearchPage>;
  getGallery(id: string, settings: SettingValues): Promise<Gallery>;
  getPageUrl(gallery: Gallery, index: number, retry: RetryHint | null, settings: SettingValues): Promise<PageImage>;
  suggestTags?(prefix: string, settings: SettingValues): Promise<TagSuggestion[]>;
  /** Required iff manifest.capabilities.accountStatus. */
  getAccountStatus?(settings: SettingValues): Promise<AccountStatus>;
}
```

Calling convention (normative):

- Host supporting 1.1 calls a source whose `manifest.contract` is `"1.0"` **exactly as in 1.0**
  (no `settings`; `retry` omitted when absent).
- For `"1.1"` sources the host always passes `settings` — the user's stored values merged over the
  manifest defaults; keys the manifest does not declare are never passed — and passes `retry` as
  `null` when absent.

## Account status

```ts
export interface AccountStatus {
  loggedIn: boolean;
  /** Present when the site shows it to this user. */
  imageLimit?: { used: number; max: number; resetsAt: string | null };
  /** ExHentai-style mirror access, when the source can tell; null = unknown. */
  exhentai?: boolean | null;
}
```

Hosts call `getAccountStatus` only when declared, and only on user-visible demand (settings
screen, before a large download) — never on a timer (Constitution VI).

## Image requests

```ts
export interface ImageRequest {
  url: string;
  headers?: Record<string, string>;
  /** Optional (1.1): the image is a cell of a sprite sheet; host crops this rectangle (px). */
  crop?: { x: number; y: number; width: number; height: number };
}
```

Normative note added to the contract header: *hosts fetch every `ImageRequest` with the source's
cookie jar (domain-scoped exactly as for `http.request`) and through the same politeness limiter,
following redirects.*

## Vocabulary

`GalleryType` gains `"asian_porn"`.

## Version

`contract/package.json` → `1.1.0`; tag `contract-v1.1.0`; CHANGELOG entry.
