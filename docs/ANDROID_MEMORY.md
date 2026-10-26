# Android image memory budgets

Android TV uses smaller image budgets to leave room for the video decoder,
embedded WebView, and the rest of the application. These are limits on specific
resources, not a guarantee about total process memory.

| Resource | Android TV / constrained Android | Other Android | Desktop |
| --- | --- | --- | --- |
| Unused decoded artwork cache | 8 MiB | 16 MiB | 64 MiB |
| GPU resource cache | 24 MiB | 48 MiB | Platform default |
| Concurrent artwork loads | 2 | 2 | 2–4 |
| Concurrent reader page loads | 2 | 2 | 2 |
| Reader pages retained around the current page | 3 | 3 | 9 |
| Maximum decoded reader / EPUB image | 16 MiB | 16 MiB | 64 MiB |
| Maximum encoded reader page | 24 MiB | 24 MiB | 96 MiB |

Artwork is decoded to its display width with a 4 MiB limit per image. Reader
images also respect their pixel budget when a page is unusually tall. Source
images above 32 megapixels (32 × 1024 × 1024 pixels) are rejected before native
decoding because codecs can allocate temporary storage at the original size.
Images with unrecognized or unreadable headers fail as image errors. A failed
scaled decode is never retried at full resolution.

Queued loads are canceled when their view or reader releases them. Completed
decodes are disposed if cancellation happened while native code was running.
A reattached view starts a new load without allowing the older request to change
its loading state. Disposed view models cannot restart loads. Artwork downloads
reuse their buffered bytes for disk caching instead of copying the entire image
into another array. Cached artwork, remote artwork, and EPUB image decoding run
outside the UI thread.

Android's `OnTrimMemory` releases cache-owned artwork without disposing images
still used by visible views. The hard budgets apply from startup because Android
14 and later no longer send the older foreground low-memory notifications. See
[Android's memory guidance](https://developer.android.com/topic/performance/memory/manage-app-memory).

## Verification

Automated coverage in `ImageMemoryTests` checks real Skia decoding, pixel limits,
oversized cache eviction, cache ownership, canceled queues, reattachment, and
disposed view models. Run the UI and mobile test projects in Release mode.

For device measurements, compare the same release build configuration before and
after these changes, with the same catalog and video source:

1. Browse several catalog pages and repeatedly open/close title details.
2. Read a long chapter, scroll rapidly, switch chapters, and navigate back.
3. Start native video and embedded web playback, then return to the catalog.
4. Background and resume the app several times.
5. Sample `adb shell dumpsys meminfo org.universalmediadistribution.client` after
   each cycle, and check `adb logcat -b crash -d` for crashes. Compare Java,
   native, graphics, and total PSS, not just managed heap size. Check that repeated
   cycles settle rather than continually increase memory.

No Android device was connected during this change, so device PSS, playback
smoothness, and prolonged stability still require this validation. The Android
ARM TV Compile target was checked; this change does not include a new APK.
