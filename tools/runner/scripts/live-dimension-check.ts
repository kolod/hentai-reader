// SC-006 live check: for 20 random galleries, the first, middle and last page image requests must
// load, and the image header's dimensions must equal Page.width/height. Only the first 64 bytes of
// each image are requested (Range) and nothing is written to disk.
//   node tools/runner/scripts/live-dimension-check.ts [sourceDir] [galleries]
import { locateSource, openSource } from "../src/source.ts";

const dir = process.argv[2] ?? "sources/hitomi";
const wanted = Number(process.argv[3] ?? 20);

type Page = { index: number; width: number; height: number; size: "exact" | "estimated" };

/** Exact sizes must match; estimated sizes may be off by 1 px per side (contract Page.size). */
function sizeOk(p: Page, actual: { width: number; height: number } | null): boolean {
  if (!actual) return false;
  const tolerance = p.size === "exact" ? 0 : 1;
  return Math.abs(actual.width - p.width) <= tolerance && Math.abs(actual.height - p.height) <= tolerance;
}
type Gallery = { id: string; pages: Page[] };

const source = await openSource(locateSource(dir), { mode: "live" });

async function call<T>(fn: string, args: unknown[]): Promise<T> {
  const res = await source.engine.call(fn, args);
  if (!res.ok) throw new Error(`${fn}: ${res.error.message}`);
  return res.value as T;
}

async function imageHeader(url: string, headers: Record<string, string> = {}) {
  const meta = JSON.stringify({ url, headers: { ...headers, range: "bytes=0-63" }, responseType: "bytes" });
  const res = await source.host.httpHost.request(meta, undefined);
  const env = JSON.parse(res.env) as { value?: { status: number; headers: Record<string, string> }; error?: { message: string } };
  if (env.error || !env.value) throw new Error(env.error?.message ?? "no response");
  return { status: env.value.status, type: env.value.headers["content-type"], bytes: res.bytes ?? new Uint8Array() };
}

/** Dimensions from a WebP header (VP8, VP8L, VP8X). */
function webpSize(b: Uint8Array): { width: number; height: number } | null {
  const ascii = (from: number, to: number) => String.fromCharCode(...b.subarray(from, to));
  if (b.length < 30 || ascii(0, 4) !== "RIFF" || ascii(8, 12) !== "WEBP") return null;
  const chunk = ascii(12, 16);
  if (chunk === "VP8X") {
    return { width: 1 + (b[24] | (b[25] << 8) | (b[26] << 16)), height: 1 + (b[27] | (b[28] << 8) | (b[29] << 16)) };
  }
  if (chunk === "VP8 ") return { width: (b[26] | (b[27] << 8)) & 0x3fff, height: (b[28] | (b[29] << 8)) & 0x3fff };
  if (chunk === "VP8L") {
    return { width: 1 + (b[21] | ((b[22] & 0x3f) << 8)), height: 1 + ((b[22] >> 6) | (b[23] << 2) | ((b[24] & 0x0f) << 10)) };
  }
  return null;
}

const first = await call<{ total?: number }>("search", [{ text: "", filters: {} }, null]);
const offset = Math.floor(Math.random() * Math.max(0, (first.total ?? 25) - 25));
const page = await call<{ items: { id: string }[] }>("search", [{ text: "", filters: {} }, `o:${offset}`]);
const ids = page.items.map((i) => i.id).sort(() => Math.random() - 0.5).slice(0, wanted);

let checked = 0;
let failures = 0;
console.log(`gallery     page  expected     actual       status`);
for (const id of ids) {
  const gallery = await call<Gallery>("getGallery", [id]);
  const n = gallery.pages.length;
  for (const index of [...new Set([0, Math.floor(n / 2), n - 1])]) {
    const p = gallery.pages[index];
    const req = await call<{ url: string; headers?: Record<string, string> }>("getPageUrl", [gallery, index]);
    checked++;
    const expected = `${p.width}x${p.height}${p.size === "estimated" ? "~" : ""}`;
    let line = `${id.padEnd(11)} ${String(index).padStart(4)}  ${expected.padEnd(12)} `;
    try {
      const h = await imageHeader(req.url, req.headers);
      const size = webpSize(h.bytes);
      const ok = (h.status === 200 || h.status === 206) && sizeOk(p, size);
      line += `${(size ? `${size.width}x${size.height}` : "?").padEnd(12)} ${h.status} ${ok ? "ok" : "MISMATCH"}`;
      if (!ok) failures++;
    } catch (e) {
      line += `${"-".padEnd(12)} FAILED ${(e as Error).message}`;
      failures++;
    }
    console.log(line);
  }
}
console.log(`\n${checked - failures}/${checked} pages ok`);
process.exitCode = failures ? 1 : 0;
