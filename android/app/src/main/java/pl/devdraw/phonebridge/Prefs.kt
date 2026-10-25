package pl.devdraw.phonebridge

import android.content.Context
import android.net.Uri
import androidx.core.content.edit

data class PairedServer(val name: String, val fingerprint: String, val port: Int, val hosts: List<String>)

/** Gdzie zapisywać pliki odebrane z komputera. */
sealed interface Destination {
    /** Pobrane/PhoneBridge przez MediaStore – nie wymaga żadnych uprawnień. */
    data object Downloads : Destination

    /** Folder wybrany przez użytkownika w systemowym oknie (SAF). */
    data class Tree(val uri: Uri) : Destination

    fun encode(): String = when (this) {
        Downloads -> DOWNLOADS
        is Tree -> uri.toString()
    }

    companion object {
        private const val DOWNLOADS = "downloads"

        fun decode(value: String?): Destination =
            if (value.isNullOrEmpty() || value == DOWNLOADS) Downloads else Tree(Uri.parse(value))
    }
}

class Prefs(context: Context) {
    private val prefs = context.getSharedPreferences("phonebridge", Context.MODE_PRIVATE)

    var server: PairedServer?
        get() {
            val fingerprint = prefs.getString("server.fp", null) ?: return null
            return PairedServer(
                name = prefs.getString("server.name", null) ?: "Komputer",
                fingerprint = fingerprint,
                port = prefs.getInt("server.port", Protocol.DEFAULT_PORT),
                hosts = prefs.getString("server.hosts", "").orEmpty().split(',').filter { it.isNotBlank() },
            )
        }
        set(value) = prefs.edit {
            if (value == null) {
                remove("server.fp"); remove("server.name"); remove("server.port"); remove("server.hosts")
            } else {
                putString("server.fp", value.fingerprint)
                putString("server.name", value.name)
                putInt("server.port", value.port)
                putString("server.hosts", value.hosts.joinToString(","))
            }
        }

    /** Zapamiętuje adres, pod którym komputer ostatnio odpowiedział (na początku listy). */
    fun rememberHost(host: String, port: Int) {
        val current = server ?: return
        server = current.copy(port = port, hosts = (listOf(host) + current.hosts.filter { it != host }).take(5))
    }

    var keepConnected: Boolean
        get() = prefs.getBoolean("keepConnected", true)
        set(value) = prefs.edit { putBoolean("keepConnected", value) }

    /** Odbieranie plików od sparowanego komputera bez pytania, do [destination]. */
    var autoAccept: Boolean
        get() = prefs.getBoolean("autoAccept", false)
        set(value) = prefs.edit { putBoolean("autoAccept", value) }

    /** Powiadomienie „Wysłać na komputer?” po zrobieniu zrzutu ekranu. */
    var offerScreenshots: Boolean
        get() = prefs.getBoolean("offerScreenshots", false)
        set(value) = prefs.edit { putBoolean("offerScreenshots", value) }

    var destination: Destination
        get() = Destination.decode(prefs.getString("destination", null))
        set(value) = prefs.edit { putString("destination", value.encode()) }
}
