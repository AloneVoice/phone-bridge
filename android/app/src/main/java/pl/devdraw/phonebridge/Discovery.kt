package pl.devdraw.phonebridge

import android.content.Context
import android.net.ConnectivityManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException

data class FoundServer(val name: String, val host: String, val port: Int, val fingerprint: String)

/**
 * Wyszukiwanie komputerów broadcastem UDP (docs/protocol.md). Odpowiedzi są niezaufane –
 * tożsamość komputera i tak sprawdza TLS z przypiętym odciskiem.
 */
object Discovery {
    private val REQUEST = """{"pb":1,"q":"discover"}""".toByteArray()

    suspend fun search(context: Context, durationMs: Long = 1500): List<FoundServer> = withContext(Dispatchers.IO) {
        val cm = context.getSystemService(ConnectivityManager::class.java)
        val network = cm.activeNetwork
        val targets = buildList {
            add(InetAddress.getByName("255.255.255.255"))
            cm.getLinkProperties(network)?.linkAddresses?.forEach { link ->
                val address = link.address
                if (address is Inet4Address) add(subnetBroadcast(address, link.prefixLength))
            }
        }.distinct()

        val found = LinkedHashMap<String, FoundServer>()
        DatagramSocket().use { socket ->
            network?.bindSocket(socket)
            socket.broadcast = true
            socket.soTimeout = 250
            val deadline = System.currentTimeMillis() + durationMs
            var nextSend = 0L
            val buffer = ByteArray(1024)
            while (System.currentTimeMillis() < deadline) {
                if (System.currentTimeMillis() >= nextSend) {
                    for (target in targets) {
                        runCatching { socket.send(DatagramPacket(REQUEST, REQUEST.size, target, Protocol.DISCOVERY_PORT)) }
                    }
                    nextSend = System.currentTimeMillis() + 500
                }
                val packet = DatagramPacket(buffer, buffer.size)
                try {
                    socket.receive(packet)
                } catch (_: SocketTimeoutException) {
                    continue
                }
                parse(packet)?.let { found["${it.fingerprint}@${it.host}"] = it }
            }
        }
        found.values.toList()
    }

    /** Szybki test, czy pod adresem nasłuchuje serwer (bez TLS). */
    suspend fun isReachable(host: String, port: Int, timeoutMs: Int = 1500): Boolean = withContext(Dispatchers.IO) {
        runCatching { Socket().use { it.connect(InetSocketAddress(host, port), timeoutMs) } }.isSuccess
    }

    private fun parse(packet: DatagramPacket): FoundServer? = runCatching {
        val json = JSONObject(String(packet.data, 0, packet.length, Charsets.UTF_8))
        val fingerprint = json.getString("fp").lowercase()
        val port = json.getInt("port")
        if (json.optInt("pb") != Protocol.VERSION || !Hex.isHex(fingerprint) || port !in 1..65535) return null
        val name = json.optString("name").filter { !it.isISOControl() }.trim().take(64).ifEmpty { "Komputer" }
        FoundServer(name, packet.address.hostAddress ?: return null, port, fingerprint)
    }.getOrNull()

    private fun subnetBroadcast(address: Inet4Address, prefixLength: Int): InetAddress {
        val ip = address.address.fold(0) { acc, b -> (acc shl 8) or (b.toInt() and 0xFF) }
        val mask = if (prefixLength == 0) 0 else -1 shl (32 - prefixLength)
        val broadcast = ip or mask.inv()
        return InetAddress.getByAddress(ByteArray(4) { i -> (broadcast shr (24 - 8 * i)).toByte() })
    }
}
