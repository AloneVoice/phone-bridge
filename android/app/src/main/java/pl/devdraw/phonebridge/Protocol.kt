package pl.devdraw.phonebridge

import org.json.JSONObject
import java.security.MessageDigest

/** Stałe i formaty z docs/protocol.md. */
object Protocol {
    const val VERSION = 1
    const val DEFAULT_PORT = 47800
    const val DISCOVERY_PORT = 47801
    const val MAX_FILES = 10_000
    const val MAX_DEPTH = 32
    const val MAX_RAW_NAME = 1024

    fun baseUrl(host: String, port: Int): String =
        if (host.contains(':')) "https://[$host]:$port" else "https://$host:$port"

    /** Parsuje ofertę komputer → telefon. Zwraca null dla nieprawidłowych danych. */
    fun parseOffer(json: JSONObject): Offer? {
        val id = json.optString("id")
        if (!Hex.isHex(id, 16)) return null
        val array = json.optJSONArray("files") ?: return null
        if (array.length() !in 1..MAX_FILES) return null
        val files = ArrayList<FileEntry>(array.length())
        for (i in 0 until array.length()) {
            val item = array.optJSONObject(i) ?: return null
            val name = item.optString("name")
            val size = item.optLong("size", -1)
            if (name.isEmpty() || name.length > MAX_RAW_NAME || size < 0) return null
            files.add(FileEntry(name, size))
        }
        return Offer(id, files)
    }
}

data class FileEntry(val name: String, val size: Long)

data class Offer(val id: String, val files: List<FileEntry>) {
    val totalBytes: Long get() = files.sumOf { it.size }
}

object Hex {
    private const val DIGITS = "0123456789abcdef"

    fun encode(bytes: ByteArray): String {
        val sb = StringBuilder(bytes.size * 2)
        for (b in bytes) {
            sb.append(DIGITS[(b.toInt() shr 4) and 0xF])
            sb.append(DIGITS[b.toInt() and 0xF])
        }
        return sb.toString()
    }

    fun decode(hex: String): ByteArray {
        require(hex.length % 2 == 0) { "Nieparzysta długość hex" }
        return ByteArray(hex.length / 2) { i -> hex.substring(i * 2, i * 2 + 2).toInt(16).toByte() }
    }

    fun isHex(value: String?, bytes: Int = 32): Boolean =
        value != null && value.length == bytes * 2 && value.all { it in '0'..'9' || it in 'a'..'f' || it in 'A'..'F' }
}

fun sha256Hex(bytes: ByteArray): String = Hex.encode(MessageDigest.getInstance("SHA-256").digest(bytes))

/** Kod porównywany przy parowaniu – identyczny algorytm jak w PhoneBridge.Core.Security.Sas. */
object Sas {
    private val LABEL = "PhoneBridge-SAS-v1".toByteArray(Charsets.US_ASCII)

    fun compute(serverFingerprint: String, clientFingerprint: String, phoneNonce: ByteArray, serverNonce: ByteArray): String {
        val md = MessageDigest.getInstance("SHA-256")
        md.update(LABEL)
        md.update(Hex.decode(serverFingerprint))
        md.update(Hex.decode(clientFingerprint))
        md.update(phoneNonce)
        md.update(serverNonce)
        val d = md.digest()
        val value = ((d[0].toLong() and 0xFF) shl 24) or ((d[1].toLong() and 0xFF) shl 16) or
            ((d[2].toLong() and 0xFF) shl 8) or (d[3].toLong() and 0xFF)
        return (value % 1_000_000).toString().padStart(6, '0')
    }

    fun format(code: String): String = code.substring(0, 3) + " " + code.substring(3)
}

/** Bezpieczne nazwy plików i folderów dla pamięci Androida (także karty SD z FAT). */
object FileNames {
    const val FALLBACK = "plik"
    private const val MAX_LENGTH = 200
    private const val INVALID = "\"*/:<>?\\|"

    fun sanitize(name: String): String {
        val sb = StringBuilder(name.length)
        for (c in name) sb.append(if (isForbidden(c)) '_' else c)
        val result = sb.toString().trim().trimEnd('.', ' ')
        return if (result.isEmpty()) FALLBACK else truncate(result)
    }

    /** Nazwa względna od nadawcy ("Folder/pod/plik.txt") → bezpieczne segmenty, bez wychodzenia poza folder docelowy. */
    fun segments(relativeName: String): List<String> {
        val parts = relativeName.split('/', '\\')
            .filter { it.isNotBlank() && it != "." }
            .map { sanitize(it) }
        if (parts.isEmpty()) return listOf(FALLBACK)
        return if (parts.size > Protocol.MAX_DEPTH) parts.takeLast(Protocol.MAX_DEPTH) else parts
    }

    private fun isForbidden(c: Char): Boolean =
        c.code < 0x20 || c.code == 0x7F || INVALID.indexOf(c) >= 0 ||
            c in '‪'..'‮' || c in '⁦'..'⁩'

    private fun truncate(name: String): String {
        if (name.length <= MAX_LENGTH) return name
        val dot = name.lastIndexOf('.')
        val ext = if (dot > 0 && name.length - dot <= 20) name.substring(dot) else ""
        var stemLength = MAX_LENGTH - ext.length
        if (Character.isHighSurrogate(name[stemLength - 1])) stemLength--
        return name.substring(0, stemLength).trimEnd('.', ' ') + ext
    }
}
