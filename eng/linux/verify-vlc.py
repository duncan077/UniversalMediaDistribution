"""Exercise the bundled library without a system VLC installation."""
import ctypes
import os
from pathlib import Path
import struct
import sys
import tempfile
import time
import wave

root = Path(sys.argv[1]).resolve()
os.environ["VLC_PLUGIN_PATH"] = str(root / "libvlc" / "plugins")
vlc = ctypes.CDLL(str(root / "libvlc.so"))
vlc.libvlc_get_version.restype = ctypes.c_char_p
vlc.libvlc_new.argtypes = [ctypes.c_int, ctypes.POINTER(ctypes.c_char_p)]
vlc.libvlc_new.restype = ctypes.c_void_p
vlc.libvlc_media_new_path.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
vlc.libvlc_media_new_path.restype = ctypes.c_void_p
vlc.libvlc_media_player_new_from_media.argtypes = [ctypes.c_void_p]
vlc.libvlc_media_player_new_from_media.restype = ctypes.c_void_p
vlc.libvlc_media_player_play.argtypes = [ctypes.c_void_p]
vlc.libvlc_media_player_get_state.argtypes = [ctypes.c_void_p]
for function in ("libvlc_media_player_release", "libvlc_media_release", "libvlc_release"):
    getattr(vlc, function).argtypes = [ctypes.c_void_p]
options = (ctypes.c_char_p * 4)(b"--no-plugins-cache", b"--aout=dummy", b"--vout=dummy", b"--no-video-title-show")
instance = vlc.libvlc_new(len(options), options)
assert instance, "Bundled VLC failed to initialize"


def verify_media(sample):
    media = vlc.libvlc_media_new_path(instance, os.fsencode(sample))
    assert media
    player = vlc.libvlc_media_player_new_from_media(media)
    assert player
    try:
        assert vlc.libvlc_media_player_play(player) == 0
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            state = vlc.libvlc_media_player_get_state(player)
            if state == 6:  # Ended: input, demux, decoder and output all ran.
                break
            assert state != 7, "VLC playback failed"
            time.sleep(0.05)
        else:
            raise AssertionError("VLC playback did not finish")
    finally:
        vlc.libvlc_media_player_release(player)
        vlc.libvlc_media_release(media)
    print("PASS:", sample.name)


try:
    with tempfile.TemporaryDirectory() as temporary:
        sample = Path(temporary) / "sample.wav"
        with wave.open(str(sample), "wb") as audio:
            audio.setparams((1, 2, 8000, 0, "NONE", "not compressed"))
            audio.writeframes(struct.pack("<h", 0) * 8000)
        verify_media(sample)
        for extra in sys.argv[2:]:
            verify_media(Path(extra))
        # VLC and WebKit share one process in the app. Private older C++/GLib
        # libraries must not shadow the newer desktop ABI on another distro.
        ctypes.CDLL("libwebkit2gtk-4.1.so.0")
        print("PASS: system WebKit loads alongside bundled VLC")
finally:
    vlc.libvlc_release(instance)
print("Bundled VLC playback passed:", vlc.libvlc_get_version().decode())
