package pl.devdraw.phonebridge

import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.view.View
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import pl.devdraw.phonebridge.databinding.ActivityReceiveBinding

/** Okno oferty komputer → telefon: odbiór do ostatniego folderu, do Pobranych albo do wybranego folderu. */
class ReceiveActivity : AppCompatActivity() {
    companion object {
        private const val MAX_LISTED = 8

        fun intent(context: Context, offerId: String): Intent =
            Intent(context, ReceiveActivity::class.java).putExtra(BridgeService.EXTRA_ID, offerId)
    }

    private lateinit var binding: ActivityReceiveBinding
    private lateinit var offerId: String

    private val pickFolder = registerForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        if (uri != null) {
            contentResolver.takePersistableUriPermission(uri,
                Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
            accept(Destination.Tree(uri))
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityReceiveBinding.inflate(layoutInflater)
        setContentView(binding.root)
        setFinishOnTouchOutside(false)
        show(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        show(intent)
    }

    private fun show(intent: Intent) {
        offerId = intent.getStringExtra(BridgeService.EXTRA_ID).orEmpty()
        val offer = BridgeService.pendingOffers[offerId]
        if (offer == null) {
            Toast.makeText(this, "Oferta wygasła albo została anulowana", Toast.LENGTH_LONG).show()
            finish()
            return
        }

        val serverName = Prefs(this).server?.name ?: "Komputer"
        binding.title.text = "$serverName wysyła ${Format.files(offer.files.size)}"
        binding.summary.text = "Łącznie ${Format.bytes(offer.totalBytes)}"
        val names = offer.files.take(MAX_LISTED).joinToString("\n") { "• ${it.name}  (${Format.bytes(it.size)})" }
        val more = offer.files.size - MAX_LISTED
        binding.fileList.text = if (more > 0) "$names\n… i ${Format.files(more)} więcej" else names

        val last = Prefs(this).destination.takeIf { DestinationWriter.isUsable(this, it) } ?: Destination.Downloads
        binding.acceptButton.text = "Zapisz w: ${DestinationWriter.displayName(this, last)}"
        binding.acceptButton.setOnClickListener { accept(last) }
        binding.downloadsButton.visibility = if (last == Destination.Downloads) View.GONE else View.VISIBLE
        binding.downloadsButton.setOnClickListener { accept(Destination.Downloads) }
        binding.otherFolderButton.setOnClickListener { pickFolder.launch((last as? Destination.Tree)?.uri) }
        binding.rejectButton.setOnClickListener {
            BridgeService.send(this, BridgeService.ACTION_REJECT) { putExtra(BridgeService.EXTRA_ID, offerId) }
            finish()
        }
    }

    private fun accept(destination: Destination) {
        BridgeService.send(this, BridgeService.ACTION_ACCEPT) {
            putExtra(BridgeService.EXTRA_ID, offerId)
            putExtra(BridgeService.EXTRA_DESTINATION, destination.encode())
        }
        finish()
    }
}
