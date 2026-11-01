// QuickJS adapter for the shared host glue (Android host only; the Windows host has the same
// jint-bridge.js). The host exposes synchronous native functions; async ones start Kotlin work and
// get a request id. This script builds the __hostBridge that contract/host-glue.js expects,
// creating the promises in JavaScript, and returns the functions the host uses on its engine
// thread: settle(id, value) to resolve a pending bridge promise, settleHttp(id, env, bytes) for
// HTTP responses, and watch(promise, id) to learn when a source call has finished.
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

  const bridge = {
    // Byte bodies cross as an ArrayBuffer (copied by the JNI shim).
    http: start(native.http),
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
    settleHttp: function (id, env, bytes) {
      const value = { env: env };
      if (bytes !== undefined) value.bytes = bytes;
      settle(id, value);
    },
    watch: function (promise, id) {
      promise.then(function (json) { native.done(id, json); });
    },
  };
})(globalThis.__native, globalThis.__frozenNow);
