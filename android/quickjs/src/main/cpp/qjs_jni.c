// JNI shim between hentaireader.host.engine.QuickJsNative and the vendored QuickJS
// (specs/007-android-app research R2). It exposes only what the host bridge needs: one runtime and
// context per engine, the contract limits (memory, per-call deadline, cancel), global and module
// evaluation, function calls on value handles, synchronous native callbacks into Kotlin, the job
// queue and ArrayBuffer transfer. Promises for the async bridge members are created in JavaScript
// (engine/qjs-bridge.js), so nothing here is asynchronous.
//
// Every function must be called on the engine's own thread: QuickJS is single-threaded and records
// the stack top of the thread that created the runtime. Strings cross as UTF-16 (jstring) and are
// converted here, so characters outside the BMP and lone surrogates survive both directions.

#include <jni.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include "quickjs.h"

#ifdef _WIN32
#include <windows.h>
#else
#include <time.h>
#endif

typedef struct {
    JSRuntime *rt;
    JSContext *ctx;
    JNIEnv *env;           // env of the engine thread, refreshed on every entry
    jobject callbacks;     // global ref to the Kotlin callback object
    jmethodID on_call;     // Object onNativeCall(int index, Object[] args)
    int64_t deadline_ms;   // 0 = no deadline
    volatile int cancel;   // set from any thread
    int interrupted;       // the interrupt handler stopped the script
    char *last_error;      // JSON {name, message, stack, interrupted}
} Engine;

static jclass g_object_class;
static jclass g_string_class;
static jclass g_double_class;
static jclass g_boolean_class;
static jclass g_long_class;
static jclass g_bytes_class;
static jmethodID g_double_value_of;
static jmethodID g_double_value;
static jmethodID g_boolean_value_of;
static jmethodID g_boolean_value;
static jmethodID g_long_value;
static jmethodID g_throwable_to_string;

#define ENGINE(p) ((Engine *)(intptr_t)(p))
#define HANDLE(h) (*(JSValue *)(intptr_t)(h))

static int64_t now_ms(void) {
#ifdef _WIN32
    return (int64_t)GetTickCount64();
#else
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
#endif
}

static int interrupt_handler(JSRuntime *rt, void *opaque) {
    (void)rt;
    Engine *e = opaque;
    if (e->cancel || (e->deadline_ms != 0 && now_ms() > e->deadline_ms)) {
        e->interrupted = 1;
        return 1;
    }
    return 0;
}

static jlong wrap(JSValue v) {
    JSValue *p = malloc(sizeof(JSValue));
    *p = v;
    return (jlong)(intptr_t)p;
}

// --- strings ------------------------------------------------------------------------------------

// QuickJS strings → UTF-8 from JS_ToCStringLen (lone surrogates as 3-byte sequences) → UTF-16.
static jstring utf8_to_jstring(JNIEnv *env, const char *s, size_t len) {
    jchar *buf = malloc((len + 1) * sizeof(jchar));
    size_t n = 0;
    for (size_t i = 0; i < len;) {
        unsigned char c = (unsigned char)s[i];
        uint32_t cp;
        if (c < 0x80) {
            cp = c;
            i += 1;
        } else if ((c & 0xE0) == 0xC0 && i + 1 < len) {
            cp = ((c & 0x1F) << 6) | (s[i + 1] & 0x3F);
            i += 2;
        } else if ((c & 0xF0) == 0xE0 && i + 2 < len) {
            cp = ((c & 0x0F) << 12) | ((s[i + 1] & 0x3F) << 6) | (s[i + 2] & 0x3F);
            i += 3;
        } else if ((c & 0xF8) == 0xF0 && i + 3 < len) {
            cp = ((c & 0x07) << 18) | ((s[i + 1] & 0x3F) << 12) | ((s[i + 2] & 0x3F) << 6) | (s[i + 3] & 0x3F);
            i += 4;
        } else {
            cp = 0xFFFD;
            i += 1;
        }
        if (cp >= 0x10000) {
            cp -= 0x10000;
            buf[n++] = (jchar)(0xD800 | (cp >> 10));
            buf[n++] = (jchar)(0xDC00 | (cp & 0x3FF));
        } else {
            buf[n++] = (jchar)cp;
        }
    }
    jstring result = (*env)->NewString(env, buf, (jsize)n);
    free(buf);
    return result;
}

