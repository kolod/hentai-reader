package hentaireader.app.ui.login

import android.annotation.SuppressLint
import android.content.Intent
import android.webkit.CookieManager
import android.webkit.WebResourceRequest
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.net.toUri
import hentaireader.app.AppServices
import hentaireader.app.R
import hentaireader.app.ui.common.HrTopBar
import hentaireader.host.http.CookieJar
import hentaireader.host.reading.LoginCookies
import hentaireader.host.sources.ManifestRules
import hentaireader.host.sources.SourceSession
import kotlinx.coroutines.launch
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonPrimitive

/**
 * Sign-in on the site's own page (005 FR-028, FR-029; 007 FR-033, research R13): the source's
 * `manifest.loginPage` in a WebView that may only visit the source's domains (other links open in
 * the system browser). When every proving cookie is set, the cookies within the source's domains are
 * stored in its jar (encrypted), the WebView's cookies are cleared and the page closes.
 */
@SuppressLint("SetJavaScriptEnabled")
@Composable
fun LoginScreen(services: AppServices, sourceId: String, onDone: () -> Unit) {
    val scope = rememberCoroutineScope()
    val session by produceState<SourceSession?>(null, sourceId) { value = runCatching { services.session(sourceId) }.getOrNull() }
    val s = session
    val page = s?.manifest?.get("loginPage") as? JsonObject
    val name = (s?.manifest?.get("name") as? JsonPrimitive)?.contentOrNull ?: sourceId
    Scaffold(topBar = { HrTopBar(stringResource(R.string.Login_Title, name), onBack = onDone) }, contentWindowInsets = WindowInsets(0)) { padding ->
        if (s == null || page == null) return@Scaffold
        val domains = (s.manifest["domains"] as? JsonArray).orEmpty().map { it.jsonPrimitive.content }
        val url = page["url"]!!.jsonPrimitive.content
        Column(Modifier.padding(padding).fillMaxSize()) {
            Text(stringResource(R.string.Login_Hint), style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(12.dp))
            // A device without a working WebView can still paste cookies in Settings.
            val webView = remember { runCatching { CookieManager.getInstance() }.isSuccess }
            if (!webView) {
                Text(stringResource(R.string.Login_NoWebView_Android), Modifier.padding(16.dp))
            } else {
                AndroidView(
                    // The WebView would otherwise draw over the hint above it.
                    modifier = Modifier.weight(1f).fillMaxWidth().clipToBounds(),
                    factory = { context ->
                        WebView(context).apply {
                            settings.javaScriptEnabled = true
                            settings.domStorageEnabled = true
                            CookieManager.getInstance().setAcceptCookie(true)
                            webViewClient = object : WebViewClient() {
                                override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
                                    val host = request.url.host ?: return true
                                    if (ManifestRules.hostInDomains(host, domains)) return false
                                    context.startActivity(Intent(Intent.ACTION_VIEW, request.url))
                                    return true
                                }

                                override fun onPageFinished(view: WebView, finishedUrl: String) {
                                    val captured = capture(domains, url)
                                    if (LoginCookies.missing(page, captured).isNotEmpty()) return
                                    s.http.cookies.add(LoginCookies.select(s.manifest, captured))
                                    CookieManager.getInstance().removeAllCookies(null)
                                    view.clearCache(true)
                                    scope.launch { onDone() }
                                }
                            }
                            loadUrl(url)
                        }
                    },
                )
            }
        }
    }
}

/** Cookies the WebView holds for the source's concrete domains and the login page (names and values only). */
private fun capture(domains: List<String>, loginUrl: String): List<CookieJar.Cookie> {
    val manager = CookieManager.getInstance()
    val hosts = (domains.map { it.removePrefix("*.") } + listOfNotNull(loginUrl.toUri().host)).distinct()
    return hosts.flatMap { host ->
        manager.getCookie("https://$host/").orEmpty().split(';').mapNotNull { part ->
            val eq = part.indexOf('=')
            if (eq <= 0) null else CookieJar.Cookie(part.substring(0, eq).trim(), part.substring(eq + 1).trim(), host, false, "/", null)
        }
    }
}
