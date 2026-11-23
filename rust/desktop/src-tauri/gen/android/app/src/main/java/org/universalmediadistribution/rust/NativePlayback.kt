package org.universalmediadistribution.rust

import android.graphics.SurfaceTexture
import android.media.MediaPlayer
import android.net.Uri
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

  private var discovery: RendererDiscoverer? = null
  private val devices = mutableMapOf<Int,RendererItem>()
  private var nextDevice = 1
  private var selectedDevice = 0

  private fun findScreens(): Boolean {
    if (discovery != null) return true
    val core = library ?: return false
    val service = RendererDiscoverer.list(core)?.firstOrNull() ?: return false
    val search = RendererDiscoverer(core, service.name)
    discovery = search
    search.setEventListener { event ->
      if (discovery === search) {
        val item = event.item
        when (event.type) {
          RendererDiscoverer.Event.ItemAdded -> if (devices.size < 256 && item.retain()) {
            devices[nextDevice++] = item
          }
          RendererDiscoverer.Event.ItemDeleted -> {
            val key = devices.entries.find { it.value.instance == item.instance }?.key
            if (key != null) devices.remove(key)?.release()
          }
        }
      }
    }
    if (search.start()) return true
    search.setEventListener(null); search.release(); discovery = null
    return false
  }

  private data class Cue(val start: Long, val end: Long, val text: String)
  private var captions = emptyList<Cue>()
  private var externalTrack = -1
  private var captionView: TextView? = null

  private fun parseCaptions(source: String): List<Cue> {
    val timing = Regex("""((?:\d{1,3}:)?\d{2}:\d{2}[.,]\d{3})\s+-->\s+((?:\d{1,3}:)?\d{2}:\d{2}[.,]\d{3})""")
    fun millis(stamp: String): Long {
      val parts = stamp.replace(',', '.').split(':', '.').map(String::toLong)
      return parts.dropLast(1).fold(0L) { total, part -> total * 60 + part } * 1000 + parts.last()
    }
    return source.replace("\r", "").split(Regex("""\n[ \t]*\n""")).mapNotNull { block ->
      val lines = block.lines()
      val index = lines.indexOfFirst { timing.containsMatchIn(it) }
      if (index < 0) return@mapNotNull null
      val match = timing.find(lines[index]) ?: return@mapNotNull null
      val text = lines.drop(index+1).joinToString("\n").replace(Regex("<[^>]+>"), "")
        .replace("&lt;", "<").replace("&gt;", ">").replace("&amp;", "&")
      Cue(millis(match.groupValues[1]), millis(match.groupValues[2]), text)
    }.sortedBy { it.start }
  }

  private fun showCaption(text: String?) {
    captionView?.let { view ->
      if (view.text.toString() != text.orEmpty()) view.text = text.orEmpty()
      view.visibility = if (text.isNullOrBlank()) View.GONE else View.VISIBLE
    }
  }

  fun close() {
    captions = emptyList(); externalTrack = -1; captionView = null
    system?.release(); system = null
    vlc?.let { it.setEventListener(null); it.stop(); it.detachViews(); it.release() }; vlc = null
    discovery?.let { it.setEventListener(null); it.stop(); it.release() }; discovery = null
    devices.values.forEach { it.release() }; devices.clear(); selectedDevice = 0
    library?.release(); library = null
    surface?.release(); surface = null
    view?.let { (it.parent as? ViewGroup)?.removeView(it) }; view = null
    session = ""; ready = false; ended = false; error = ""
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
    val uri = Uri.parse(a.getString("source"))
    val root = owner.findViewById<FrameLayout>(android.R.id.content)
    if (a.getString("engine") == "vlc") {
      val renderer = VLCVideoLayout(owner); view = renderer; root.addView(renderer)
      layout(web,a.getJSONObject("box"))
      val core = LibVLC(owner, arrayListOf("--no-video-title-show")); library=core
      val player = VlcPlayer(core); vlc = player
      player.attachViews(renderer,null,false,false)
      player.setEventListener { event -> if (vlc === player) when(event.type) {
        VlcPlayer.Event.Playing -> { ready=true; ended=false }
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
      player.setOnTimedTextListener { _, text -> if(externalTrack == -1) showCaption(text?.text) }
      player.setOnPreparedListener { ready=true; it.start() }
      player.setOnCompletionListener { ended=true }
      player.setOnErrorListener { _,_,_ -> error="Android"; true }
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
      when (op) {
        "close" -> { close();return result.put("closed",true) }
        "discover" -> if (!findScreens()) return result.put("error", "Discovery")
        "renderer" -> {
          val id = a.getInt("value")
          if (v == null || (id != 0 && !devices.containsKey(id))) return result.put("error","Renderer")
          v.stop(); ready = false; ended = false
          if (v.setRenderer(devices[id]) != 0) {
            v.setRenderer(devices[selectedDevice]); v.play()
            return result.put("error","Renderer")
          }
          selectedDevice = id; v.play()
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
            val cues = if (track < -1) parseCaptions(a.getString("text")) else emptyList()
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
      if (ready && player != null && externalTrack < -1) {
        val now = player.currentPosition.toLong()
        val match = captions.binarySearchBy(now) { it.start }
        val cue = captions.getOrNull(if (match >= 0) match else -match-2)
        showCaption(cue?.takeIf { now < it.end }?.text)
      }
      if (discovery != null) {
        val screens = JSONArray()
        devices.forEach { (id,item) -> screens.put(JSONObject().put("id",id).put("name",item.displayName)) }
        result.put("renderers",screens).put("selectedRenderer",selectedDevice)
      }
      val audio=JSONArray();val subtitles=JSONArray()
      fun add(list:JSONArray,id:Int,name:String){list.put(JSONObject().put("id",id).put("name",name))}
      if(ready && v!=null) { v.audioTracks?.forEach {add(audio,it.id,it.name)};v.spuTracks?.forEach {add(subtitles,it.id,it.name)} }
      if(ready && player!=null) player.trackInfo.forEachIndexed {i,t->
        if(t.trackType==MediaPlayer.TrackInfo.MEDIA_TRACK_TYPE_AUDIO) add(audio,i,t.language)
        if(t.trackType==MediaPlayer.TrackInfo.MEDIA_TRACK_TYPE_SUBTITLE || t.trackType==MediaPlayer.TrackInfo.MEDIA_TRACK_TYPE_TIMEDTEXT) add(subtitles,i,t.language)
      }
      return result.put("position",if(ready) ((player?.currentPosition?.toLong() ?: v?.time ?: 0L).coerceAtLeast(0)/1000.0) else 0)
        .put("duration",if(ready) ((player?.duration?.toLong() ?: v?.length ?: 0L).coerceAtLeast(0)/1000.0) else 0)
        .put("paused",!(ready && (player?.isPlaying ?: v?.isPlaying ?: false))).put("ready",ready)
        .put("ended",ended).put("audio",audio).put("subtitle",subtitles)
        .put("selectedAudio",if(ready) (v?.audioTrack ?: player?.getSelectedTrack(MediaPlayer.TrackInfo.MEDIA_TRACK_TYPE_AUDIO) ?: -1) else -1)
        .put("selectedSubtitle",if(ready) (v?.spuTrack ?: (if (externalTrack < -1) externalTrack else player?.getSelectedTrack(MediaPlayer.TrackInfo.MEDIA_TRACK_TYPE_TIMEDTEXT)?.takeIf { it >= 0 } ?: player?.getSelectedTrack(MediaPlayer.TrackInfo.MEDIA_TRACK_TYPE_SUBTITLE)) ?: -1) else -1)
    } catch (_:Exception) { if(op=="start")close();return result.put("error","Playback") }
  }
}
