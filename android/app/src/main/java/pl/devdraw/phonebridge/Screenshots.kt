package pl.devdraw.phonebridge

import android.Manifest
import android.content.ContentUris
import android.content.Context
import android.content.pm.PackageManager
import android.database.ContentObserver
import android.net.Uri
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.MediaStore
import androidx.core.content.ContextCompat

/**
 * Obserwuje MediaStore i zgłasza nowe zrzuty ekranu (pliki w folderze „Screenshots”).
 * Wymaga READ_MEDIA_IMAGES; działa, dopóki działa usługa pierwszoplanowa.
 */
class ScreenshotWatcher(
    private val context: Context,
    private val onScreenshot: (uri: Uri, id: Long) -> Unit,
) : ContentObserver(Handler(Looper.getMainLooper())) {

    companion object {
        private const val MAX_AGE_SECONDS = 60

        fun hasPermission(context: Context): Boolean =
            ContextCompat.checkSelfPermission(context, Manifest.permission.READ_MEDIA_IMAGES) == PackageManager.PERMISSION_GRANTED

        /** Rozpoznaje folder zrzutów ekranu (Pictures/Screenshots, DCIM/Screenshots, nazwy producentów). */
        fun isScreenshotPath(relativePath: String?): Boolean =
            relativePath != null && relativePath.contains("screenshot", ignoreCase = true)
    }

    private val seen = ArrayDeque<Long>()
    private var registered = false

    fun register() {
        if (registered) return
        context.contentResolver.registerContentObserver(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, true, this)
        registered = true
    }

    fun unregister() {
        if (!registered) return
        context.contentResolver.unregisterContentObserver(this)
        registered = false
    }

    override fun onChange(selfChange: Boolean, uri: Uri?) {
        runCatching { check(uri) }
    }

    private fun check(uri: Uri?) {
        val id = uri?.lastPathSegment?.toLongOrNull()
        val projection = arrayOf(
            MediaStore.Images.Media._ID,
            MediaStore.Images.Media.RELATIVE_PATH,
            MediaStore.Images.Media.IS_PENDING,
            MediaStore.Images.Media.DATE_ADDED,
        )
        val args = Bundle().apply {
            if (id != null) {
                putString(android.content.ContentResolver.QUERY_ARG_SQL_SELECTION, "${MediaStore.Images.Media._ID} = ?")
                putStringArray(android.content.ContentResolver.QUERY_ARG_SQL_SELECTION_ARGS, arrayOf(id.toString()))
            } else {
                putStringArray(android.content.ContentResolver.QUERY_ARG_SORT_COLUMNS, arrayOf(MediaStore.Images.Media.DATE_ADDED))
                putInt(android.content.ContentResolver.QUERY_ARG_SORT_DIRECTION, android.content.ContentResolver.QUERY_SORT_DIRECTION_DESCENDING)
                putInt(android.content.ContentResolver.QUERY_ARG_LIMIT, 1)
            }
        }
        context.contentResolver.query(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, projection, args, null)?.use { c ->
            if (!c.moveToFirst()) return
            val imageId = c.getLong(0)
            val path = c.getString(1)
            val pending = c.getInt(2) != 0
            val added = c.getLong(3)
            val fresh = System.currentTimeMillis() / 1000 - added <= MAX_AGE_SECONDS
            if (pending || !fresh || !isScreenshotPath(path) || imageId in seen) return
            seen.addLast(imageId)
            if (seen.size > 50) seen.removeFirst()
            onScreenshot(ContentUris.withAppendedId(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, imageId), imageId)
        }
    }
}
