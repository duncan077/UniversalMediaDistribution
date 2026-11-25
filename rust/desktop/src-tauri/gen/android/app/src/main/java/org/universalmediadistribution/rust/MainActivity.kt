package org.universalmediadistribution.rust

import android.os.Bundle
import android.content.res.Configuration
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.provider.Settings
import android.view.KeyEvent
import android.view.ViewGroup
import android.webkit.WebView
import android.widget.FrameLayout
import androidx.activity.OnBackPressedCallback
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.content.FileProvider
import androidx.core.content.pm.PackageInfoCompat
import java.io.File
import org.json.JSONObject
import kotlin.math.roundToInt

class MainActivity : TauriActivity() {
  private val television: Boolean
    get() = (resources.configuration.uiMode and Configuration.UI_MODE_TYPE_MASK) == Configuration.UI_MODE_TYPE_TELEVISION
  private var provider: Pair<String, WebView>? = null
  private val providerBack = object : OnBackPressedCallback(false) {
    override fun handleOnBackPressed() = focusLibrary()
  }

  @androidx.annotation.Keep
  fun openFilteredPlayer(url: String, hideAds: Boolean, session: String, english: Boolean, interactive: Boolean, bounds: String) {
    updateFilterConditions()
    if (!interactive) {
      val source = Uri.parse(url)
      require(source.scheme == "https" && source.host != null && !PlayerFilter.denied(url, url, "document", "GET", true))
      closeProvider()
      val player = PlayerFilter.providerView(this, source, hideAds, true)
      player.isFocusableInTouchMode = !television
      if (television) player.isFocusable = false
      player.setOnFocusChangeListener { _, focused ->
        providerBack.isEnabled = focused
        if (focused) libraryWeb.evaluateJavascript("document.dispatchEvent(new Event('umd-provider-enter'))", null)
      }
      provider = session to player
      findViewById<FrameLayout>(android.R.id.content).addView(player, FrameLayout.LayoutParams(1, 1))
      layoutProvider(session, bounds, -1)
      player.loadUrl(url)
      if (television) libraryWeb.post {
        if (provider?.second === player && !player.hasFocus()) libraryWeb.requestFocus()
      }
      return
    }
    ProviderActivity.opening(session)
    startActivity(Intent(this, ProviderActivity::class.java).setData(android.net.Uri.parse(url)).putExtra("hideAds", hideAds).putExtra("session",session).putExtra("english",english).putExtra("interactive",interactive))
  }

  @androidx.annotation.Keep
  fun providerMedia(session: String, script: String, close: Boolean, key: Long) {
    val current = provider
    if (current?.first == session) {
      if (close) {
        closeProvider()
        PlayerFilter.mediaResult(key, "false")
      } else current.second.evaluateJavascript(script) { PlayerFilter.mediaResult(key, it ?: "null") }
      return
    }
    ProviderActivity.media(session, script, close, key)
  }

  @androidx.annotation.Keep
  fun layoutProvider(session: String, bounds: String, focus: Int) {
    val current = provider ?: return
    if (current.first != session) return
    val box = JSONObject(bounds)
    val scale = libraryWeb.width / box.getDouble("pageWidth")
    val root = findViewById<FrameLayout>(android.R.id.content)
    val libraryOrigin = IntArray(2).also { libraryWeb.getLocationInWindow(it) }
    val rootOrigin = IntArray(2).also { root.getLocationInWindow(it) }
    current.second.layoutParams = FrameLayout.LayoutParams(
      (box.getDouble("width") * scale).roundToInt().coerceAtLeast(1),
      (box.getDouble("height") * scale).roundToInt().coerceAtLeast(1)
    ).apply {
      leftMargin = libraryOrigin[0] - rootOrigin[0] + (box.getDouble("x") * scale).roundToInt()
      topMargin = libraryOrigin[1] - rootOrigin[1] + (box.getDouble("y") * scale).roundToInt()
    }
    if (focus == 1) {
      current.second.isFocusableInTouchMode = true
      current.second.requestFocus()
    }
    else if (focus == 0) focusLibrary()
  }

  private fun closeProvider() {
    val player = provider?.second ?: return
    provider = null
    providerBack.isEnabled = false
    (player.parent as? ViewGroup)?.removeView(player)
    player.destroy()
    libraryWeb.requestFocus()
  }

