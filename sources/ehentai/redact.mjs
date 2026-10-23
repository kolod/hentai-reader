// Fixture redaction for the E-Hentai source (run by source-runner, never by the apps).
// Each response is rebuilt from only the parts ehentai.js reads, so gallery titles, tags, comments,
// user names, member ids and e-mail addresses have nowhere to survive. Tags the test cases need are
// kept by an allowlist; everything else becomes a neutral placeholder.
// Must be idempotent: `source-runner check` re-applies it to prove the fixtures are redacted.

const KEEP_TAGS = new Set(["female:glasses", "female:sunglasses"]);
const API = /^https:\/\/(api\.e-hentai\.org|s\.exhentai\.org)\/api\.php$/;
const SITE = /^https:\/\/(e-hentai\.org|exhentai\.org)(\/.*)?$/;

/**
 * The ExHentai access check redirects through forums.e-hentai.org/remoteapi.php?ex=… and back to
 * exhentai.org/?poni=…; both values are derived from the account (poni starts with the member id).
 */
export function redactUrl(url) {
  return url.replace(/([?&](?:ex|poni)=)[^&#]+/g, "$1redacted");
}

export function redact(url, text) {
  if (API.test(url)) return redactApi(text);
  // Forum hops (ExHentai access check) and the login page can carry account data: keep nothing.
  if (url.startsWith("https://forums.e-hentai.org/")) return page("<p>forums</p>");
  const m = SITE.exec(url);
  if (!m) return text;
  const path = m[2] ?? "/";
  if (/temporarily banned/i.test(text)) return keepSentence(text, /[^.<>]*temporarily banned[^<]*/i);
  if (/exceeded your image viewing limits/i.test(text)) return keepSentence(text, /[^.<>]*exceeded your image viewing limits[^<]*/i);
  if (path.startsWith("/g/")) return redactGallery(text);
  if (path.startsWith("/s/")) return redactViewer(text);
  if (path.startsWith("/home.php")) return redactHome(text);
  if (path.startsWith("/bounce_login.php")) return page("<p>login</p>");
  if (path === "/" || path.startsWith("/?") || path.startsWith("/toplist.php")) return redactListing(text);
  return text;
}

function keepSentence(text, re) {
  const m = re.exec(text);
  return m ? `<html><body><p>${m[0].trim()}</p></body></html>` : text;
}

// --- API ----------------------------------------------------------------------------------------

function redactApi(text) {
  let data;
  try {
    data = JSON.parse(text);
  } catch {
    return text;
  }
  if (Array.isArray(data.gmetadata)) {
    data.gmetadata = data.gmetadata.map((g) => {
      if (g.error) return { gid: g.gid, error: g.error };
      return {
        gid: g.gid,
        token: g.token,
        title: `Gallery ${g.gid}`,
        title_jpn: "",
        category: g.category,
        thumb: g.thumb,
        uploader: "uploader",
        posted: g.posted,
        filecount: g.filecount,
        filesize: g.filesize,
        expunged: g.expunged,
        rating: g.rating,
        torrentcount: "0",
        torrents: [],
        tags: (g.tags ?? []).map((t, i) => redactTag(t, i)),
      };
    });
    return JSON.stringify(data);
  }
  if (data.tags && typeof data.tags === "object" && !Array.isArray(data.tags)) {
    const out = {};
    Object.entries(data.tags).forEach(([id, t], i) => {
      const keep = KEEP_TAGS.has(`${t.ns}:${t.tn}`);
      const entry = { ns: t.ns, tn: keep ? t.tn : `redacted ${i + 1}` };
      if (t.mtn !== undefined) {
        entry.mns = t.mns;
        entry.mtn = KEEP_TAGS.has(`${t.mns}:${t.mtn}`) ? t.mtn : `redacted master ${i + 1}`;
      }
      out[id] = entry;
    });
    return JSON.stringify({ tags: out });
  }
  return text;
}

function redactTag(raw, i) {
  const colon = raw.indexOf(":");
  const ns = colon < 0 ? "" : raw.slice(0, colon);
  if (ns === "language" || KEEP_TAGS.has(raw)) return raw;
  return ns ? `${ns}:redacted ${i + 1}` : `redacted ${i + 1}`;
}

// --- HTML pages ---------------------------------------------------------------------------------

const page = (body) => `<!DOCTYPE html>\n<html><body>\n${body}\n</body></html>`;

function redactListing(text) {
  if (/No hits found/.test(text)) return page("<p>No hits found</p>");
  const start = text.indexOf('class="itg');
  const links = [];
  const seen = new Set();
  if (start >= 0) {
    for (const m of text.slice(start).matchAll(/https:\/\/(?:e-hentai|exhentai)\.org\/g\/(\d+)\/([0-9a-f]{10})\//g)) {
      if (seen.has(m[1])) continue;
      seen.add(m[1]);
      links.push(m[0]);
    }
  }
  const parts = [];
  if (start >= 0) parts.push(`<table class="itg">${links.map((l) => `<tr><td><a href="${l}">gallery</a></td></tr>`).join("")}</table>`);
  const unext = /id="unext" href="([^"]+)"/.exec(text);
  if (unext) parts.push(`<a id="unext" href="${unext[1]}">next</a>`);
  const pager = [...new Set([...text.matchAll(/toplist\.php\?tl=\d+(?:&amp;|&)p=\d+/g)].map((m) => m[0]))];
  if (pager.length) parts.push(`<div class="pager">${pager.map((h) => `<a href="${h}">page</a>`).join("")}</div>`);
  return page(parts.join("\n"));
}

function redactGallery(text) {
  if (/Gallery Not Available|This gallery has been removed|Key missing, or incorrect key provided/i.test(text)) {
    return page("<p>Gallery Not Available</p>");
  }
  const cells = [...text.matchAll(/<a href="(https:\/\/[^"]+\/s\/[0-9a-f]+\/\d+-\d+)"[^>]*>\s*<div title="Page (\d+):\s*([^"]*)" style="([^"]*)"/g)];
  if (cells.length === 0 && /nw=always/.test(text)) {
    return page('<p>Content Warning</p><a href="?nw=always">View Gallery</a>');
  }
  const gdt = cells
    .map(([, href, n, name, style]) => `<a href="${href}"><div title="Page ${n}: ${neutralName(n, name)}" style="${style}"></div></a>`)
    .join("");
  const pager = [...new Set([...text.matchAll(/href="(https:\/\/[^"]+\/g\/\d+\/[0-9a-f]{10}\/\?p=\d+)"/g)].map((m) => m[1]))];
  return page(`<div id="gdt">${gdt}</div>\n<table class="ptt"><tr>${pager.map((h) => `<td><a href="${h}">page</a></td>`).join("")}</tr></table>`);
}

function neutralName(n, name) {
  const ext = /\.[a-z0-9]+$/i.exec(name)?.[0] ?? "";
  return `${String(n).padStart(3, "0")}${ext.toLowerCase()}`;
}

function redactViewer(text) {
  const img = /<img id="img" src="([^"]+)"(?: style="([^"]*)")?/.exec(text);
  const size = /<div>([^<]*?) :: (\d+) x (\d+) :: ([^<]*)<\/div>/.exec(text);
  const nl = /nl\('([^']+)'\)/.exec(text);
  const original = /<a href="([^"]+\/fullimg\/[^"]+)">Download original (\d+) x (\d+)([^<]*)<\/a>/.exec(text);
  const parts = [];
  if (img) parts.push(`<img id="img" src="${img[1]}" style="${img[2] ?? ""}"${nl ? ` onerror="this.onerror=null; nl('${nl[1]}')"` : ""} />`);
  if (size) parts.push(`<div>${neutralName(1, size[1])} :: ${size[2]} x ${size[3]} :: ${size[4]}</div>`);
  if (nl && !img) parts.push(`<a href="#" onclick="return nl('${nl[1]}')">reload</a>`);
  if (original) {
    const url = original[1].replace(/\/[^/]+$/, (last) => `/${neutralName(1, last)}`);
    parts.push(`<a href="${url}">Download original ${original[2]} x ${original[3]}${original[4]}</a>`);
  }
  return page(parts.join("\n"));
}

function redactHome(text) {
  if (!/Image Limits/.test(text)) return page("<p>not logged in</p>");
  // Only the generic limit sentences survive; the rest of the page (user name, ids, GP, links) goes.
  const sentences = [...text.matchAll(/You are currently [^<]*(?:<strong>[\d,]+<\/strong>[^<]*)*/g)].map((m) => `<p>${m[0].trim()}</p>`);
  return page(`<h2>Image Limits</h2>\n${sentences.join("\n")}`);
}
