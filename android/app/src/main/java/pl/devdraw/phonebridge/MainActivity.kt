package pl.devdraw.phonebridge

import android.Manifest
import android.animation.ObjectAnimator
import android.animation.ValueAnimator
import android.annotation.SuppressLint
import android.content.Intent
import android.content.res.ColorStateList
import android.net.Uri
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.view.animation.AccelerateDecelerateInterpolator
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.repeatOnLifecycle
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Job
import kotlinx.coroutines.async
import kotlinx.coroutines.launch
import pl.devdraw.phonebridge.databinding.ActivityMainBinding
import pl.devdraw.phonebridge.databinding.DialogCodeBinding
import pl.devdraw.phonebridge.databinding.ItemServerBinding

class MainActivity : AppCompatActivity() {
    private lateinit var binding: ActivityMainBinding
    private lateinit var prefs: Prefs
    private var scanJob: Job? = null
    private var askedForNotifications = false
    private var pulse: ObjectAnimator? = null

    private val notificationPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { render() }

    private val mediaPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        prefs.offerScreenshots = granted
        if (!granted) Toast.makeText(this, "Bez dostępu do zdjęć PhoneBridge nie zauważy nowych zrzutów ekranu", Toast.LENGTH_LONG).show()
        BridgeService.send(this, BridgeService.ACTION_START)
        render()
    }

    private val pickFolder = registerForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        if (uri != null) {
            contentResolver.takePersistableUriPermission(uri,
                Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
            prefs.destination = Destination.Tree(uri)
            render()
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)
        prefs = Prefs(this)

        binding.refreshButton.setOnClickListener { scan() }
        binding.connectIpButton.setOnClickListener { pairManually() }
        binding.notificationButton.setOnClickListener { notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS) }
        binding.batteryButton.setOnClickListener { requestBatteryExemption() }
        binding.forgetButton.setOnClickListener { forget() }
        binding.folderRow.setOnClickListener { chooseFolder() }
        binding.keepConnectedSwitch.setOnCheckedChangeListener { view, checked ->
            if (!view.isPressed) return@setOnCheckedChangeListener
            prefs.keepConnected = checked
            BridgeService.send(this, if (checked) BridgeService.ACTION_START else BridgeService.ACTION_STOP)
        }
        binding.autoAcceptSwitch.setOnCheckedChangeListener { view, checked ->
            if (view.isPressed) prefs.autoAccept = checked
        }
        binding.screenshotSwitch.setOnCheckedChangeListener { view, checked ->
            if (!view.isPressed) return@setOnCheckedChangeListener
            if (checked && !ScreenshotWatcher.hasPermission(this)) {
                mediaPermission.launch(Manifest.permission.READ_MEDIA_IMAGES)
                return@setOnCheckedChangeListener
            }
            prefs.offerScreenshots = checked
            BridgeService.send(this, BridgeService.ACTION_START)
        }

        lifecycleScope.launch {
            repeatOnLifecycle(Lifecycle.State.STARTED) {
                BridgeService.status.collect { renderStatus(it) }
            }
        }
    }

    override fun onStart() {
        super.onStart()
        render()
        if (prefs.server != null && prefs.keepConnected) BridgeService.send(this, BridgeService.ACTION_START)
        if (prefs.server == null) scan()
        if (!Notifications.canNotify(this) && !askedForNotifications) {
            askedForNotifications = true
            notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
    }

    override fun onStop() {
        super.onStop()
        pulse?.cancel()
        pulse = null
    }

    private fun render() {
        val paired = prefs.server != null
        binding.pairedSection.visibility = if (paired) View.VISIBLE else View.GONE
        binding.forgetButton.visibility = if (paired) View.VISIBLE else View.GONE
        binding.unpairedSection.visibility = if (paired) View.GONE else View.VISIBLE
        binding.keepConnectedSwitch.isChecked = prefs.keepConnected
        binding.autoAcceptSwitch.isChecked = prefs.autoAccept
        // uprawnienie mogło zostać cofnięte w ustawieniach systemu
        binding.screenshotSwitch.isChecked = prefs.offerScreenshots && ScreenshotWatcher.hasPermission(this)

        // folder, do którego straciliśmy dostęp, zastępujemy Pobranymi
        if (!DestinationWriter.isUsable(this, prefs.destination)) prefs.destination = Destination.Downloads
        binding.folderName.text = DestinationWriter.displayName(this, prefs.destination)

        binding.notificationWarning.visibility = if (Notifications.canNotify(this)) View.GONE else View.VISIBLE
        val power = getSystemService(PowerManager::class.java)
        binding.batteryButton.visibility = if (power.isIgnoringBatteryOptimizations(packageName)) View.GONE else View.VISIBLE
        renderStatus(BridgeService.status.value)
    }

    private fun renderStatus(status: ConnectionStatus) {
        val server = prefs.server
        val connected = server != null && status is ConnectionStatus.Connected
        binding.statusText.text = when {
            server == null -> "Nie sparowano"
            status is ConnectionStatus.Connected -> "Połączono"
            status is ConnectionStatus.Searching -> "Łączenie…"
            status is ConnectionStatus.Offline -> "Komputer niedostępny"
            !prefs.keepConnected -> "Połączenie w tle wyłączone"
            else -> "Uruchamianie…"
        }
        binding.heroSubtitle.text = when {
            server == null -> "Wybierz swój komputer z listy poniżej"
            status is ConnectionStatus.Offline -> "${server.name} · ponawiam połączenie"
            else -> server.name
        }
        binding.statusDot.setBackgroundResource(if (connected) R.drawable.dot_connected else R.drawable.dot_disconnected)
        binding.heroLine.backgroundTintList = ColorStateList.valueOf(
            ContextCompat.getColor(this, if (connected) R.color.brand_coral else R.color.hero_line_idle))
        setPulse(connected)
    }

    /** Kropka „płynąca” od telefonu do komputera, gdy połączenie jest aktywne. */
    private fun setPulse(active: Boolean) {
        val dot = binding.heroPulse
        if (!active) {
            pulse?.cancel()
            pulse = null
            dot.visibility = View.INVISIBLE
            return
        }
        if (pulse != null) return
        binding.heroTrack.post {
            val distance = (binding.heroTrack.width - dot.width).toFloat()
            if (distance <= 0) return@post
            dot.visibility = View.VISIBLE
            pulse = ObjectAnimator.ofFloat(dot, View.TRANSLATION_X, 0f, distance).apply {
                duration = 1600
                repeatCount = ValueAnimator.INFINITE
                repeatMode = ValueAnimator.REVERSE
                interpolator = AccelerateDecelerateInterpolator()
                start()
            }
        }
    }

    private fun chooseFolder() {
        MaterialAlertDialogBuilder(this)
            .setTitle("Gdzie zapisywać pliki?")
            .setItems(arrayOf("Pobrane/PhoneBridge", "Wybierz folder…")) { _, which ->
                if (which == 0) {
                    prefs.destination = Destination.Downloads
                    render()
                } else {
                    pickFolder.launch((prefs.destination as? Destination.Tree)?.uri)
                }
            }
            .show()
    }

    // ---------- wyszukiwanie i parowanie ----------

    private fun scan() {
        if (scanJob?.isActive == true) return
        scanJob = lifecycleScope.launch {
            binding.searchProgress.visibility = View.VISIBLE
            binding.serverList.removeAllViews()
            binding.emptyText.visibility = View.GONE
            val found = Discovery.search(this@MainActivity, 2500)
            binding.searchProgress.visibility = View.GONE
            binding.emptyText.visibility = if (found.isEmpty()) View.VISIBLE else View.GONE
            for (server in found) {
                val item = ItemServerBinding.inflate(layoutInflater, binding.serverList, false)
                item.serverName.text = server.name
                item.serverAddress.text = server.host
                item.root.setOnClickListener { pair(server.host, server.port, server.fingerprint) }
                binding.serverList.addView(item.root)
            }
        }
    }

    private fun pairManually() {
        val input = binding.ipInput.text.toString().trim()
        val host = input.substringBefore(':')
        val port = input.substringAfter(':', "").toIntOrNull() ?: Protocol.DEFAULT_PORT
        if (host.isEmpty() || port !in 1..65535) {
            binding.ipInput.error = "Wpisz adres IP komputera, np. 192.168.1.10"
            return
        }
        pair(host, port, null)
    }

    /** Kafelki cyfr – ten sam wygląd co w oknie parowania na komputerze. */
    private fun fillCodeTiles(container: LinearLayout, code: String) {
        fun dp(value: Int) = TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, value.toFloat(), resources.displayMetrics).toInt()
        container.removeAllViews()
        code.forEachIndexed { index, digit ->
            val tile = TextView(this).apply {
                text = digit.toString()
                gravity = Gravity.CENTER
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 28f)
                typeface = android.graphics.Typeface.create("sans-serif-medium", android.graphics.Typeface.BOLD)
                setTextColor(ContextCompat.getColor(context, R.color.on_coral_soft))
                setBackgroundResource(R.drawable.bg_tile_soft)
            }
            container.addView(tile, LinearLayout.LayoutParams(dp(40), dp(54)).apply {
                marginStart = if (index == 3) dp(14) else if (index > 0) dp(6) else 0
            })
        }
    }

    @SuppressLint("SetTextI18n")
    private fun pair(host: String, port: Int, expectedFingerprint: String?) {
        lifecycleScope.launch {
            val connecting = MaterialAlertDialogBuilder(this@MainActivity)
                .setMessage("Łączenie z $host…").setCancelable(false).show()
            val session = PairingSession(this@MainActivity, host, port, expectedFingerprint)
            try {
                session.start()
                connecting.dismiss()

                val view = DialogCodeBinding.inflate(layoutInflater)
                view.codeMessage.text = "Na komputerze „${session.serverName}” pojawiło się okno z kodem. Sprawdź, czy jest taki sam:"
                fillCodeTiles(view.codeTiles, session.code)
                val userConfirmed = CompletableDeferred<Boolean>()
                val dialog = MaterialAlertDialogBuilder(this@MainActivity)
                    .setTitle("Porównaj kod")
                    .setView(view.root)
                    .setCancelable(false)
                    .setPositiveButton("Kody się zgadzają") { _, _ -> userConfirmed.complete(true) }
                    .setNegativeButton("Anuluj") { _, _ -> userConfirmed.complete(false) }
                    .show()

                // runCatching: błąd żądania nie może anulować całej korutyny przed decyzją użytkownika
                val reveal = async { runCatching { session.reveal() } }
                // komputer odrzucił (albo błąd), zanim użytkownik kliknął – zamykamy okno kodu
                val watcher = launch {
                    if (reveal.await().getOrNull() != true && !userConfirmed.isCompleted) {
                        dialog.dismiss()
                        userConfirmed.complete(false)
                    }
                }

                val confirmed = userConfirmed.await()
                watcher.cancel()
                if (!confirmed) {
                    if (!reveal.isCompleted) {
                        reveal.cancel() // przerwane żądanie zamyka okno na komputerze
                        return@launch
                    }
                    val result = reveal.await()
                    result.exceptionOrNull()?.let { throw it }
                    if (result.getOrNull() == false) showError("Parowanie zostało odrzucone na komputerze.")
                    return@launch
                }

                val waiting = MaterialAlertDialogBuilder(this@MainActivity)
                    .setMessage("Potwierdź parowanie na komputerze „${session.serverName}”…").setCancelable(false).show()
                val accepted = try { reveal.await().getOrThrow() } finally { waiting.dismiss() }
                if (!accepted) {
                    showError("Parowanie zostało odrzucone na komputerze.")
                    return@launch
                }

                val server = session.confirm()
                prefs.server = server
                prefs.keepConnected = true
                ShareTargets.publish(this@MainActivity, server)
                BridgeService.send(this@MainActivity, BridgeService.ACTION_START)
                render()
                Toast.makeText(this@MainActivity, "Sparowano z ${server.name}", Toast.LENGTH_LONG).show()
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                showError(describePairingError(e))
            } finally {
                connecting.dismiss()
            }
        }
    }

    private fun describePairingError(e: Exception): String = when (e) {
        is javax.net.ssl.SSLException -> "Nie udało się nawiązać bezpiecznego połączenia (${e.message})."
        is java.net.ConnectException, is java.net.SocketTimeoutException ->
            "Komputer nie odpowiada. Sprawdź, czy PhoneBridge działa na komputerze, sieć jest oznaczona jako prywatna, a zapora zezwala na połączenie."
        else -> e.message ?: e.javaClass.simpleName
    }

    private fun showError(message: String) {
        MaterialAlertDialogBuilder(this).setTitle("Nie sparowano").setMessage(message).setPositiveButton("OK", null).show()
    }

    private fun forget() {
        val server = prefs.server ?: return
        MaterialAlertDialogBuilder(this)
            .setMessage("Zapomnieć komputer ${server.name}? Aby znowu przesyłać pliki, trzeba będzie sparować urządzenia ponownie.")
            .setPositiveButton("Zapomnij") { _, _ ->
                prefs.server = null
                ShareTargets.remove(this)
                BridgeService.send(this, BridgeService.ACTION_STOP)
                render()
                scan()
            }
            .setNegativeButton("Anuluj", null)
            .show()
    }

    @SuppressLint("BatteryLife")
    private fun requestBatteryExemption() {
        val intent = Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:$packageName"))
        runCatching { startActivity(intent) }
            .onFailure { startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)) }
    }
}
