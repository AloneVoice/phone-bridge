package pl.devdraw.phonebridge

import android.content.Context
import okhttp3.OkHttpClient
import org.json.JSONObject
import java.io.IOException
import java.security.SecureRandom

/**
 * Parowanie commit/reveal z porównaniem kodu (docs/protocol.md).
 * Pierwsze żądanie idzie bez przypinania – odcisk komputera z tej sesji TLS jest potem przypinany
 * i wchodzi do kodu, który użytkownik porównuje na obu ekranach.
 */
class PairingSession(private val context: Context, val host: String, val port: Int, private val expectedFingerprint: String? = null) {
    private val nonce = ByteArray(32).also { SecureRandom().nextBytes(it) }
    private val base = Protocol.baseUrl(host, port)
    private lateinit var pairingId: String
    private lateinit var pinned: OkHttpClient

    lateinit var serverName: String
        private set
    lateinit var serverFingerprint: String
        private set
    lateinit var code: String
        private set

    suspend fun start() {
        val trust = PinningTrustManager(expectedFingerprint)
        val response = try {
            Http.postJson(Http.client(trust), "$base/v1/pair/start",
                JSONObject().put("deviceName", deviceName(context)).put("commitment", sha256Hex(nonce)))
        } catch (e: HttpException) {
            if (e.code == 429) throw IOException("Komputer obsługuje teraz inne parowanie. Spróbuj ponownie za chwilę.")
            throw e
        }
        serverFingerprint = trust.seenFingerprint ?: throw IOException("Nie otrzymano certyfikatu komputera")
        pairingId = response.getString("pairingId")
        val serverNonce = response.getString("serverNonce")
        if (!Hex.isHex(serverNonce)) throw IOException("Nieprawidłowa odpowiedź komputera")
        serverName = response.optString("serverName").filter { !it.isISOControl() }.trim().take(64).ifEmpty { host }
        code = Sas.compute(serverFingerprint, Identity.fingerprint(), nonce, Hex.decode(serverNonce))
        pinned = Http.pinned(serverFingerprint)
    }

    /** Ujawnia nonce; komputer pokazuje wtedy kod i czeka na decyzję użytkownika. */
    suspend fun reveal(): Boolean =
        Http.postJson(pinned, "$base/v1/pair/reveal",
            JSONObject().put("pairingId", pairingId).put("nonce", Hex.encode(nonce))).optBoolean("accepted")

    suspend fun confirm(): PairedServer {
        val ok = Http.postJson(pinned, "$base/v1/pair/confirm", JSONObject().put("pairingId", pairingId)).optBoolean("ok")
        if (!ok) throw IOException("Komputer nie potwierdził parowania")
        return PairedServer(serverName, serverFingerprint, port, listOf(host))
    }
}