  private fun focusLibrary() {
    providerBack.isEnabled = false
    if (television) provider?.second?.let { player ->
      player.isFocusableInTouchMode = false
      player.isFocusable = false
    }
    libraryWeb.requestFocus()
    libraryWeb.evaluateJavascript("document.dispatchEvent(new Event('umd-provider-focus'))", null)
  }

  override fun dispatchKeyEvent(event: KeyEvent): Boolean {
    if (event.keyCode == KeyEvent.KEYCODE_BACK && providerBack.isEnabled) {
      if (event.action == KeyEvent.ACTION_UP) focusLibrary()
      return true
    }
    return super.dispatchKeyEvent(event)
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
  private var pendingUpdate: File? = null
  private val updatePermission = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) {
    pendingUpdate?.let { apk ->
      if (!canInstallUpdates() || !openUpdateInstaller(apk)) finishUpdate(cancelled = true)
    }
  }
  private val updateInstaller = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
    finishUpdate(cancelled = result.resultCode != RESULT_OK)
  }

  private fun updateDirectory() = File(cacheDir, "verified-updates")

  private fun canInstallUpdates() = Build.VERSION.SDK_INT < 26 || packageManager.canRequestPackageInstalls()

  private fun finishUpdate(cancelled: Boolean) {
    pendingUpdate?.delete()
    pendingUpdate = null
    if (cancelled && ::libraryWeb.isInitialized) {
      libraryWeb.evaluateJavascript("window.dispatchEvent(new Event('umd-update-install-cancelled'))", null)
    }
  }

  @Suppress("DEPRECATION")
  private fun openUpdateInstaller(apk: File): Boolean {
    val uri = FileProvider.getUriForFile(this, "$packageName.fileprovider", apk)
    val requests = listOf(
      Intent(Intent.ACTION_INSTALL_PACKAGE).setData(uri),
      Intent(Intent.ACTION_VIEW).setDataAndType(uri, "application/vnd.android.package-archive")
    )
    return requests.any { request ->
      runCatching {
        updateInstaller.launch(request.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
          .putExtra(Intent.EXTRA_RETURN_RESULT, true))
      }.isSuccess
    }
  }

  @Suppress("DEPRECATION")
  @androidx.annotation.Keep
  fun installVerifiedApk(path: String, version: String): Boolean {
    return try {
      if (pendingUpdate != null) return false
      val dir = updateDirectory().canonicalFile
      val apk = File(path).canonicalFile
      if (apk.parentFile != dir || apk.extension != "apk" || !apk.isFile) return false
      val archive = packageManager.getPackageArchiveInfo(apk.path, PackageManager.GET_SIGNATURES) ?: return false
      val installed = packageManager.getPackageInfo(packageName, PackageManager.GET_SIGNATURES)
      if (archive.packageName != packageName || archive.versionName != version ||
          PackageInfoCompat.getLongVersionCode(archive) <= PackageInfoCompat.getLongVersionCode(installed) ||
          archive.signatures.isNullOrEmpty() || archive.signatures?.toSet() != installed.signatures?.toSet()) return false
      dir.listFiles()?.filter { it != apk && System.currentTimeMillis() - it.lastModified() > 86400000L }
        ?.forEach { it.delete() }
      pendingUpdate = apk
      val opened = if (canInstallUpdates()) openUpdateInstaller(apk) else {
        listOf(
          Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:$packageName")),
          Intent(Settings.ACTION_SECURITY_SETTINGS)
        ).any { request -> runCatching { updatePermission.launch(request) }.isSuccess }
      }
      if (!opened) pendingUpdate = null
      opened
    } catch (_: Exception) {
      pendingUpdate = null
      false
    }
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
    closeProvider()
    playback.destroy()
    super.onDestroy()
  }

  override fun onTrimMemory(level: Int) {
    super.onTrimMemory(level)
    if (level >= android.content.ComponentCallbacks2.TRIM_MEMORY_RUNNING_LOW && filterReady) {
      PlayerFilter.trimMemory()
    }
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
    if (television) {
      ReaderTiles.register()
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
    pendingUpdate = savedInstanceState?.getString("umd.verified.update")
      ?.takeIf { it == File(it).name && it.endsWith(".apk") }
      ?.let { File(updateDirectory(), it).takeIf(File::isFile) }
    onBackPressedDispatcher.addCallback(this, providerBack)
  }

  override fun onSaveInstanceState(outState: Bundle) {
    super.onSaveInstanceState(outState)
    outState.putString("umd.verified.update", pendingUpdate?.name)
  }
}
