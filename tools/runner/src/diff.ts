// Structural JSON diff: one line per changed, added or removed path.

const MAX_LINES = 50;

export function diff(expected: unknown, actual: unknown): string[] {
  const lines: string[] = [];
  walk(expected, actual, "", lines);
  if (lines.length > MAX_LINES) {
    const more = lines.length - MAX_LINES;
    return [...lines.slice(0, MAX_LINES), `… ${more} more difference(s)`];
  }
  return lines;
}

function walk(a: unknown, b: unknown, path: string, out: string[]): void {
  if (Object.is(a, b)) return;
  const label = path || "(root)";
  if (Array.isArray(a) && Array.isArray(b)) {
    const n = Math.max(a.length, b.length);
    for (let i = 0; i < n; i++) {
      const p = `${path}[${i}]`;
      if (i >= b.length) out.push(`- ${p}: ${show(a[i])}`);
      else if (i >= a.length) out.push(`+ ${p}: ${show(b[i])}`);
      else walk(a[i], b[i], p, out);
    }
    return;
  }
  if (isObject(a) && isObject(b)) {
    const keys = new Set([...Object.keys(a), ...Object.keys(b)]);
    for (const k of [...keys].sort()) {
      const p = path ? `${path}.${k}` : k;
      if (!(k in b)) out.push(`- ${p}: ${show(a[k])}`);
      else if (!(k in a)) out.push(`+ ${p}: ${show(b[k])}`);
      else walk(a[k], b[k], p, out);
    }
    return;
  }
  out.push(`~ ${label}: ${show(a)} → ${show(b)}`);
}

function isObject(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

function show(v: unknown): string {
  const s = JSON.stringify(v);
  return s === undefined ? "undefined" : s.length > 80 ? `${s.slice(0, 77)}…` : s;
}
