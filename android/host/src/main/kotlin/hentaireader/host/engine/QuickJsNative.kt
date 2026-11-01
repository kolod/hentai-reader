package hentaireader.host.engine

/**
 * JNI entry points of libhrquickjs (android/quickjs/src/main/cpp/qjs_jni.c). Values are handles
 * (`Long`, 0 = failed, see [lastError]). Arguments for [call]: String, Double, Boolean, ByteArray
 * (→ ArrayBuffer), a boxed handle `Long` (→ that value) or null (→ undefined).
 * Every call must happen on the engine's own thread ([EngineThread]).
 */
internal object QuickJsNative {
    init {
        System.loadLibrary("hrquickjs")
    }

    /** Receives `__native.<name>(...)` calls; returns a String (→ JS string) or null (→ undefined). */
    interface Callbacks {
        fun onNativeCall(index: Int, args: Array<Any?>): Any?
    }

    @JvmStatic external fun create(memoryLimit: Long, callbacks: Callbacks, nativeNames: Array<String>): Long
    @JvmStatic external fun destroy(engine: Long)
    @JvmStatic external fun setTimeout(engine: Long, timeoutMs: Long)
    @JvmStatic external fun cancel(engine: Long)
    @JvmStatic external fun wasInterrupted(engine: Long): Boolean
    @JvmStatic external fun evalGlobal(engine: Long, code: String, file: String): Long
    @JvmStatic external fun evalModule(engine: Long, code: String, file: String): Long
    @JvmStatic external fun call(engine: Long, fn: Long, args: Array<Any?>?): Long
    @JvmStatic external fun getProperty(engine: Long, obj: Long, name: String): Long
    @JvmStatic external fun toJavaString(engine: Long, handle: Long): String?
    @JvmStatic external fun free(engine: Long, handle: Long)
    @JvmStatic external fun executePendingJobs(engine: Long): Boolean
    @JvmStatic external fun lastError(engine: Long): String?
}