static jstring js_to_jstring(JNIEnv *env, JSContext *ctx, JSValueConst v) {
    size_t len;
    const char *s = JS_ToCStringLen(ctx, &len, v);
    if (s == NULL) return NULL;
    jstring result = utf8_to_jstring(env, s, len);
    JS_FreeCString(ctx, s);
    return result;
}

// UTF-16 → UTF-8 (pairs as 4 bytes, lone surrogates as 3 bytes, which QuickJS keeps as code units).
// The result is NUL-terminated; the caller frees it.
static char *jstring_to_utf8(JNIEnv *env, jstring str, size_t *plen) {
    jsize n = (*env)->GetStringLength(env, str);
    const jchar *chars = (*env)->GetStringChars(env, str, NULL);
    char *buf = malloc((size_t)n * 3 + 1);
    size_t len = 0;
    for (jsize i = 0; i < n; i++) {
        uint32_t cp = chars[i];
        if (cp >= 0xD800 && cp <= 0xDBFF && i + 1 < n && chars[i + 1] >= 0xDC00 && chars[i + 1] <= 0xDFFF) {
            cp = 0x10000 + ((cp - 0xD800) << 10) + (chars[i + 1] - 0xDC00);
            i++;
        }
        if (cp < 0x80) {
            buf[len++] = (char)cp;
        } else if (cp < 0x800) {
            buf[len++] = (char)(0xC0 | (cp >> 6));
            buf[len++] = (char)(0x80 | (cp & 0x3F));
        } else if (cp < 0x10000) {
            buf[len++] = (char)(0xE0 | (cp >> 12));
            buf[len++] = (char)(0x80 | ((cp >> 6) & 0x3F));
            buf[len++] = (char)(0x80 | (cp & 0x3F));
        } else {
            buf[len++] = (char)(0xF0 | (cp >> 18));
            buf[len++] = (char)(0x80 | ((cp >> 12) & 0x3F));
            buf[len++] = (char)(0x80 | ((cp >> 6) & 0x3F));
            buf[len++] = (char)(0x80 | (cp & 0x3F));
        }
    }
    (*env)->ReleaseStringChars(env, str, chars);
    buf[len] = '\0';
    if (plen) *plen = len;
    return buf;
}

static JSValue jstring_to_js(JNIEnv *env, JSContext *ctx, jstring str) {
    size_t len;
    char *buf = jstring_to_utf8(env, str, &len);
    JSValue v = JS_NewStringLen(ctx, buf, len);
    free(buf);
    return v;
}


// --- errors -------------------------------------------------------------------------------------

static void set_last_error(Engine *e, const char *json) {
    free(e->last_error);
    size_t len = strlen(json);
    e->last_error = malloc(len + 1);
    memcpy(e->last_error, json, len + 1);
}

