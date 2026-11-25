package org.universalmediadistribution.rust

import android.content.Context
import android.graphics.SurfaceTexture
import android.media.MediaPlayer
import android.net.Uri
import android.net.wifi.WifiManager
import android.os.Handler
import android.os.Looper
import android.view.Surface
import android.view.TextureView
import android.view.View
import android.view.ViewGroup
import android.webkit.WebView
import android.view.Gravity
import android.widget.TextView
import android.widget.FrameLayout
import org.json.JSONArray
import org.json.JSONObject
import org.videolan.libvlc.LibVLC
import org.videolan.libvlc.Media
import org.videolan.libvlc.RendererItem
import org.videolan.libvlc.RendererDiscoverer
import org.videolan.libvlc.util.VLCVideoLayout
import org.videolan.libvlc.MediaPlayer as VlcPlayer
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit
import kotlin.math.roundToInt

class NativePlayback(private val owner: MainActivity) {
  private var session = ""
  private var system: MediaPlayer? = null
  private var vlc: VlcPlayer? = null
  private var library: LibVLC? = null
  private var surface: Surface? = null
  private var view: View? = null
  private var ready = false
  private var ended = false
  private var error = ""
  private val ui = Handler(Looper.getMainLooper())
  private val retirement = ThreadPoolExecutor(0, 1, 15, TimeUnit.SECONDS, LinkedBlockingQueue())
  private var releasing = false
  private var pendingStart: (() -> Unit)? = null
  private var tracksDirty = true
  private var devicesDirty = true
  private var changingRenderer = false
  private var rendererFailed = false

  private var discovery: RendererDiscoverer? = null
  private var multicast: WifiManager.MulticastLock? = null
  private val devices = mutableMapOf<Int,RendererItem>()
  private var nextDevice = 1
  private var selectedDevice = 0

  private fun findScreens(): Boolean {
    if (discovery != null) return true
    val core = library ?: return false
    val service = RendererDiscoverer.list(core)?.firstOrNull() ?: return false
    val search = RendererDiscoverer(core, service.name)
    // Wi-Fi may filter Chromecast discovery packets while there is no multicast lock.
    multicast = runCatching {
      val wifi = owner.applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
      wifi?.createMulticastLock("UMD Chromecast")?.apply { acquire() }
    }.getOrNull()
    discovery = search
    search.setEventListener { event ->
      if (discovery === search) {
        val item = event.item
        when (event.type) {
          RendererDiscoverer.Event.ItemAdded -> if (devices.size < 256 && item.retain()) {
            devices[nextDevice++] = item
            devicesDirty = true
          }
          RendererDiscoverer.Event.ItemDeleted -> {
            val key = devices.entries.find { it.value.instance == item.instance }?.key
            if (key != null) { devices.remove(key)?.release(); devicesDirty = true }
          }
        }
      }
    }
    if (search.start()) return true
    stopDiscovery()
    return false
  }

  private fun stopDiscovery() {
    discovery?.let { it.setEventListener(null); it.stop(); it.release() }
    discovery = null
    multicast?.let { if (it.isHeld) it.release() }
    multicast = null
  }

  private data class Cue(val start: Long, val end: Long, val text: String)
  private var captions = emptyList<Cue>()
  private var externalTrack = -1
  private var captionView: TextView? = null

  private fun showCaption(text: String?) {
    captionView?.let { view ->
      if (view.text.toString() != text.orEmpty()) view.text = text.orEmpty()
      view.visibility = if (text.isNullOrBlank()) View.GONE else View.VISIBLE
    }
  }

