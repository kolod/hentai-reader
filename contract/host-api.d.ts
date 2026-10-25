/**
 * Hentai Reader — host API and source contract.
 *
 * Single source of truth (Constitution II). Version = contract/package.json "version".
 * Any change here is versioned and implemented on every host at the same time.
 *
 * Execution model:
 * - A source is one ES module (ES2020) evaluated after /contract/prelude.js.
 * - The engine exposes only ECMAScript built-ins plus the globals declared below.
 * - A host may reuse one context across calls, discard it at any time, and run several calls
 *   concurrently on it. Module-level caches are allowed but must not be relied on.
 * - Limits: 60 s wall-clock per call, 128 MiB engine memory.
 * - All dates exchanged with the host are ISO 8601 strings.
 * - (1.1) Hosts fetch every ImageRequest with the source's cookie jar (domain-scoped exactly as
 *   for http.request), through the same politeness limiter, following redirects.
 */

// ---------------------------------------------------------------------------------------------
// Host capabilities (globals available to source scripts)
// ---------------------------------------------------------------------------------------------

declare global {
  const http: HostHttp;
  const html: HostHtml;
  const json: HostJson;
  const storage: HostStorage;
  /** Resolves after `ms` milliseconds (max 60 000). The only way to wait. */
  function sleep(ms: number): Promise<void>;
  /** Writes to the host's debug log only; never shown to the user. */
  const console: HostConsole;

  /** Defined by /contract/prelude.js. The only error type a source should throw. */
  class SourceError extends Error {
    constructor(kind: SourceErrorKind, message: string, options?: SourceErrorOptions);
    readonly kind: SourceErrorKind;
    readonly retryAfterSeconds?: number;
    readonly details?: Record<string, unknown>;
  }
}

export interface HostHttp {
  /**
   * Performs a request through the host. Never throws for HTTP status codes; rejects with
   * SourceError("Network") on transport failure or timeout, and with SourceError("InvalidInput")
   * if the URL's host is not matched by `manifest.domains`.
   * The host follows up to 5 redirects, applies `manifest.politeness`, and attaches cookies from
   * its per-source jar (merged with any `cookie` header given here). Set-Cookie responses update
   * the jar; they are never exposed in `HttpResponse.headers`.
   */
  request(req: HttpRequest & { responseType: "bytes" }): Promise<HttpResponse<Uint8Array>>;
  request(req: HttpRequest & { responseType?: "text" }): Promise<HttpResponse<string>>;
}

export interface HttpRequest {
  method?: "GET" | "POST";             // default "GET"
  url: string;                         // absolute http(s) URL
  headers?: Record<string, string>;    // e.g. { range: "bytes=0-99", referer: "…" }
  body?: string | Uint8Array;          // POST only
  responseType?: "text" | "bytes";     // default "text"; text is decoded per charset, else UTF-8
  timeoutMs?: number;                  // default 30 000
}

export interface HttpResponse<B extends string | Uint8Array> {
  status: number;
  url: string;                         // final URL after redirects
  headers: Record<string, string>;     // lower-cased names; repeated headers joined with ", "
  body: B;
}

export interface HostHtml {
  /**
   * Runs a CSS selector against a markup string (parsed as a full HTML document) or within a
   * previously returned element. Supported syntax: CSS Selectors Level 3 plus `:has()`.
   * Non-standard pseudo-classes (`:contains`, `:eq`, …) throw SourceError("InvalidInput").
   * On an element, only descendants match — never the element itself (like querySelectorAll).
   * Elements are returned in document order.
   */
  select(target: string | HtmlElement, selector: string): HtmlElement[];
}

/**
 * Host handle to a parsed element. Valid only during the source call that obtained it; do not
 * keep elements in module-level variables.
 */
export interface HtmlElement {
  readonly tag: string;                // lower-case tag name
  /**
   * Text of the element and its descendants, with runs of ASCII whitespace (space, tab, CR, LF,
   * FF) collapsed to one space and trimmed. Non-breaking spaces are kept. Do not rely on a space
   * being inserted between adjacent elements.
   */
  text(): string;
  ownText(): string;                   // same normalisation, direct text-node children only
  html(): string;                      // inner HTML; exact serialisation is host-specific
  outerHtml(): string;                 // exact serialisation is host-specific
  attr(name: string): string | null;   // raw attribute value; URLs are NOT resolved
}

export interface HostJson {
  /** Same semantics as JSON.parse; provided so hosts may use a faster native parser. */
  parse(text: string): unknown;
}

export interface HostStorage {
  /** Per-source key-value store. Key ≤ 128 chars, value ≤ 1 MiB, total ≤ 8 MiB per source. */
  get(key: string): Promise<string | null>;
  /** `null` deletes the key. */
  set(key: string, value: string | null): Promise<void>;
}

export interface HostConsole {
  log(...args: unknown[]): void;
  warn(...args: unknown[]): void;
  error(...args: unknown[]): void;
}

// ---------------------------------------------------------------------------------------------
// Errors
// ---------------------------------------------------------------------------------------------

