package pl.devdraw.phonebridge

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.math.BigInteger
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.spec.ECGenParameterSpec
import java.security.cert.X509Certificate
import java.util.Date
import javax.security.auth.x500.X500Principal

/**
 * Tożsamość telefonu: klucz ECDSA P-256 w Android Keystore (nieeksportowalny)
 * i samopodpisany certyfikat, który Keystore tworzy razem z kluczem.
 */
object Identity {
    const val ALIAS = "phonebridge_client"

    private fun keyStore(): KeyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }

    @Synchronized
    private fun ensure(): KeyStore {
        val ks = keyStore()
        if (!ks.containsAlias(ALIAS)) generate()
        return ks
    }

    fun certificate(): X509Certificate = ensure().getCertificate(ALIAS) as X509Certificate

    fun privateKey(): PrivateKey = ensure().getKey(ALIAS, null) as PrivateKey

    fun fingerprint(): String = sha256Hex(certificate().encoded)

    private fun generate() {
        val now = System.currentTimeMillis()
        val spec = KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_SIGN)
            .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
            // NONE jest potrzebne, bo TLS może podpisywać gotowy skrót
            .setDigests(KeyProperties.DIGEST_NONE, KeyProperties.DIGEST_SHA256, KeyProperties.DIGEST_SHA384, KeyProperties.DIGEST_SHA512)
            .setCertificateSubject(X500Principal("CN=PhoneBridge Android"))
            .setCertificateSerialNumber(BigInteger(63, SecureRandom()))
            .setCertificateNotBefore(Date(now - 86_400_000L))
            .setCertificateNotAfter(Date(now + 30L * 365 * 86_400_000L))
            .build()
        KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore").run {
            initialize(spec)
            generateKeyPair()
        }
    }
}
