package pl.devdraw.phonebridge

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import okhttp3.Call
import okhttp3.Callback
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Protocol as HttpProtocol
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import org.json.JSONObject
import java.io.IOException
import java.net.Socket
import java.security.Principal
import java.security.PrivateKey
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLEngine
import javax.net.ssl.X509ExtendedKeyManager
import javax.net.ssl.X509TrustManager
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

class HttpException(val code: Int) : IOException("Błąd serwera: HTTP $code")

/** Przedstawia serwerowi certyfikat telefonu z Android Keystore. */
private class ClientKeyManager : X509ExtendedKeyManager() {
    override fun chooseClientAlias(keyType: Array<out String>?, issuers: Array<out Principal>?, socket: Socket?) = Identity.ALIAS
    override fun chooseEngineClientAlias(keyType: Array<out String>?, issuers: Array<out Principal>?, engine: SSLEngine?) = Identity.ALIAS
    override fun getClientAliases(keyType: String?, issuers: Array<out Principal>?) = arrayOf(Identity.ALIAS)
    override fun getCertificateChain(alias: String?): Array<X509Certificate> = arrayOf(Identity.certificate())
    override fun getPrivateKey(alias: String?): PrivateKey = Identity.privateKey()
    override fun getServerAliases(keyType: String?, issuers: Array<out Principal>?): Array<String>? = null
    override fun chooseServerAlias(keyType: String?, issuers: Array<out Principal>?, socket: Socket?): String? = null
}

/**
 * Akceptuje wyłącznie certyfikat komputera o zapamiętanym odcisku.
 * Bez odcisku (tylko pierwszy krok parowania) zapamiętuje widziany odcisk – weryfikuje go potem kod SAS.
 */
class PinningTrustManager(private val expectedFingerprint: String?) : X509TrustManager {
    @Volatile
    var seenFingerprint: String? = null
        private set

    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        val leaf = chain?.firstOrNull() ?: throw CertificateException("Brak certyfikatu komputera")
        val fingerprint = sha256Hex(leaf.encoded)
        seenFingerprint = fingerprint
        if (expectedFingerprint != null && !fingerprint.equals(expectedFingerprint, ignoreCase = true)) {
            throw CertificateException("Certyfikat komputera nie zgadza się ze sparowanym")
        }
    }

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) =
        throw CertificateException("Telefon nie przyjmuje połączeń")

    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
}

object Http {
    private val JSON = "application/json; charset=utf-8".toMediaType()

    fun client(trustManager: PinningTrustManager): OkHttpClient {
        val ssl = SSLContext.getInstance("TLS").apply { init(arrayOf(ClientKeyManager()), arrayOf(trustManager), null) }
        return OkHttpClient.Builder()
            .sslSocketFactory(ssl.socketFactory, trustManager)
            // tożsamość komputera sprawdza przypięty odcisk, nie nazwa hosta (łączymy się po IP)
            .hostnameVerifier { _, _ -> true }
            .protocols(listOf(HttpProtocol.HTTP_1_1))
            .connectTimeout(5, TimeUnit.SECONDS)
            // oferta czeka na decyzję użytkownika komputera do 120 s
            .readTimeout(150, TimeUnit.SECONDS)
            .writeTimeout(60, TimeUnit.SECONDS)
            .pingInterval(25, TimeUnit.SECONDS)
            .build()
    }

    fun pinned(fingerprint: String): OkHttpClient = client(PinningTrustManager(fingerprint))

    suspend fun postJson(client: OkHttpClient, url: String, body: JSONObject): JSONObject {
        val request = Request.Builder().url(url).post(body.toString().toRequestBody(JSON)).build()
        return client.newCall(request).await().use { response ->
            if (!response.isSuccessful) throw HttpException(response.code)
            val text = withContext(Dispatchers.IO) { response.body?.string().orEmpty() }
            JSONObject(text)
        }
    }
}

/** Wykonuje żądanie; anulowanie korutyny przerywa połączenie. */
suspend fun Call.await(): Response = suspendCancellableCoroutine { continuation ->
    continuation.invokeOnCancellation { cancel() }
    enqueue(object : Callback {
        override fun onResponse(call: Call, response: Response) = continuation.resume(response)
        override fun onFailure(call: Call, e: IOException) {
            if (!continuation.isCancelled) continuation.resumeWithException(e)
        }
    })
}