// Takes the pending exception and stores {name, message, stack, interrupted} as JSON.
static void capture_exception(Engine *e) {
    JSContext *ctx = e->ctx;
    JSValue ex = JS_GetException(ctx);
    JSValue info = JS_NewObject(ctx);
    if (JS_IsError(ctx, ex)) {
        JS_SetPropertyStr(ctx, info, "name", JS_GetPropertyStr(ctx, ex, "name"));
        JS_SetPropertyStr(ctx, info, "message", JS_GetPropertyStr(ctx, ex, "message"));
        JS_SetPropertyStr(ctx, info, "stack", JS_GetPropertyStr(ctx, ex, "stack"));
    } else {
        JS_SetPropertyStr(ctx, info, "name", JS_NewString(ctx, "Thrown"));
        JS_SetPropertyStr(ctx, info, "message", JS_ToString(ctx, ex));
    }
    JS_SetPropertyStr(ctx, info, "interrupted", JS_NewBool(ctx, e->interrupted));
    JSValue json = JS_JSONStringify(ctx, info, JS_UNDEFINED, JS_UNDEFINED);
    const char *s = JS_IsException(json) ? NULL : JS_ToCString(ctx, json);
    if (s != NULL) {
        set_last_error(e, s);
        JS_FreeCString(ctx, s);
    } else {
        JSValue ignored = JS_GetException(ctx);
        JS_FreeValue(ctx, ignored);
        set_last_error(e, e->interrupted
            ? "{\"name\":\"InternalError\",\"message\":\"interrupted\",\"interrupted\":true}"
            : "{\"name\":\"InternalError\",\"message\":\"out of memory\",\"interrupted\":false}");
    }
    JS_FreeValue(ctx, json);
    JS_FreeValue(ctx, info);
    JS_FreeValue(ctx, ex);
}

// --- Java ⇄ JS values ---------------------------------------------------------------------------

static jobject js_to_java(JNIEnv *env, JSContext *ctx, JSValueConst v) {
    if (JS_IsString(v)) return js_to_jstring(env, ctx, v);
    if (JS_IsNumber(v)) {
        double d;
        JS_ToFloat64(ctx, &d, v);
        return (*env)->CallStaticObjectMethod(env, g_double_class, g_double_value_of, d);
    }
    if (JS_IsBool(v)) {
        return (*env)->CallStaticObjectMethod(env, g_boolean_class, g_boolean_value_of, (jboolean)JS_ToBool(ctx, v));
    }
    if (JS_IsObject(v)) {
        size_t size;
        uint8_t *data = JS_GetArrayBuffer(ctx, &size, v);
        if (data != NULL) {
            jbyteArray arr = (*env)->NewByteArray(env, (jsize)size);
            (*env)->SetByteArrayRegion(env, arr, 0, (jsize)size, (const jbyte *)data);
            return arr;
        }
        JSValue ignored = JS_GetException(ctx); // not an ArrayBuffer
        JS_FreeValue(ctx, ignored);
    }
    return NULL;
}

// String, Double, Boolean, byte[] (→ ArrayBuffer), Long (→ the value of that handle), null → undefined.
static JSValue java_to_js(JNIEnv *env, JSContext *ctx, jobject o) {
    if (o == NULL) return JS_UNDEFINED;
    if ((*env)->IsInstanceOf(env, o, g_string_class)) return jstring_to_js(env, ctx, (jstring)o);
    if ((*env)->IsInstanceOf(env, o, g_double_class)) {
        return JS_NewFloat64(ctx, (*env)->CallDoubleMethod(env, o, g_double_value));
    }
    if ((*env)->IsInstanceOf(env, o, g_boolean_class)) {
        return JS_NewBool(ctx, (*env)->CallBooleanMethod(env, o, g_boolean_value));
    }
    if ((*env)->IsInstanceOf(env, o, g_long_class)) {
        jlong h = (*env)->CallLongMethod(env, o, g_long_value);
        return JS_DupValue(ctx, HANDLE(h));
    }
    if ((*env)->IsInstanceOf(env, o, g_bytes_class)) {
        jbyteArray arr = (jbyteArray)o;
        jsize n = (*env)->GetArrayLength(env, arr);
        jbyte *bytes = (*env)->GetByteArrayElements(env, arr, NULL);
        JSValue v = JS_NewArrayBufferCopy(ctx, (const uint8_t *)bytes, (size_t)n);
        (*env)->ReleaseByteArrayElements(env, arr, bytes, JNI_ABORT);
        return v;
    }
    return JS_UNDEFINED;
}

