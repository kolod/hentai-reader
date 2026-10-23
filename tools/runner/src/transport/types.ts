// What the host's HTTP layer needs from a network implementation (offline, live, recording).

export interface RawRequest {
  method: "GET" | "POST";
  url: string;
  /** Lower-case names, exactly as sent (including cookies). */
  headers: Record<string, string>;
  body?: Uint8Array;
  timeoutMs: number;
}

export interface RawResponse {
  status: number;
  /** All headers as received, lower-case names, including every set-cookie. */
  headers: [string, string][];
  body: Uint8Array;
}

export interface Transport {
  send(req: RawRequest): Promise<RawResponse>;
}
