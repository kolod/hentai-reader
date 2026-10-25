// manifest.loginPage rules (contract 1.2, specs/005-windows-reader/contracts/contract-1.2-login-page.md).
// The same rules are applied by every host when it loads a source.
import { domainMatches } from "./host/cookies.ts";

/** True when `host` is allowed by a manifest domain list ("site.org" or "*.site.org"). */
export function hostInDomains(host: string, domains: readonly string[]): boolean {
  host = host.toLowerCase();
  return domains.some((d) => {
    const pattern = d.toLowerCase();
    return pattern.startsWith("*.") ? domainMatches(host, pattern.slice(2)) && host !== pattern.slice(2) : host === pattern;
  });
}

/** Violations of the loginPage rules; empty when the manifest has none or it is valid. */
export function loginPageViolations(manifest: Record<string, unknown>): string[] {
  const page = manifest.loginPage as { url?: unknown; cookies?: unknown; shareWith?: unknown } | undefined;
  if (page === undefined) return [];
  const out: string[] = [];
  const domains = (manifest.domains ?? []) as string[];
  const m = /^(\d+)\.(\d+)$/.exec(String(manifest.contract));
  if (!m || (Number(m[1]) === 1 && Number(m[2]) < 2)) out.push("manifest.loginPage: requires contract 1.2");
  if (manifest.login === "none") out.push('manifest.loginPage: not allowed when login is "none"');

  let url: URL | null = null;
  try {
    url = typeof page.url === "string" ? new URL(page.url) : null;
  } catch {
    url = null;
  }
  if (!url || url.protocol !== "https:") out.push("manifest.loginPage.url: must be an absolute https URL");
  else if (!hostInDomains(url.hostname, domains)) out.push(`manifest.loginPage.url: host ${url.hostname} is outside manifest.domains`);

  const cookies = Array.isArray(page.cookies) ? page.cookies : [];
  if (cookies.length === 0 || cookies.some((c) => typeof c !== "string" || c.length === 0) || new Set(cookies).size !== cookies.length) {
    out.push("manifest.loginPage.cookies: must list at least one cookie name, each once");
  }
  for (const d of Array.isArray(page.shareWith) ? page.shareWith : []) {
    if (typeof d !== "string" || !hostInDomains(d, domains)) out.push(`manifest.loginPage.shareWith: ${String(d)} is outside manifest.domains`);
  }
  return out;
}