  fun close() {
    pendingStart = null
    captions = emptyList(); externalTrack = -1; captionView = null
    val oldSystem = system; system = null
    val oldVlc = vlc; vlc = null
    oldVlc?.let { it.setEventListener(null); it.detachViews() }
    val oldDiscovery = discovery; discovery = null
    oldDiscovery?.setEventListener(null)
    val oldMulticast = multicast; multicast = null
    val oldDevices = devices.values.toList()
    devices.clear(); selectedDevice = 0
    val oldLibrary = library; library = null
    val oldSurface = surface; surface = null
    view?.let { (it.parent as? ViewGroup)?.removeView(it) }; view = null
    session = ""; ready = false; ended = false; error = ""
    tracksDirty = true; devicesDirty = true
    changingRenderer = false; rendererFailed = false
    if (oldSystem == null && oldVlc == null && oldLibrary == null && oldSurface == null && oldDiscovery == null && oldMulticast == null) return
    releasing = true
    retirement.execute {
      try {
        runCatching { oldSystem?.release() }
        runCatching { oldVlc?.stop() }
        runCatching { oldVlc?.release() }
        runCatching { oldDiscovery?.stop() }
        runCatching { oldDiscovery?.release() }
        runCatching { if (oldMulticast?.isHeld == true) oldMulticast.release() }
        oldDevices.forEach { runCatching { it.release() } }
        runCatching { oldLibrary?.release() }
        runCatching { oldSurface?.release() }
      } finally {
        ui.post {
          releasing = false
          val start = pendingStart
          pendingStart = null
          start?.invoke()
        }
      }
    }
  }

  fun destroy() {
    close()
    retirement.shutdown()
  }

  private fun changeRenderer(player: VlcPlayer, id: Int): Boolean {
    val target = devices[id]
    if (id != 0 && target?.retain() != true) return false
    val previous = devices[selectedDevice]?.takeIf { it.retain() }
    changingRenderer = true
    rendererFailed = false
    ready = false
    ended = false
    retirement.execute {
      val changed = runCatching {
        player.stop()
        if (player.setRenderer(target) == 0) true
        else { player.setRenderer(previous); false }
      }.getOrDefault(false)
      runCatching { player.play() }
      target?.release()
      previous?.release()
      ui.post {
        if (vlc === player) {
          if (changed) selectedDevice = id
          rendererFailed = !changed
          changingRenderer = false
          devicesDirty = true
        }
      }
    }
    return true
  }

  private fun layout(web: WebView, box: JSONObject) {
    val target = view ?: return
    val scale = web.width / box.optDouble("pageWidth", 1.0).coerceAtLeast(1.0)
    val origin = IntArray(2); web.getLocationInWindow(origin)
    val root = target.parent as View; val offset = IntArray(2); root.getLocationInWindow(offset)
    val frame = FrameLayout.LayoutParams((box.optDouble("width",1.0)*scale).roundToInt().coerceAtLeast(1),
      (box.optDouble("height",1.0)*scale).roundToInt().coerceAtLeast(1))
    frame.leftMargin = origin[0]-offset[0]+(box.optDouble("x",0.0)*scale).roundToInt()
    frame.topMargin = origin[1]-offset[1]+(box.optDouble("y",0.0)*scale).roundToInt()
    target.layoutParams = frame
    target.visibility = if (box.optBoolean("visible",true)) View.VISIBLE else View.INVISIBLE
  }

  private fun start(a: JSONObject, web: WebView) {
    close()
    session = a.getString("id")
    if (releasing) {
      val id = session
      pendingStart = {
        try { open(a, web) }
        catch (_: Exception) { close(); session = id; error = "Playback" }
      }
    } else open(a, web)
  }

