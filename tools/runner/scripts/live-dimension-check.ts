// Live page-size check (001 SC-006, 002 SC-003): for N random galleries, the first, middle and last
// page image requests must load; exact sizes (Page `exact`, or PageImage width/height) must equal
// the served image's header; estimated sizes must match its aspect ratio within 2 %. Only the
// first 64 KiB of each image are requested (Range) and nothing is written to disk.
//   node tools/runner/scripts/live-dimension-check.ts [sourceDir] [galleries] [setting=value …]
import { callAndValidate } from "../src/commands/run.ts";
import { locateSource, openSource } from "../src/source.ts";

const [dir = "sources/hitomi", count = "20", ...settingArgs] = process.argv.slice(2);
const wanted = Number(count);
const settings = Object.fromEntries(settingArgs.map((s) => s.split("=") as [string, string]));
const ASPECT_TOLERANCE = 0.02;

type Size = { width: number; height: number };
type Page = Size & { index: number; size: "exact" | "estimated" };
type Gallery = { id: string; pages: Page[] };
type PageImage = { url: string; headers?: Record<string, string>; width?: number; height?: number };

const source = await openSource(locateSource(dir), { mode: "live" });

async function call<T>(fn: string, args: unknown[]): Promise<T> {
  const { result, violations } = await callAndValidate(source, fn, args, settings);
  if (!result.ok) throw new Error(`${fn}: ${result.error.message}`);
  if (violations.length) throw new Error(`${fn}: contract violations: ${violations.join("; ")}`);
  return result.value as T;
}

async function imageHeader(url: string, headers: Record<string, string> = {}) {
  const meta = JSON.stringify({ url, headers: { ...headers, range: "bytes=0-65535" }, responseType: "bytes" });
  const res = await source.host.httpHost.request(meta, undefined);
  const env = JSON.parse(res.env) as { value?: { status: number }; error?: { message: string } };
  if (env.error || !env.value) throw new Error(env.error?.message ?? "no response");
  return { status: env.value.status, bytes: res.bytes ?? new Uint8Array() };
}

/** Dimensions from a WebP (VP8/VP8L/VP8X), PNG, GIF or JPEG header. */
function imageSize(b: Uint8Array): Size | null {
  const ascii = (from: number, to: number) => String.fromCharCode(...b.subarray(from, to));
  const be16 = (i: number) => (b[i] << 8) | b[i + 1];
  if (b.length >= 30 && ascii(0, 4) === "RIFF" && ascii(8, 12) === "WEBP") {
    const chunk = ascii(12, 16);
    if (chunk === "VP8X") return { width: 1 + (b[24] | (b[25] << 8) | (b[26] << 16)), height: 1 + (b[27] | (b[28] << 8) | (b[29] << 16)) };
    if (chunk === "VP8 ") return { width: (b[26] | (b[27] << 8)) & 0x3fff, height: (b[28] | (b[29] << 8)) & 0x3fff };
    if (chunk === "VP8L") return { width: 1 + (b[21] | ((b[22] & 0x3f) << 8)), height: 1 + ((b[22] >> 6) | (b[23] << 2) | ((b[24] & 0x0f) << 10)) };
    return null;
  }
  if (b.length >= 24 && b[0] === 0x89 && ascii(1, 4) === "PNG") {
    const v = new DataView(b.buffer, b.byteOffset, b.byteLength);
    return { width: v.getUint32(16), height: v.getUint32(20) };
  }
  if (b.length >= 10 && ascii(0, 4) === "GIF8") return { width: b[6] | (b[7] << 8), height: b[8] | (b[9] << 8) };
  if (b.length >= 4 && b[0] === 0xff && b[1] === 0xd8) {
    for (let i = 2; i + 9 < b.length; ) {
      if (b[i] !== 0xff) return null;
      const marker = b[i + 1];
      if (marker >= 0xc0 && marker <= 0xcf && marker !== 0xc4 && marker !== 0xc8 && marker !== 0xcc) {
        return { height: be16(i + 5), width: be16(i + 7) };
      }
      i += 2 + be16(i + 2);
    }
  }
  return null;
}

function sizeOk(p: Page, image: PageImage, actual: Size | null): string | null {
  if (!actual) return "unreadable header";
  if (image.width !== undefined && image.height !== undefined) {
    if (image.width !== actual.width || image.height !== actual.height) return `PageImage ${image.width}x${image.height}`;
  }
  if (p.size === "exact") return p.width === actual.width && p.height === actual.height ? null : "exact size differs";
  const deviation = Math.abs(p.width / p.height / (actual.width / actual.height) - 1);
  return deviation <= ASPECT_TOLERANCE ? null : `aspect off by ${(deviation * 100).toFixed(1)} %`;
}

const first = await call<{ items: { id: string }[]; nextCursor: string | null }>("search", [{ text: "", filters: {} }, null]);
let items = first.items;
if (first.nextCursor) {
  // A second page widens the sample a little without guessing site-specific cursor formats.
  items = [...items, ...(await call<{ items: { id: string }[] }>("search", [{ text: "", filters: {} }, first.nextCursor])).items];
}
const ids = items.map((i) => i.id).sort(() => Math.random() - 0.5).slice(0, wanted);

let checked = 0;
let failures = 0;
console.log(`gallery                    page  page size     served       status`);
for (const id of ids) {
  const gallery = await call<Gallery>("getGallery", [id]);
  const n = gallery.pages.length;
  for (const index of [...new Set([0, Math.floor(n / 2), n - 1])]) {
    const p = gallery.pages[index];
    checked++;
    const expected = `${p.width}x${p.height}${p.size === "estimated" ? "~" : ""}`;
    let line = `${id.padEnd(26)} ${String(index).padStart(4)}  ${expected.padEnd(13)} `;
    try {
      const req = await call<PageImage>("getPageUrl", [gallery, index]);
      const h = await imageHeader(req.url, req.headers);
      const actual = imageSize(h.bytes);
      const problem = h.status === 200 || h.status === 206 ? sizeOk(p, req, actual) : `HTTP ${h.status}`;
      line += `${(actual ? `${actual.width}x${actual.height}` : "?").padEnd(12)} ${problem ? `MISMATCH (${problem})` : "ok"}`;
      if (problem) failures++;
    } catch (e) {
      line += `${"-".padEnd(12)} FAILED ${(e as Error).message}`;
      failures++;
    }
    console.log(line);
  }
}
console.log(`\n${checked - failures}/${checked} pages ok`);
process.exitCode = failures ? 1 : 0;
