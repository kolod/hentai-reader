// Evaluated by every host as global code before a source module. Defines SourceError, the only
// error type sources throw (see host-api.d.ts). Plain ES2020, no host capabilities used.
(function () {
  "use strict";
  var KINDS = ["RateLimited", "LoginRequired", "NotFound", "SiteChanged", "InvalidInput", "Network"];

  class SourceError extends Error {
    constructor(kind, message, options) {
      if (KINDS.indexOf(kind) < 0) {
        throw new TypeError("SourceError: unknown kind '" + kind + "'");
      }
      super(String(message));
      this.name = "SourceError";
      this.kind = kind;
      if (options && options.retryAfterSeconds !== undefined) {
        this.retryAfterSeconds = options.retryAfterSeconds;
      }
      if (options && options.details !== undefined) {
        this.details = options.details;
      }
    }
  }

  Object.defineProperty(globalThis, "SourceError", {
    value: SourceError,
    writable: false,
    configurable: false,
    enumerable: false,
  });
})();
