// Per-source key-value storage for the reference host (in memory, per run).
import { envelopeError, envelopeValue } from "./envelope.ts";

export const STORAGE_LIMITS = { keyChars: 128, valueBytes: 1024 * 1024, totalBytes: 8 * 1024 * 1024 };

export class StorageHost {
  private readonly data = new Map<string, string>();

  seed(values: Record<string, string>): void {
    for (const [k, v] of Object.entries(values)) this.data.set(k, v);
  }

  snapshot(): Record<string, string> {
    return Object.fromEntries(this.data);
  }

  async get(key: string): Promise<string> {
    const bad = checkKey(key);
    if (bad) return bad;
    return envelopeValue(this.data.get(key) ?? null);
  }

  async set(key: string, valueJson: string): Promise<string> {
    const bad = checkKey(key);
    if (bad) return bad;
    const value = JSON.parse(valueJson) as string | null;
    if (value === null) {
      this.data.delete(key);
      return envelopeValue(null);
    }
    const size = byteLength(value);
    if (size > STORAGE_LIMITS.valueBytes) {
      return envelopeError("InvalidInput", `storage.set: value for '${key}' exceeds ${STORAGE_LIMITS.valueBytes} bytes`);
    }
    let total = size + byteLength(key);
    for (const [k, v] of this.data) if (k !== key) total += byteLength(k) + byteLength(v);
    if (total > STORAGE_LIMITS.totalBytes) {
      return envelopeError("InvalidInput", `storage.set: source storage exceeds ${STORAGE_LIMITS.totalBytes} bytes`);
    }
    this.data.set(key, value);
    return envelopeValue(null);
  }
}

function checkKey(key: string): string | null {
  if (key.length === 0 || key.length > STORAGE_LIMITS.keyChars) {
    return envelopeError("InvalidInput", `storage: key must be 1–${STORAGE_LIMITS.keyChars} characters`);
  }
  return null;
}

function byteLength(s: string): number {
  return Buffer.byteLength(s, "utf8");
}
