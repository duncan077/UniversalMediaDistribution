package org.universalmediadistribution.rust

import android.os.Bundle
import android.content.res.Configuration
import android.content.Intent
import android.content.pm.PackageManager
import android.webkit.WebView
import androidx.activity.enableEdgeToEdge

class MainActivity : TauriActivity() {
  @androidx.annotation.Keep
  fun openFilteredPlayer(url: String, hideAds: Boolean, session: String, english: Boolean, interactive: Boolean) {
    updateFilterConditions()
    ProviderActivity.opening(session)
    startActivity(Intent(this, ProviderActivity::class.java).setData(android.net.Uri.parse(url)).putExtra("hideAds", hideAds).putExtra("session",session).putExtra("english",english).putExtra("interactive",interactive))
  }

  @androidx.annotation.Keep
  fun providerMedia(session: String, script: String, close: Boolean, key: Long) {
    ProviderActivity.media(session, script, close, key)
  }

  private fun updateFilterConditions() {
    val service = getSystemService(ACTIVITY_SERVICE) as android.app.ActivityManager
    val memory = android.app.ActivityManager.MemoryInfo().also { service.getMemoryInfo(it) }
    val network = getSystemService(CONNECTIVITY_SERVICE) as android.net.ConnectivityManager
    val power = getSystemService(POWER_SERVICE) as android.os.PowerManager
    val battery = getSystemService(BATTERY_SERVICE) as android.os.BatteryManager
    val charge = battery.getIntProperty(android.os.BatteryManager.BATTERY_PROPERTY_CAPACITY)
    PlayerFilter.conditions(service.isLowRamDevice || memory.totalMem <= 3L * 1024 * 1024 * 1024,
      network.isActiveNetworkMetered || power.isPowerSaveMode || (!battery.isCharging && charge in 0..20))
  }
  @Suppress("DEPRECATION")
  @androidx.annotation.Keep
  fun installVerifiedApk(path: String, version: String): Boolean {
    return try {
      val source = java.io.File(path)
      val archive = packageManager.getPackageArchiveInfo(path, android.content.pm.PackageManager.GET_SIGNATURES) ?: return false
      val installed = packageManager.getPackageInfo(packageName, android.content.pm.PackageManager.GET_SIGNATURES)
      if (archive.packageName != packageName || archive.versionName != version ||
          archive.signatures.isNullOrEmpty() || archive.signatures?.toSet() != installed.signatures?.toSet()) return false
      val dir = java.io.File(cacheDir, "verified-updates").apply { mkdirs() }
      dir.listFiles()?.filter { System.currentTimeMillis() - it.lastModified() > 86400000L }?.forEach { it.delete() }
      val apk = source.canonicalFile
      if (apk.parentFile != dir.canonicalFile) return false
      val uri = androidx.core.content.FileProvider.getUriForFile(this, "$packageName.fileprovider", apk)
      startActivity(Intent(Intent.ACTION_VIEW).setDataAndType(uri, "application/vnd.android.package-archive")
        .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION))
      true
    } catch (_: Exception) { false }
  }

  override fun onResume() {
    super.onResume()
    if (filterReady) updateFilterConditions()
  }
  private var filterReady = false

  private lateinit var libraryWeb: WebView
  private val playback = NativePlayback(this)
  @androidx.annotation.Keep
  fun nativePlayback(operation: String, arguments: String): String =
    playback.command(operation, org.json.JSONObject(arguments), libraryWeb).toString()
  override fun onDestroy() {
    playback.close()
    super.onDestroy()
  }

  override fun onWebViewCreate(webView: WebView) {
    libraryWeb = webView
    super.onWebViewCreate(webView)
    filterReady = true
    updateFilterConditions()
    // Wry installs its client after this callback returns.
    webView.post {
      if (androidx.webkit.WebViewFeature.isFeatureSupported(androidx.webkit.WebViewFeature.GET_WEB_VIEW_CLIENT)) {
        androidx.webkit.WebViewCompat.getWebViewClient(webView)?.let { webView.webViewClient = LibraryFilter(it) }
      }
    }
    if ((resources.configuration.uiMode and Configuration.UI_MODE_TYPE_MASK) == Configuration.UI_MODE_TYPE_TELEVISION) {
      webView.settings.userAgentString += " UMD-TV"
      val pick = Intent(Intent.ACTION_GET_CONTENT).apply {
        addCategory(Intent.CATEGORY_OPENABLE)
        type = "application/octet-stream"
      }
      val hasPicker = packageManager.queryIntentActivities(pick, PackageManager.MATCH_DEFAULT_ONLY)
        .any { it.activityInfo.packageName != "com.android.tv.frameworkpackagestubs" }
      if (!hasPicker) webView.settings.userAgentString += " UMD-NoDocumentPicker"
    }
  }
  override fun onCreate(savedInstanceState: Bundle?) {
    enableEdgeToEdge()
    super.onCreate(savedInstanceState)
  }
}
