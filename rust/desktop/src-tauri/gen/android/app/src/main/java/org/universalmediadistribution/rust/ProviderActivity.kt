package org.universalmediadistribution.rust

import android.app.Activity
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
        player = PlayerFilter.providerView(this, source, intent.getBooleanExtra("hideAds", true), false)
        root.addView(player, LinearLayout.LayoutParams(LayoutParams.MATCH_PARENT, 0, 1f))
        setContentView(root)
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
