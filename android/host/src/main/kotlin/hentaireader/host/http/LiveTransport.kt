package hentaireader.host.http

import java.io.IOException
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resumeWithException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import okhttp3.Call
import okhttp3.Callback
import okhttp3.MediaType.Companion.toMediaTypeOrNull
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response

/**
 * Real network on OkHttp. Cookies and redirects are handled by [HttpHost], never by OkHttp
 * (no cookie jar, no cache, redirects off), so the domain allow-list applies to every hop.
 * Cancelling the coroutine cancels the call.
 */
class LiveTransport(userAgent: String = DEFAULT_USER_AGENT) : Transport {
    private val client = OkHttpClient.Builder()
        .followRedirects(false)
        .followSslRedirects(false)
        .retryOnConnectionFailure(true)
        .callTimeout(0, TimeUnit.MILLISECONDS)
        .addNetworkInterceptor { chain ->
            val request = chain.request()
            if (request.header("user-agent") == null) chain.proceed(request.newBuilder().header("user-agent", userAgent).build())
            else chain.proceed(request)
        }
        .build()

    override suspend fun send(request: RawRequest): RawResponse {
        val builder = Request.Builder().url(request.url)
        var contentType: String? = null
        for ((name, value) in request.headers) {
            if (name == "content-type") contentType = value
            builder.header(name, value)
        }
        // Byte ranges must address the identity representation: asked with Accept-Encoding, nginx
        // (Hitomi) answers a range of the gzip file. OkHttp adds gzip unless told otherwise.
        if (request.headers.containsKey("range") && !request.headers.containsKey("accept-encoding")) builder.header("accept-encoding", "identity")
        val body = request.body?.toRequestBody(contentType?.toMediaTypeOrNull())
        builder.method(request.method, body ?: if (request.method == "POST") ByteArray(0).toRequestBody() else null)

        val call = client.newBuilder().callTimeout(request.timeoutMs, TimeUnit.MILLISECONDS).build().newCall(builder.build())
        val response = call.await()
        // The body is read here, not in the callback: off the caller's thread (never the UI thread).
        return withContext(Dispatchers.IO) {
            response.use { r ->
                val headers = r.headers.map { (k, v) -> k.lowercase() to v }
                RawResponse(r.code, headers, r.body.bytes())
            }
        }
    }

    private suspend fun Call.await(): Response = suspendCancellableCoroutine { cont ->
        enqueue(object : Callback {
            override fun onResponse(call: Call, response: Response) {
                cont.resume(response) { _, value, _ -> value.close() }
            }

            override fun onFailure(call: Call, e: IOException) {
                cont.resumeWithException(e)
            }
        })
        cont.invokeOnCancellation { cancel() }
    }

    companion object {
        const val DEFAULT_USER_AGENT = "Mozilla/5.0 (Linux; Android 14) HentaiReader"
    }
}
