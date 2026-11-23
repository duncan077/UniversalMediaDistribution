package org.universalmediadistribution.rust

import android.graphics.Bitmap
import android.webkit.*
import androidx.annotation.Keep
import java.io.ByteArrayInputStream

@Keep
internal object PlayerFilter {
    @JvmStatic external fun mediaResult(key: Long, value: String)
    @JvmStatic external fun conditions(low: Boolean, defer: Boolean)
    @JvmStatic external fun injection(address: String): String?
    @JvmStatic external fun denied(url: String, page: String, kind: String, method: String, isolated: Boolean): Boolean

    fun response(request: WebResourceRequest, top: String, isolated: Boolean): WebResourceResponse? {
        val headers = request.requestHeaders
        val destination = headers.entries.firstOrNull { it.key.equals("Sec-Fetch-Dest", true) }?.value
        val kind = when {
            request.isForMainFrame -> "document"
            destination == "audio" || destination == "video" || destination == "track" -> "media"
            destination == "iframe" || destination == "frame" -> "subdocument"
            destination == "empty" -> "xmlhttprequest"
            destination == "style" -> "stylesheet"
            destination != null -> destination
            else -> "other"
        }
        val page = headers.entries.firstOrNull { it.key.equals("Referer", true) }?.value ?: top
        if (!denied(request.url.toString(), page, kind, request.method, isolated)) return null
        return WebResourceResponse("text/plain", "utf-8", 403, "Blocked", mapOf("Cache-Control" to "no-store"), ByteArrayInputStream(ByteArray(0)))
    }
}

// Keep Wry in charge of its own IPC, asset protocol, range requests and navigation callbacks.
internal class LibraryFilter(private val inner: WebViewClient) : WebViewClient() {
    override fun shouldInterceptRequest(view: WebView, request: WebResourceRequest) =
        PlayerFilter.response(request, "https://tauri.localhost", false) ?: inner.shouldInterceptRequest(view, request)
    override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest) = inner.shouldOverrideUrlLoading(view, request)
    override fun onPageStarted(view: WebView, url: String, favicon: Bitmap?) = inner.onPageStarted(view, url, favicon)
    override fun onPageFinished(view: WebView, url: String) = inner.onPageFinished(view, url)
    override fun doUpdateVisitedHistory(view: WebView, url: String, isReload: Boolean) = inner.doUpdateVisitedHistory(view, url, isReload)
    override fun onReceivedError(view: WebView, request: WebResourceRequest, error: WebResourceError) = inner.onReceivedError(view, request, error)
}
