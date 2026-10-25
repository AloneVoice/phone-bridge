package pl.devdraw.phonebridge

import java.util.Locale

object Format {
    private val POLISH = Locale.forLanguageTag("pl-PL")

    fun bytes(bytes: Long): String {
        val units = arrayOf("B", "KB", "MB", "GB", "TB")
        var value = bytes.toDouble()
        var unit = 0
        while (value >= 1024 && unit < units.size - 1) {
            value /= 1024
            unit++
        }
        if (unit == 0) return "$bytes B"
        return String.format(POLISH, if (value < 10) "%.1f %s" else "%.0f %s", value, units[unit])
    }

    /** 1 plik, 2 pliki, 5 plików, 22 pliki, 112 plików. */
    fun files(count: Int): String {
        val lastTwo = count % 100
        val last = count % 10
        val word = when {
            count == 1 -> "plik"
            last in 2..4 && lastTwo !in 12..14 -> "pliki"
            else -> "plików"
        }
        return "$count $word"
    }
}
