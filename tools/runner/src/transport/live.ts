// Live transport: real network via Node fetch. Redirects are followed by the host, not here.
import type { RawRequest, RawResponse, Transport } from "./types.ts";

export class LiveTransport implements Transport {
  async send(req: RawRequest): Promise<RawResponse> {
    const res = await fetch(req.url, {
      method: req.method,
      headers: req.headers,
      body: req.body ? Buffer.from(req.body) : undefined,
      redirect: "manual",
      signal: AbortSignal.timeout(req.timeoutMs),
    });
    const headers: [string, string][] = [];
    res.headers.forEach((value, name) => {
      if (name !== "set-cookie") headers.push([name.toLowerCase(), value]);
    });
    for (const cookie of res.headers.getSetCookie()) headers.push(["set-cookie", cookie]);
    return { status: res.status, headers, body: new Uint8Array(await res.arrayBuffer()) };
  }
}