// __native.<name>(...) → Kotlin onNativeCall(index, args). A returned String becomes a JS string,
// anything else undefined; a Kotlin exception becomes a JS InternalError.
static JSValue native_call(JSContext *ctx, JSValueConst this_val, int argc, JSValueConst *argv, int magic) {
    (void)this_val;
    Engine *e = JS_GetContextOpaque(ctx);
    JNIEnv *env = e->env;
    jobjectArray args = (*env)->NewObjectArray(env, argc, g_object_class, NULL);
    for (int i = 0; i < argc; i++) {
        jobject o = js_to_java(env, ctx, argv[i]);
        (*env)->SetObjectArrayElement(env, args, i, o);
        if (o != NULL) (*env)->DeleteLocalRef(env, o);
    }
    jobject result = (*env)->CallObjectMethod(env, e->callbacks, e->on_call, (jint)magic, args);
    (*env)->DeleteLocalRef(env, args);
    if ((*env)->ExceptionCheck(env)) {
        jthrowable t = (*env)->ExceptionOccurred(env);
        (*env)->ExceptionClear(env);
        jstring text = (jstring)(*env)->CallObjectMethod(env, t, g_throwable_to_string);
        const char *msg = text ? (*env)->GetStringUTFChars(env, text, NULL) : NULL;
        JSValue thrown = JS_ThrowInternalError(ctx, "host: %s", msg ? msg : "error");
        if (msg) (*env)->ReleaseStringUTFChars(env, text, msg);
        (*env)->DeleteLocalRef(env, t);
        return thrown;
    }
    JSValue v = JS_UNDEFINED;
    if (result != NULL) {
        if ((*env)->IsInstanceOf(env, result, g_string_class)) v = jstring_to_js(env, ctx, (jstring)result);
        (*env)->DeleteLocalRef(env, result);
    }
    return v;
}

static jclass global_class(JNIEnv *env, const char *name) {
    jclass local = (*env)->FindClass(env, name);
    jclass global = (*env)->NewGlobalRef(env, local);
    (*env)->DeleteLocalRef(env, local);
    return global;
}

JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM *vm, void *reserved) {
    (void)reserved;
    JNIEnv *env;
    if ((*vm)->GetEnv(vm, (void **)&env, JNI_VERSION_1_6) != JNI_OK) return JNI_ERR;
    g_object_class = global_class(env, "java/lang/Object");
    g_string_class = global_class(env, "java/lang/String");
    g_double_class = global_class(env, "java/lang/Double");
    g_boolean_class = global_class(env, "java/lang/Boolean");
    g_long_class = global_class(env, "java/lang/Long");
    g_bytes_class = global_class(env, "[B");
    g_double_value_of = (*env)->GetStaticMethodID(env, g_double_class, "valueOf", "(D)Ljava/lang/Double;");
    g_double_value = (*env)->GetMethodID(env, g_double_class, "doubleValue", "()D");
    g_boolean_value_of = (*env)->GetStaticMethodID(env, g_boolean_class, "valueOf", "(Z)Ljava/lang/Boolean;");
    g_boolean_value = (*env)->GetMethodID(env, g_boolean_class, "booleanValue", "()Z");
    g_long_value = (*env)->GetMethodID(env, g_long_class, "longValue", "()J");
    jclass throwable = (*env)->FindClass(env, "java/lang/Throwable");
    g_throwable_to_string = (*env)->GetMethodID(env, throwable, "toString", "()Ljava/lang/String;");
    (*env)->DeleteLocalRef(env, throwable);
    return JNI_VERSION_1_6;
}

// --- exported API -------------------------------------------------------------------------------

#define FN(name) Java_hentaireader_host_engine_QuickJsNative_##name

