// Runner-internal glue, evaluated as global code after contract/prelude.js and before the source.
// Builds the contract globals on top of the private __hostBridge, then deletes the bridge so the
// source can reach the host only through the contract. Every bridge call returns a JSON envelope
// `{ value }` or `{ error: { kind, message, options } }`, so no exceptions cross the boundary.
// The value of this script is the `bind(exports)` function used by the engine.
(function () {
  "use strict";
  const bridge = globalThis.__hostBridge;
  delete globalThis.__hostBridge;

  function unwrap(envelope) {
    const env = JSON.parse(envelope);
    if (env.error) {
      throw new SourceError(env.error.kind, env.error.message, env.error.options);
    }
    return env.value;
  }

  function invalid(message) {
    return new SourceError("InvalidInput", message);
  }

  // --- http --------------------------------------------------------------------------------

  const http = Object.freeze({
    request(req) {
      if (req === null || typeof req !== "object") {
        return Promise.reject(invalid("http.request: request object required"));
      }
      const bytesBody = req.body instanceof Uint8Array;
      const meta = JSON.stringify({
        method: req.method,
        url: req.url,
        headers: req.headers,
        body: bytesBody ? undefined : req.body,
        responseType: req.responseType,
        timeoutMs: req.timeoutMs,
      });
      const body = bytesBody
        ? req.body.buffer.slice(req.body.byteOffset, req.body.byteOffset + req.body.byteLength)
        : undefined;
      return bridge.http(meta, body).then(function (res) {
        const value = unwrap(res.env);
        return {
          status: value.status,
          url: value.url,
          headers: value.headers,
          body: value.bytes ? new Uint8Array(res.bytes) : value.text,
        };
      });
    },
  });

  // --- html --------------------------------------------------------------------------------

  const elementIds = new WeakMap();

  function element(desc) {
    const id = desc.id;
    const el = Object.freeze({
      tag: desc.tag,
      text() { return unwrap(bridge.htmlCall("text", id, "")); },
      ownText() { return unwrap(bridge.htmlCall("ownText", id, "")); },
      html() { return unwrap(bridge.htmlCall("html", id, "")); },
      outerHtml() { return unwrap(bridge.htmlCall("outerHtml", id, "")); },
      attr(name) { return unwrap(bridge.htmlCall("attr", id, String(name))); },
    });
    elementIds.set(el, id);
    return el;
  }

  const html = Object.freeze({
    select(target, selector) {
      if (typeof selector !== "string") {
        throw invalid("html.select: selector must be a string");
      }
      let found;
      if (typeof target === "string") {
        found = unwrap(bridge.htmlSelect(target, -1, selector));
      } else {
        const id = elementIds.get(target);
        if (id === undefined) {
          throw invalid("html.select: target must be a markup string or an element from html.select");
        }
        found = unwrap(bridge.htmlSelect("", id, selector));
      }
      return found.map(element);
    },
  });

  // --- json, storage, sleep, console ---------------------------------------------------------

  const json = Object.freeze({
    parse(text) { return JSON.parse(text); },
  });

  const storage = Object.freeze({
    get(key) {
      return bridge.storageGet(String(key)).then(unwrap);
    },
    set(key, value) {
      return bridge.storageSet(String(key), JSON.stringify(value === null ? null : String(value))).then(unwrap);
    },
  });

  function sleep(ms) {
    return bridge.sleep(Number(ms)).then(unwrap);
  }

  function format(arg) {
    if (typeof arg === "string") return arg;
    if (arg instanceof Error) return arg.name + ": " + arg.message;
    try {
      return JSON.stringify(arg);
    } catch (e) {
      return String(arg);
    }
  }

  function logger(level) {
    return function () {
      bridge.log(level, Array.prototype.map.call(arguments, format).join(" "));
    };
  }

  const console = Object.freeze({ log: logger("log"), warn: logger("warn"), error: logger("error") });

  // --- frozen clock (offline mode) -----------------------------------------------------------

  if (typeof bridge.frozenNow === "number") {
    const RealDate = Date;
    const now = bridge.frozenNow;
    const FrozenDate = function Date() {
      if (!new.target) return new RealDate(now).toString();
      return arguments.length === 0 ? new RealDate(now) : new RealDate(...arguments);
    };
    FrozenDate.prototype = RealDate.prototype;
    FrozenDate.now = function () { return now; };
    FrozenDate.parse = RealDate.parse;
    FrozenDate.UTC = RealDate.UTC;
    globalThis.Date = FrozenDate;
  }

  for (const [name, value] of Object.entries({ http, html, json, storage, sleep, console })) {
    Object.defineProperty(globalThis, name, { value, writable: false, configurable: false, enumerable: false });
  }

  // --- invocation ----------------------------------------------------------------------------

  function describeError(e) {
    if (e instanceof SourceError) {
      return {
        type: "SourceError",
        kind: e.kind,
        message: e.message,
        retryAfterSeconds: e.retryAfterSeconds,
        details: e.details,
      };
    }
    if (e instanceof Error) {
      return { type: "ScriptError", name: e.name, message: e.message, stack: e.stack };
    }
    return { type: "ScriptError", name: "Thrown", message: format(e) };
  }

  return function bind(exports) {
    return function invoke(fn, argsJson) {
      return Promise.resolve()
        .then(function () {
          if (fn === "__describe") {
            const names = Object.keys(exports);
            return {
              manifest: exports.manifest === undefined ? null : exports.manifest,
              functions: names.filter(function (n) { return typeof exports[n] === "function"; }),
            };
          }
          const f = exports[fn];
          if (typeof f !== "function") {
            throw invalid("source does not export function '" + fn + "'");
          }
          return f.apply(undefined, JSON.parse(argsJson));
        })
        .then(
          function (value) {
            return JSON.stringify({ ok: true, value: value === undefined ? null : value });
          },
          function (e) {
            return JSON.stringify({ ok: false, error: describeError(e) });
          },
        );
    };
  };
})();
