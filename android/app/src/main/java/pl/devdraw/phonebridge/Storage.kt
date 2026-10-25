package pl.devdraw.phonebridge

import android.content.ContentResolver
import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Environment
import android.provider.DocumentsContract
import android.provider.MediaStore
import android.provider.OpenableColumns
import android.webkit.MimeTypeMap
import androidx.documentfile.provider.DocumentFile
import java.io.IOException
import java.io.OutputStream

/** Plik w trakcie zapisu: widoczny dopiero po [commit], usuwany przy błędzie. */
class OutputTarget(
    private val resolver: ContentResolver,
    val uri: Uri,
    private val pending: Boolean,
    private val isDocument: Boolean,
) {
    fun open(): OutputStream = resolver.openOutputStream(uri, "w") ?: throw IOException("Nie można otworzyć pliku do zapisu")

    fun commit() {
        if (pending) resolver.update(uri, ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }, null, null)
    }

    fun delete() {
        runCatching {
            if (isDocument) DocumentsContract.deleteDocument(resolver, uri) else resolver.delete(uri, null, null)
        }
    }
}

/** Tworzy pliki (z podfolderami) w wybranym miejscu. Nazwy muszą być już zsanityzowane. */
class DestinationWriter(private val context: Context, private val destination: Destination) {
    private val resolver = context.contentResolver
    private val directories = HashMap<String, DocumentFile>()

    fun create(segments: List<String>): OutputTarget = when (destination) {
        Destination.Downloads -> createInDownloads(segments)
        is Destination.Tree -> createInTree(destination.uri, segments)
    }

    private fun createInDownloads(segments: List<String>): OutputTarget {
        val name = segments.last()
        val relativePath = (listOf(Environment.DIRECTORY_DOWNLOADS, "PhoneBridge") + segments.dropLast(1)).joinToString("/") + "/"
        val values = ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME, name)
            put(MediaStore.MediaColumns.RELATIVE_PATH, relativePath)
            put(MediaStore.MediaColumns.MIME_TYPE, mimeType(name))
            put(MediaStore.MediaColumns.IS_PENDING, 1)
        }
        val uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)
            ?: throw IOException("Nie można utworzyć pliku w Pobranych")
        return OutputTarget(resolver, uri, pending = true, isDocument = false)
    }

    private fun createInTree(treeUri: Uri, segments: List<String>): OutputTarget {
        var dir = DocumentFile.fromTreeUri(context, treeUri)?.takeIf { it.canWrite() }
            ?: throw IOException("Brak dostępu do wybranego folderu – wybierz go ponownie")
        var path = ""
        for (segment in segments.dropLast(1)) {
            path += "/$segment"
            val parent = dir
            dir = directories.getOrPut(path) {
                parent.findFile(segment)?.takeIf { it.isDirectory }
                    ?: parent.createDirectory(segment)
                    ?: throw IOException("Nie można utworzyć folderu")
            }
        }
        // octet-stream: dostawca nie dokleja rozszerzenia; przy kolizji sam nada nazwę "plik (1).ext"
        val file = dir.createFile("application/octet-stream", segments.last())
            ?: throw IOException("Nie można utworzyć pliku")
        return OutputTarget(resolver, file.uri, pending = false, isDocument = true)
    }

    companion object {
        fun displayName(context: Context, destination: Destination): String = when (destination) {
            Destination.Downloads -> "Pobrane/PhoneBridge"
            is Destination.Tree -> DocumentFile.fromTreeUri(context, destination.uri)?.name ?: "wybrany folder"
        }

        /** Czy nadal mamy trwałe uprawnienie do zapisu w folderze. */
        fun isUsable(context: Context, destination: Destination): Boolean = when (destination) {
            Destination.Downloads -> true
            is Destination.Tree -> context.contentResolver.persistedUriPermissions.any { it.uri == destination.uri && it.isWritePermission }
        }

        private fun mimeType(name: String): String {
            val ext = name.substringAfterLast('.', "").lowercase()
            return MimeTypeMap.getSingleton().getMimeTypeFromExtension(ext) ?: "application/octet-stream"
        }
    }
}

data class SourceFile(val uri: Uri, val name: String, val size: Long)

object Sources {
    /** Nazwa i rozmiar udostępnionego pliku. Gdy aplikacja źródłowa nie podaje rozmiaru – liczymy go. */
    fun describe(resolver: ContentResolver, uri: Uri): SourceFile {
        var name: String? = null
        var size: Long? = null
        runCatching {
            resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { c ->
                if (c.moveToFirst()) {
                    c.getColumnIndex(OpenableColumns.DISPLAY_NAME).takeIf { it >= 0 && !c.isNull(it) }?.let { name = c.getString(it) }
                    c.getColumnIndex(OpenableColumns.SIZE).takeIf { it >= 0 && !c.isNull(it) }?.let { size = c.getLong(it) }
                }
            }
        }
        if (size == null || size!! < 0) {
            size = runCatching { resolver.openAssetFileDescriptor(uri, "r")?.use { it.length } }.getOrNull()?.takeIf { it >= 0 }
        }
        if (size == null) {
            size = resolver.openInputStream(uri)?.use { input ->
                val buffer = ByteArray(256 * 1024)
                var total = 0L
                while (true) {
                    val read = input.read(buffer)
                    if (read < 0) break
                    total += read
                }
                total
            } ?: throw IOException("Nie można odczytać pliku")
        }
        return SourceFile(uri, name ?: uri.lastPathSegment ?: FileNames.FALLBACK, size!!)
    }
}