JNIEXPORT jlong JNICALL FN(create)(JNIEnv *env, jclass cls, jlong memoryLimit, jobject callbacks, jobjectArray nativeNames) {
    (void)cls;
    Engine *e = calloc(1, sizeof(Engine));
    e->env = env;
    e->rt = JS_NewRuntime();
    if (memoryLimit > 0) JS_SetMemoryLimit(e->rt, (size_t)memoryLimit);
    JS_SetInterruptHandler(e->rt, interrupt_handler, e);
    e->ctx = JS_NewContext(e->rt);
    JS_SetContextOpaque(e->ctx, e);
    e->callbacks = (*env)->NewGlobalRef(env, callbacks);
    jclass cb = (*env)->GetObjectClass(env, callbacks);
    e->on_call = (*env)->GetMethodID(env, cb, "onNativeCall", "(I[Ljava/lang/Object;)Ljava/lang/Object;");
    (*env)->DeleteLocalRef(env, cb);

    JSValue native = JS_NewObject(e->ctx);
    jsize count = (*env)->GetArrayLength(env, nativeNames);
    for (jsize i = 0; i < count; i++) {
        jstring name = (jstring)(*env)->GetObjectArrayElement(env, nativeNames, i);
        char *cname = jstring_to_utf8(env, name, NULL);
        JS_SetPropertyStr(e->ctx, native, cname, JS_NewCFunctionMagic(e->ctx, native_call, cname, 0, JS_CFUNC_generic_magic, (int)i));
        free(cname);
        (*env)->DeleteLocalRef(env, name);
    }
    JSValue global = JS_GetGlobalObject(e->ctx);
    JS_SetPropertyStr(e->ctx, global, "__native", native);
    JS_FreeValue(e->ctx, global);
    return (jlong)(intptr_t)e;
}

JNIEXPORT void JNICALL FN(destroy)(JNIEnv *env, jclass cls, jlong engine) {
    (void)cls;
    Engine *e = ENGINE(engine);
    e->env = env;
    JS_FreeContext(e->ctx);
    JS_FreeRuntime(e->rt);
    (*env)->DeleteGlobalRef(env, e->callbacks);
    free(e->last_error);
    free(e);
}

JNIEXPORT void JNICALL FN(setTimeout)(JNIEnv *env, jclass cls, jlong engine, jlong timeoutMs) {
    (void)env;
    (void)cls;
    Engine *e = ENGINE(engine);
    e->deadline_ms = timeoutMs > 0 ? now_ms() + timeoutMs : 0;
    e->interrupted = 0;
}

JNIEXPORT void JNICALL FN(cancel)(JNIEnv *env, jclass cls, jlong engine) {
    (void)env;
    (void)cls;
    ENGINE(engine)->cancel = 1;
}

JNIEXPORT jboolean JNICALL FN(wasInterrupted)(JNIEnv *env, jclass cls, jlong engine) {
    (void)env;
    (void)cls;
    return (jboolean)ENGINE(engine)->interrupted;
}

JNIEXPORT jlong JNICALL FN(evalGlobal)(JNIEnv *env, jclass cls, jlong engine, jstring code, jstring file) {
    (void)cls;
    Engine *e = ENGINE(engine);
    e->env = env;
    size_t len;
    char *ccode = jstring_to_utf8(env, code, &len);
    char *cfile = jstring_to_utf8(env, file, NULL);
    JSValue v = JS_Eval(e->ctx, ccode, len, cfile, JS_EVAL_TYPE_GLOBAL);
    free(ccode);
    free(cfile);
    if (JS_IsException(v)) {
        capture_exception(e);
        return 0;
    }
    return wrap(v);
}

// Evaluates an ES module and returns its namespace object (the source's exports).
JNIEXPORT jlong JNICALL FN(evalModule)(JNIEnv *env, jclass cls, jlong engine, jstring code, jstring file) {
    (void)cls;
    Engine *e = ENGINE(engine);
    e->env = env;
    size_t len;
    char *ccode = jstring_to_utf8(env, code, &len);
    char *cfile = jstring_to_utf8(env, file, NULL);
    JSValue module = JS_Eval(e->ctx, ccode, len, cfile, JS_EVAL_TYPE_MODULE | JS_EVAL_FLAG_COMPILE_ONLY);
    free(ccode);
    free(cfile);
    if (JS_IsException(module)) {
        capture_exception(e);
        return 0;
    }
    JSModuleDef *def = JS_VALUE_GET_PTR(module);
    JSValue evaluated = JS_EvalFunction(e->ctx, module);
    if (JS_IsException(evaluated)) {
        capture_exception(e);
        return 0;
    }
    // Module evaluation yields a promise (top-level await); settle it before reading the exports.
    JSContext *job_ctx;
    while (JS_ExecutePendingJob(e->rt, &job_ctx) > 0) {
    }
    if (JS_PromiseState(e->ctx, evaluated) == JS_PROMISE_REJECTED) {
        JS_Throw(e->ctx, JS_PromiseResult(e->ctx, evaluated));
        JS_FreeValue(e->ctx, evaluated);
        capture_exception(e);
        return 0;
    }
    JS_FreeValue(e->ctx, evaluated);
    return wrap(JS_GetModuleNamespace(e->ctx, def));
}

