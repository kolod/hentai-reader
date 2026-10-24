// Jint adapter for the shared host glue (Windows host only). The host exposes synchronous native
// functions; async ones start .NET work and get a request id. This script builds the
// __hostBridge that contract/host-glue.js expects, creating the promises in JavaScript, and
// returns the functions the host uses on its engine thread: settle(id, value) to resolve a pending
// bridge promise, and watch(promise, id) to learn when a source call has finished.
(function (native, frozenNow) {
  "use strict";
  const pending = new Map();
  let nextId = 1;

  function start(fn) {
    return function () {
      const id = nextId++;
      const args = Array.prototype.slice.call(arguments);
      return new Promise(function (resolve) {
        pending.set(id, resolve);
        fn.apply(undefined, [id].concat(args));
      });
    };
  }

  // Byte bodies cross into .NET as plain number arrays (small; only POST bodies).
  function http(id, meta, body) {
    native.http(id, meta, body === undefined ? undefined : Array.from(new Uint8Array(body)));
  }

  const bridge = {
    http: start(http),
    htmlSelect: native.htmlSelect,
    htmlCall: native.htmlCall,
    storageGet: start(native.storageGet),
    storageSet: start(native.storageSet),
    sleep: start(native.sleep),
    log: native.log,
  };
  if (typeof frozenNow === "number") bridge.frozenNow = frozenNow;
  globalThis.__hostBridge = bridge;
  delete globalThis.__native;
  delete globalThis.__frozenNow;

  function settle(id, value) {
    const resolve = pending.get(id);
    if (!resolve) return;
    pending.delete(id);
    resolve(value);
  }

  return {
    settle: settle,
    // Response bodies arrive as base64 and become an ArrayBuffer here, on the engine thread.
    settleHttp: function (id, env, base64) {
      const value = { env: env };
      if (typeof base64 === "string") value.bytes = decodeBase64(base64);
      settle(id, value);
    },
    watch: function (promise, id) {
      promise.then(function (json) { native.done(id, json); });
    },
  };

  function decodeBase64(s) {
    const table = new Uint8Array(128);
    const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    for (let i = 0; i < alphabet.length; i++) table[alphabet.charCodeAt(i)] = i;
    const len = s.length;
    const pad = s.endsWith("==") ? 2 : s.endsWith("=") ? 1 : 0;
    const out = new Uint8Array((len * 3) / 4 - pad);
    let o = 0;
    for (let i = 0; i < len; i += 4) {
      const n = (table[s.charCodeAt(i)] << 18) | (table[s.charCodeAt(i + 1)] << 12) |
        (table[s.charCodeAt(i + 2)] << 6) | table[s.charCodeAt(i + 3)];
      if (o < out.length) out[o++] = (n >> 16) & 255;
      if (o < out.length) out[o++] = (n >> 8) & 255;
      if (o < out.length) out[o++] = n & 255;
    }
    return out.buffer;
  }
})(globalThis.__native, globalThis.__frozenNow);
