package org.universalmediadistribution.rust

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.BitmapRegionDecoder
import android.graphics.Rect
import androidx.annotation.Keep
import java.io.ByteArrayOutputStream
import java.io.FileInputStream

@Keep
internal object ReaderTiles {
    @JvmStatic external fun register()

    @Suppress("DEPRECATION")
    @JvmStatic fun open(path: String): BitmapRegionDecoder? = FileInputStream(path).use {
        BitmapRegionDecoder.newInstance(it.fd, false)
    }

    @JvmStatic fun read(decoder: BitmapRegionDecoder, top: Int, height: Int, sample: Int): ByteArray? {
        val bounds = Rect(0, top, decoder.width, (top + height).coerceAtMost(decoder.height))
        val options = BitmapFactory.Options().apply {
            inSampleSize = sample
            inPreferredConfig = Bitmap.Config.ARGB_8888
        }
        val bitmap = decoder.decodeRegion(bounds, options) ?: return null
        return try {
            val output = ByteArrayOutputStream()
            if (bitmap.compress(Bitmap.CompressFormat.PNG, 100, output)) output.toByteArray() else null
        } finally {
            bitmap.recycle()
        }
    }
}