JNIEXPORT jlong JNICALL FN(call)(JNIEnv *env, jclass cls, jlong engine, jlong fn, jobjectArray args) {
    (void)cls;
    Engine *e = ENGINE(engine);
    e->env = env;
    jsize argc = args ? (*env)->GetArrayLength(env, args) : 0;
    JSValue *argv = argc > 0 ? malloc(sizeof(JSValue) * (size_t)argc) : NULL;
    for (jsize i = 0; i < argc; i++) {
        jobject o = (*env)->GetObjectArrayElement(env, args, i);
        argv[i] = java_to_js(env, e->ctx, o);
        if (o != NULL) (*env)->DeleteLocalRef(env, o);
    }
    JSValue result = JS_Call(e->ctx, HANDLE(fn), JS_UNDEFINED, argc, argv);
    for (jsize i = 0; i < argc; i++) JS_FreeValue(e->ctx, argv[i]);
    free(argv);
    if (JS_IsException(result)) {
        capture_exception(e);
        return 0;
    }
    return wrap(result);
}

JNIEXPORT jlong JNICALL FN(getProperty)(JNIEnv *env, jclass cls, jlong engine, jlong obj, jstring name) {
    (void)cls;
    Engine *e = ENGINE(engine);
    e->env = env;
    char *cname = jstring_to_utf8(env, name, NULL);
    JSValue v = JS_GetPropertyStr(e->ctx, HANDLE(obj), cname);
    free(cname);
    if (JS_IsException(v)) {
        capture_exception(e);
        return 0;
    }
    return wrap(v);
}

JNIEXPORT jstring JNICALL FN(toJavaString)(JNIEnv *env, jclass cls, jlong engine, jlong h) {
    (void)cls;
    Engine *e = ENGINE(engine);
    e->env = env;
    jstring s = js_to_jstring(env, e->ctx, HANDLE(h));
    if (s == NULL) capture_exception(e);
    return s;
}

JNIEXPORT void JNICALL FN(free)(JNIEnv *env, jclass cls, jlong engine, jlong h) {
    (void)cls;
    Engine *e = ENGINE(engine);
    e->env = env;
    if (h == 0) return;
    JS_FreeValue(e->ctx, HANDLE(h));
    free((JSValue *)(intptr_t)h);
}

// Runs every pending job. Returns false when a job threw (interrupt, out of memory); the error is
// then in lastError.
JNIEXPORT jboolean JNICALL FN(executePendingJobs)(JNIEnv *env, jclass cls, jlong engine) {
    (void)cls;
    Engine *e = ENGINE(engine);
    e->env = env;
    JSContext *job_ctx;
    int r;
    while ((r = JS_ExecutePendingJob(e->rt, &job_ctx)) > 0) {
    }
    if (r < 0) {
        capture_exception(e);
        return JNI_FALSE;
    }
    return JNI_TRUE;
}

JNIEXPORT jstring JNICALL FN(lastError)(JNIEnv *env, jclass cls, jlong engine) {
    (void)cls;
    Engine *e = ENGINE(engine);
    if (e->last_error == NULL) return NULL;
    return utf8_to_jstring(env, e->last_error, strlen(e->last_error));
}