/**
 * RateLimited   — site signalled a limit (HTTP 429/509, limit page); host stops, does not retry.
 * LoginRequired — content needs a login the host does not have.
 * NotFound      — gallery/page does not exist.
 * SiteChanged   — response no longer matches what the script expects (parse failure).
 * InvalidInput  — bad cursor, unknown filter/value, undeclared domain, bad selector.
 * Network       — raised by the host only.
 * Any other exception reaching the host is classified by the host as `ScriptError`.
 */
export type SourceErrorKind =
  | "RateLimited"
  | "LoginRequired"
  | "NotFound"
  | "SiteChanged"
  | "InvalidInput"
  | "Network";

export interface SourceErrorOptions {
  retryAfterSeconds?: number;
  details?: Record<string, unknown>;
}

// ---------------------------------------------------------------------------------------------
// Source module exports
// ---------------------------------------------------------------------------------------------

/**
 * Shape of a source module's exports.
 *
 * Calling convention (1.1):
 * - A source whose `manifest.contract` is "1.0" is called exactly as in 1.0: no `settings`
 *   argument, and `retry` omitted when there is no retry.
 * - A source declaring "1.1" always receives `settings` as the last argument — the user's stored
 *   values merged over the manifest defaults, only ids the manifest declares — and `retry` is
 *   `null` when there is no retry.
 */
export interface SourceModule {
  manifest: SourceManifest;
  /** Empty text and no filters = front feed. `cursor` is null for the first page. */
  search(query: SearchQuery, cursor: string | null, settings?: SettingValues): Promise<SearchPage>;
  getGallery(id: string, settings?: SettingValues): Promise<Gallery>;
  /** `retry` is present (1.0) or non-null (1.1) when a previous attempt for this page failed. */
  getPageUrl(gallery: Gallery, index: number, retry?: RetryHint | null, settings?: SettingValues): Promise<PageImage>;
  /** Required iff `manifest.capabilities.suggestTags` is true. */
  suggestTags?(prefix: string, settings?: SettingValues): Promise<TagSuggestion[]>;
  /**
   * (1.1) Required iff `manifest.capabilities.accountStatus` is true. Hosts call it only on
   * user-visible demand (settings screen, before a large download), never on a timer.
   */
  getAccountStatus?(settings?: SettingValues): Promise<AccountStatus>;
}

export interface SourceManifest {
  id: string;                          // /^[a-z0-9-]{2,32}$/, stable forever
  name: string;                        // display name, shown as-is (source content)
  version: string;                     // SemVer of the script; tag sources-<id>-vX.Y.Z
  contract: string;                    // required contract "MAJOR.MINOR", e.g. "1.0"
  domains: string[];                   // "hitomi.la", "*.hitomi.la"; requests elsewhere refused
  politeness: {
    minIntervalMs: number;             // min gap between request starts, ≥ 0
    maxConcurrency: number;            // ≥ 1
  };
  login: "none" | "optional" | "required";
  /**
   * (1.2) Where a host may let the user sign in interactively and how it recognises a completed
   * sign-in. Only with `login` other than "none".
   */
  loginPage?: LoginPage;
  filters: FilterDefinition[];
  /** (1.1) User-facing settings; each id at most once, `default` ∈ `values`. */
  settings?: SettingDefinition[];
  capabilities: {
    suggestTags: boolean;
    /** (1.1) The source exports `getAccountStatus`. */
    accountStatus?: boolean;
  };
}

/**
 * (1.2) Interactive sign-in. A host MAY open `url` in an embedded browser; it MUST NOT navigate it
 * to hosts outside `manifest.domains` (those open externally). When every name in `cookies` is
 * set for `url`, the sign-in is complete: the host stores in the source's cookie jar only the
 * cookies whose domain is within `manifest.domains`; the account is shared, so the `cookies` found
 * on any of these domains are also stored for the domain of `url` and for each `shareWith` domain.
 * Cookies the user types or pastes follow the same rule. As always, cookies
 * stay local and are sent only to the source's domains.
 */
export interface LoginPage {
  /** Absolute https URL; its host is within `manifest.domains`. */
  url: string;
  /** Names of the cookies that prove a completed sign-in; at least one, unique. */
  cookies: string[];
  /** Further domains (each within `manifest.domains`) that share the account, e.g. a mirror. */
  shareWith?: string[];
}

// ---------------------------------------------------------------------------------------------
// Settings and account status (1.1)
// ---------------------------------------------------------------------------------------------

/** Well-known vocabulary — UI labels for these live in app string resources (Constitution IX). */
export type SettingDefinition =
  | { id: "image_quality"; values: ("resampled" | "original")[]; default: "resampled" | "original" }
  | { id: "site"; values: ("e-hentai" | "exhentai")[]; default: "e-hentai" | "exhentai" };

/** Current setting values passed to 1.1 sources. */
export interface SettingValues {
  image_quality?: "resampled" | "original";
  site?: "e-hentai" | "exhentai";
}

