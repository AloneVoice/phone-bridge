package pl.devdraw.phonebridge

import android.app.Activity
import android.content.ClipData
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.widget.Toast

/**
 * Cel systemowego „Udostępnij”. Nie pokazuje interfejsu: przekazuje pliki (z uprawnieniem do odczytu)
 * usłudze pierwszoplanowej i od razu się zamyka.
 */
class ShareActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val uris = when (intent.action) {
            Intent.ACTION_SEND -> listOfNotNull(intent.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java))
            Intent.ACTION_SEND_MULTIPLE -> intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM, Uri::class.java).orEmpty()
            else -> emptyList()
        }
        val server = Prefs(this).server

        when {
            server == null -> {
                Toast.makeText(this, "Najpierw sparuj telefon z komputerem", Toast.LENGTH_LONG).show()
                startActivity(Intent(this, MainActivity::class.java))
            }
            uris.isEmpty() -> Toast.makeText(this, "PhoneBridge przesyła tylko pliki", Toast.LENGTH_LONG).show()
            else -> {
                val clip = ClipData.newRawUri("pliki", uris.first())
                uris.drop(1).forEach { clip.addItem(ClipData.Item(it)) }
                BridgeService.send(this, BridgeService.ACTION_UPLOAD) {
                    clipData = clip
                    addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                }
                Toast.makeText(this, "Wysyłanie do ${server.name}…", Toast.LENGTH_SHORT).show()
            }
        }
        finish()
    }
}
