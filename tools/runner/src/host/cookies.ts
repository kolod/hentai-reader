// Minimal per-source cookie jar: Set-Cookie parsing, domain/path matching, Netscape file seeding.

interface Cookie {
  name: string;
  value: string;
  domain: string;
  hostOnly: boolean;
  path: string;
  expires: number | null;
}

export class CookieJar {
  private cookies: Cookie[] = [];

  /** Stores cookies from Set-Cookie headers received for `url`. */
  store(url: string, setCookieHeaders: string[]): void {
    const u = new URL(url);
    for (const header of setCookieHeaders) {
      const [pair, ...attrs] = header.split(";");
      const eq = pair.indexOf("=");
      if (eq <= 0) continue;
      const cookie: Cookie = {
        name: pair.slice(0, eq).trim(),
        value: pair.slice(eq + 1).trim(),
        domain: u.hostname.toLowerCase(),
        hostOnly: true,
        path: "/",
        expires: null,
      };
      for (const attr of attrs) {
        const [rawKey, ...rest] = attr.split("=");
        const key = rawKey.trim().toLowerCase();
        const val = rest.join("=").trim();
        if (key === "domain" && val) {
          const domain = val.replace(/^\./, "").toLowerCase();
          if (!domainMatches(u.hostname.toLowerCase(), domain)) continue;
          cookie.domain = domain;
          cookie.hostOnly = false;
        } else if (key === "path" && val.startsWith("/")) {
          cookie.path = val;
        } else if (key === "max-age") {
          cookie.expires = Date.now() + Number(val) * 1000;
        } else if (key === "expires" && cookie.expires === null) {
          const t = Date.parse(val);
          if (!Number.isNaN(t)) cookie.expires = t;
        }
      }
      this.put(cookie);
    }
  }

  /** Seeds a cookie string `k=v; k2=v2` for a domain (and its subdomains). */
  seed(domain: string, cookieString: string): void {
    for (const part of cookieString.split(";")) {
      const eq = part.indexOf("=");
      if (eq <= 0) continue;
      this.put({
        name: part.slice(0, eq).trim(),
        value: part.slice(eq + 1).trim(),
        domain: domain.replace(/^\./, "").toLowerCase(),
        hostOnly: false,
        path: "/",
        expires: null,
      });
    }
  }

  /** Seeds from a Netscape cookies.txt (domain, subdomains, path, secure, expiry, name, value). */
  seedNetscape(text: string): void {
    for (const line of text.split(/\r?\n/)) {
      const trimmed = line.replace(/^#HttpOnly_/, "");
      if (!trimmed || trimmed.startsWith("#")) continue;
      const f = trimmed.split("\t");
      if (f.length < 7) continue;
      const expiry = Number(f[4]);
      this.put({
        name: f[5],
        value: f[6],
        domain: f[0].replace(/^\./, "").toLowerCase(),
        hostOnly: f[1].toUpperCase() !== "TRUE",
        path: f[2] || "/",
        expires: expiry > 0 ? expiry * 1000 : null,
      });
    }
  }

  /** Cookie pairs applicable to `url`, as name → value. */
  for(url: string): Map<string, string> {
    const u = new URL(url);
    const host = u.hostname.toLowerCase();
    const now = Date.now();
    const out = new Map<string, string>();
    for (const c of this.cookies) {
      if (c.expires !== null && c.expires <= now) continue;
      const hostOk = c.hostOnly ? host === c.domain : domainMatches(host, c.domain);
      if (hostOk && u.pathname.startsWith(c.path)) out.set(c.name, c.value);
    }
    return out;
  }

  private put(cookie: Cookie): void {
    this.cookies = this.cookies.filter(
      (c) => !(c.name === cookie.name && c.domain === cookie.domain && c.path === cookie.path),
    );
    this.cookies.push(cookie);
  }
}

export function domainMatches(host: string, domain: string): boolean {
  return host === domain || host.endsWith(`.${domain}`);
}

/** Merges jar cookies with a script-supplied Cookie header; script values win on name clashes. */
export function mergeCookieHeader(jar: Map<string, string>, scriptHeader: string | undefined): string | undefined {
  const merged = new Map(jar);
  if (scriptHeader) {
    for (const part of scriptHeader.split(";")) {
      const eq = part.indexOf("=");
      if (eq > 0) merged.set(part.slice(0, eq).trim(), part.slice(eq + 1).trim());
    }
  }
  if (merged.size === 0) return undefined;
  return [...merged].map(([k, v]) => `${k}=${v}`).join("; ");
}