  private fun open(a: JSONObject, web: WebView) {
    val uri = Uri.parse(a.getString("source"))
    val root = owner.findViewById<FrameLayout>(android.R.id.content)
    if (a.getString("engine") == "vlc") {
      val renderer = VLCVideoLayout(owner); view = renderer; root.addView(renderer)
      layout(web,a.getJSONObject("box"))
      val core = LibVLC(owner, arrayListOf("--no-video-title-show")); library=core
      val player = VlcPlayer(core); vlc = player
      player.attachViews(renderer,null,false,false)
      player.setEventListener { event -> if (vlc === player) when(event.type) {
        VlcPlayer.Event.Playing -> { ready=true; ended=false; tracksDirty=true }
        VlcPlayer.Event.ESAdded, VlcPlayer.Event.ESDeleted, VlcPlayer.Event.ESSelected -> tracksDirty=true
        VlcPlayer.Event.EndReached -> ended=true
        VlcPlayer.Event.EncounteredError -> error="VLC"
        else -> Unit
      } }
      val media=Media(core,uri)
      try {
        a.optJSONArray("tracks")?.let { tracks -> for (i in 0 until tracks.length()) {
          val track=tracks.getJSONObject(i)
          media.addSlave(org.videolan.libvlc.interfaces.IMedia.Slave(0,if(track.optBoolean("default"))4 else 2,track.getString("url")))
        } }
        player.media=media
      } finally { media.release() }
      player.play()
    } else {
      val frame = FrameLayout(owner)
      view = frame; root.addView(frame); layout(web, a.getJSONObject("box"))
      val renderer = TextureView(owner)
      frame.addView(renderer, FrameLayout.LayoutParams(-1, -1))
      captionView = TextView(owner).also { text ->
        text.setTextColor(android.graphics.Color.WHITE)
        text.setBackgroundColor(0xB0000000.toInt()); text.gravity = Gravity.CENTER
        text.isFocusable = false; text.visibility = View.GONE
        frame.addView(text, FrameLayout.LayoutParams(-2, -2, Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL))
      }
      val player=MediaPlayer(); system=player
      fun attach(texture:SurfaceTexture) { surface?.release(); surface=Surface(texture); player.setSurface(surface) }
      renderer.surfaceTextureListener=object:TextureView.SurfaceTextureListener {
        override fun onSurfaceTextureAvailable(t:SurfaceTexture,w:Int,h:Int) { if(system===player) attach(t) }
        override fun onSurfaceTextureSizeChanged(t:SurfaceTexture,w:Int,h:Int) {}
        override fun onSurfaceTextureUpdated(t:SurfaceTexture) {}
        override fun onSurfaceTextureDestroyed(t:SurfaceTexture):Boolean { if(system===player) {player.setSurface(null);surface?.release();surface=null}; return true }
      }
      renderer.surfaceTexture?.let { attach(it) }
      player.setOnTimedTextListener { active, text -> if(system===active && externalTrack == -1) showCaption(text?.text) }
      player.setOnPreparedListener { if(system===it) { ready=true; tracksDirty=true; it.start() } }
      player.setOnCompletionListener { if(system===it) ended=true }
      player.setOnErrorListener { active,_,_ -> if(system===active) error="Android"; true }
      player.setOnInfoListener { active,what,_ ->
        if(system===active && what==MediaPlayer.MEDIA_INFO_METADATA_UPDATE) tracksDirty=true
        false
      }
      player.setDataSource(owner,uri); player.prepareAsync()
    }
  }