export interface AccountStatus {
  loggedIn: boolean;
  /** Present when the site shows it to this user. */
  imageLimit?: { used: number; max: number; resetsAt: string | null };
  /** Access to an ExHentai-style mirror, when the source can tell; null = unknown. */
  exhentai?: boolean | null;
}

// ---------------------------------------------------------------------------------------------
// Search
// ---------------------------------------------------------------------------------------------

/** Well-known vocabulary — UI labels for these live in app string resources (Constitution IX). */
export type FilterId = "sort" | "language" | "type" | "min_rating" | "pages";

export type SortValue =
  | "latest"
  | "popular_day"
  | "popular_week"
  | "popular_month"
  | "popular_year"
  | "rating";

export type GalleryType =
  | "doujinshi"
  | "manga"
  | "artist_cg"
  | "game_cg"
  | "image_set"
  | "anime"
  | "western"
  | "non_h"
  | "cosplay"
  | "misc"
  | "asian_porn";

/** ISO 639-1 code, e.g. "en", "ja", "zh", "ko". */
export type LanguageCode = string;

export type FilterDefinition =
  | { id: "sort"; kind: "single"; values: SortValue[]; default: SortValue }
  /**
   * (1.2) `multi`: the query's `language` is a list and matches galleries in ANY of the languages;
   * `single` (1.0/1.1) remains valid. A source declares one kind; hosts accept both.
   */
  | { id: "language"; kind: "single" | "multi"; values: LanguageCode[] }
  | { id: "type"; kind: "multi"; values: GalleryType[] }
  | { id: "min_rating"; kind: "number"; min: number; max: number }
  | { id: "pages"; kind: "range"; min: number; max: number };

export interface SearchQuery {
  /**
   * Free text; may contain the source's own tag syntax, e.g. `female:glasses -male:muscle`.
   * (1.1.1) Every source MUST also understand `<namespace>:<name>` with spaces in the name written
   * as `_` (e.g. `female:big_breasts`, `other:full_color`) — hosts build tag searches this way.
   */
  text: string;
  /** Only ids declared in the manifest; unknown ids/values → SourceError("InvalidInput"). */
  filters: {
    sort?: SortValue;
    /** A single code for a `single` language filter, a non-empty list for a `multi` one (1.2). */
    language?: LanguageCode | LanguageCode[];
    type?: GalleryType[];
    min_rating?: number;
    pages?: { min?: number; max?: number };
  };
}

export interface SearchPage {
  items: GallerySummary[];
  /** Opaque; null on the last page. The host passes it back unchanged. */
  nextCursor: string | null;
  /** Total number of results, when the site exposes it. */
  total?: number;
}

export interface TagSuggestion {
  namespace: string | null;            // e.g. "female"; null for untyped tags
  name: string;
  count: number | null;                // galleries with this tag, if known
}

// ---------------------------------------------------------------------------------------------
// Galleries and pages
// ---------------------------------------------------------------------------------------------

export interface Tag {
  /** Well-known: artist, group, parody, character, female, male, mixed, other, language. */
  namespace: string | null;
  name: string;
}

export interface ImageRequest {
  url: string;
  /** Headers required to fetch the image, e.g. { referer: "https://hitomi.la/" }. */
  headers?: Record<string, string>;
  /** (1.1) The image is a cell of a sprite sheet; the host shows only this rectangle (px). */
  crop?: { x: number; y: number; width: number; height: number };
}

export interface GallerySummary {
  id: string;                          // unique within the source; (manifest.id, id) is global
  title: string;
  cover: ImageRequest | null;
  language: LanguageCode | null;
  type: GalleryType | null;
  pageCount: number;
  artists: string[];
  /** Most relevant tags for a card (≤ 20). */
  tags: Tag[];
  /** Gallery page on the site, for "open in browser". */
  webUrl: string;
}

export interface Gallery extends GallerySummary {
  titleOriginal: string | null;        // e.g. Japanese title
  /** All tags. */
  tags: Tag[];
  uploadedAt: string | null;           // ISO 8601
  rating: number | null;               // 0–5
  uploader: string | null;
  pages: Page[];                       // length === pageCount, ordered by index
  /** Source-private, JSON-serialisable; hosts store and return it untouched. */
  sourceData?: unknown;
}

export interface Page {
  index: number;                       // 0-based
  width: number;                       // > 0
  height: number;                      // > 0
  /** "estimated" = aspect-ratio estimate, refined by PageImage.width/height (Constitution IV). */
  size: "exact" | "estimated";
  thumbnail?: ImageRequest | null;
  /** Original file name, if the site exposes one (used for archive entry names). */
  fileName?: string;
}

export interface PageImage extends ImageRequest {
  format?: "webp" | "avif" | "jpeg" | "png" | "gif";
  /** Exact dimensions, when learned while resolving the URL (refines an estimated Page). */
  width?: number;
  height?: number;
  /** Opaque; returned to the source in RetryHint.previous on failure (e.g. E-Hentai `nl`). */
  retryToken?: string;
}

export interface RetryHint {
  attempt: number;                     // 1 on the first retry
  previous: PageImage;
  /** HTTP status of the failed image fetch; absent for network failures. */
  status?: number;
}

export {};
