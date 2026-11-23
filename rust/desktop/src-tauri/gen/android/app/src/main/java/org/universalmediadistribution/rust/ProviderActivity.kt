package org.universalmediadistribution.rust

import android.app.Activity
import android.net.Uri
import android.os.Bundle
import android.webkit.*
import android.widget.LinearLayout
import android.widget.Button
import android.view.ViewGroup.LayoutParams

class ProviderActivity : Activity() {
    private lateinit var player: WebView

    companion object {
        private var current = java.lang.ref.WeakReference<ProviderActivity>(null)
        private var pending: String? = null
        fun opening(session: String) { pending = session }
        fun media(session: String, script: String, close: Boolean, key: Long) {
            val owner = current.get()
            if (pending == session && !close) { PlayerFilter.mediaResult(key, "null"); return }
            if (owner == null || owner.isFinishing || owner.intent.getStringExtra("session") != session) {
                PlayerFilter.mediaResult(key, "false")
            } else if (close) {
                owner.finish()
                PlayerFilter.mediaResult(key, "false")
            } else owner.player.evaluateJavascript(script) { PlayerFilter.mediaResult(key, it ?: "null") }
        }
    }
    override fun onCreate(state: Bundle?) {
        super.onCreate(state)
        val source = intent.data
        if (source?.scheme != "https" || source.host == null ||
            PlayerFilter.denied(source.toString(),source.toString(),"document","GET",true)) { finish(); return }
        val root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        root.addView(Button(this).apply { text = if (intent.getBooleanExtra("english", false)) "Back to UMD" else "Volver a UMD"; setOnClickListener {
          if(intent.getBooleanExtra("interactive",false)) {
            startActivity(android.content.Intent(this@ProviderActivity,MainActivity::class.java).addFlags(android.content.Intent.FLAG_ACTIVITY_REORDER_TO_FRONT))
          } else finish()
        } })
        player = WebView(this)
        root.addView(player, LinearLayout.LayoutParams(LayoutParams.MATCH_PARENT, 0, 1f))
        setContentView(root)
        player.settings.apply {
            javaScriptEnabled = true
            domStorageEnabled = true
            allowFileAccess = false
            allowContentAccess = false
            mixedContentMode = WebSettings.MIXED_CONTENT_NEVER_ALLOW
            javaScriptCanOpenWindowsAutomatically = false
            setSupportMultipleWindows(true)
        }
        player.webChromeClient = WebChromeClient()
        player.setDownloadListener { _, _, _, _, _ -> }
        val cosmetic = if (intent.getBooleanExtra("hideAds", true)) PlayerFilter.injection(source.toString()) else null
        val early = androidx.webkit.WebViewFeature.isFeatureSupported(androidx.webkit.WebViewFeature.DOCUMENT_START_SCRIPT)
        if (cosmetic != null && early) androidx.webkit.WebViewCompat.addDocumentStartJavaScript(player, cosmetic, setOf("*"))
        player.webViewClient = object : WebViewClient() {
            override fun onPageFinished(view: WebView, url: String) {
                if (!early && cosmetic != null) view.evaluateJavascript(cosmetic, null)
            }
            override fun shouldInterceptRequest(view: WebView, request: WebResourceRequest) =
                PlayerFilter.response(request, source.toString(), true)
            override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
                val url = request.url
                if (url.scheme != "https") return true
                fun port(u: Uri) = if (u.port == -1) 443 else u.port
                return request.isForMainFrame && (!url.host.equals(source.host, true) || port(url) != port(source))
            }
        }
        pending = null
        current = java.lang.ref.WeakReference(this)
        player.loadUrl(source.toString())
    }
    override fun onDestroy() {
        if (::player.isInitialized) {
            (player.parent as? android.view.ViewGroup)?.removeView(player)
            player.destroy()
        }
        super.onDestroy()
    }
}