  fun command(op:String,a:JSONObject,web:WebView):JSONObject {
    val result=JSONObject()
    try {
      if (op=="wireless") {
        owner.startActivity(android.content.Intent(android.provider.Settings.ACTION_CAST_SETTINGS))
        return result
      }
      if (op=="start") start(a,web)
      if (session!=a.optString("id")) return result.put("closed",true)
      val player=system; val v=vlc
      if (changingRenderer && op != "close") {
        if (op == "layout") layout(web, a.getJSONObject("box"))
        return result.put("ready", false).put("paused", true)
      }
      when (op) {
        "close" -> { close();return result.put("closed",true) }
        "discover" -> {
          if (!findScreens()) return result.put("error", "Discovery")
          devicesDirty = true
        }
        "renderer" -> {
          val id = a.getInt("value")
          if (v == null || !changeRenderer(v, id)) return result.put("error","Renderer")
          return result.put("ready", false).put("paused", true)
        }
        "layout" -> layout(web,a.getJSONObject("box"))
        "pause" -> if(ready) { player?.pause();v?.pause() }
        "play" -> if(ready) { ended=false;player?.start();v?.play() }
        "volume" -> { val percent = a.getInt("value").coerceIn(0,100); val gain = percent / 100f; player?.setVolume(gain,gain); v?.setVolume(percent) }
        "seek" -> if(ready) {val ms=(a.getDouble("value")*1000).toLong();player?.seekTo(ms.coerceIn(0,Int.MAX_VALUE.toLong()).toInt());v?.time=ms;ended=false}
        "audio","subtitle" -> if(ready) {
          val track=a.getInt("value")
          if(v!=null){ if(op=="audio") v.audioTrack=track else v.spuTrack=track }
          else if (player != null && op == "subtitle") {
            val cues = if (track < -1) a.getJSONArray("cues").let { values ->
              List(values.length()) { index ->
                val cue = values.getJSONObject(index)
                Cue(cue.getLong("start"), cue.getLong("end"), cue.getString("text"))
              }
            } else emptyList()
            if (track < -1 && cues.isEmpty()) return result.put("error", "Subtitle")
            for (type in listOf(MediaPlayer.TrackInfo.MEDIA_TRACK_TYPE_SUBTITLE, MediaPlayer.TrackInfo.MEDIA_TRACK_TYPE_TIMEDTEXT)) {
              val selected = player.getSelectedTrack(type)
              if (selected >= 0) player.deselectTrack(selected)
            }
            externalTrack = if (track < -1) track else -1
            captions = cues; showCaption(null)
            if (track >= 0) player.selectTrack(track)
          } else if (player != null && track >= 0) player.selectTrack(track)
        }
      }
      if(error.isNotEmpty()) return result.put("error",error)
      if (rendererFailed) {
        result.put("rendererFailed", true)
        rendererFailed = false
      }
      if (ready && player != null && externalTrack < -1) {
        val now = player.currentPosition.toLong()
        val match = captions.binarySearchBy(now) { it.start }
        val cue = captions.getOrNull(if (match >= 0) match else -match-2)
        showCaption(cue?.takeIf { now < it.end }?.text)
      }
      if (discovery != null && devicesDirty) {
        val screens = JSONArray()
        devices.forEach { (id,item) -> screens.put(JSONObject().put("id",id).put("name",item.displayName)) }
        result.put("renderers",screens).put("selectedRenderer",selectedDevice)
        devicesDirty = false
      }
      if (ready && tracksDirty) {
        val audio=JSONArray();val subtitles=JSONArray()
        fun add(list:JSONArray,id:Int,name:String){list.put(JSONObject().put("id",id).put("name",name))}
        if(v!=null) { v.audioTracks?.forEach {add(audio,it.id,it.name)};v.spuTracks?.forEach {add(subtitles,it.id,it.name)} }
        if(player!=null) player.trackInfo.forEachIndexed {i,t->
          if(t.trackType==MediaPlayer.TrackInfo.MEDIA_TRACK_TYPE_AUDIO) add(audio,i,t.language)
          if(t.trackType==MediaPlayer.TrackInfo.MEDIA_TRACK_TYPE_SUBTITLE || t.trackType==MediaPlayer.TrackInfo.MEDIA_TRACK_TYPE_TIMEDTEXT) add(subtitles,i,t.language)
        }
        result.put("audio",audio).put("subtitle",subtitles)
        tracksDirty = false
      }
      return result.put("position",if(ready) ((player?.currentPosition?.toLong() ?: v?.time ?: 0L).coerceAtLeast(0)/1000.0) else 0)
        .put("duration",if(ready) ((player?.duration?.toLong() ?: v?.length ?: 0L).coerceAtLeast(0)/1000.0) else 0)
        .put("paused",!(ready && (player?.isPlaying ?: v?.isPlaying ?: false))).put("ready",ready)
        .put("ended",ended)
        .put("selectedAudio",if(ready) (v?.audioTrack ?: player?.getSelectedTrack(MediaPlayer.TrackInfo.MEDIA_TRACK_TYPE_AUDIO) ?: -1) else -1)
        .put("selectedSubtitle",if(ready) (v?.spuTrack ?: (if (externalTrack < -1) externalTrack else player?.getSelectedTrack(MediaPlayer.TrackInfo.MEDIA_TRACK_TYPE_TIMEDTEXT)?.takeIf { it >= 0 } ?: player?.getSelectedTrack(MediaPlayer.TrackInfo.MEDIA_TRACK_TYPE_SUBTITLE)) ?: -1) else -1)
    } catch (_:Exception) { if(op=="start")close();return result.put("error","Playback") }
  }
}
