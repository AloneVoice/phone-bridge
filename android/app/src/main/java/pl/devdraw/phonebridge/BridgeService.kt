package pl.devdraw.phonebridge

import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.Network
import android.net.Uri
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.BufferedSink
import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.security.MessageDigest
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

sealed interface ConnectionStatus {
    data object NotPaired : ConnectionStatus
    data object Stopped : ConnectionStatus
    data object Searching : ConnectionStatus
    data class Connected(val serverName: String) : ConnectionStatus
    data class Offline(val serverName: String) : ConnectionStatus
}

/**
 * Usługa pierwszoplanowa: utrzymuje kanał WebSocket z komputerem (oferty komputer → telefon)
 * i wykonuje transfery w obu kierunkach.
 */
class BridgeService : Service() {

    companion object {
        const val ACTION_START = "pl.devdraw.phonebridge.START"
        const val ACTION_STOP = "pl.devdraw.phonebridge.STOP"
        const val ACTION_ACCEPT = "pl.devdraw.phonebridge.ACCEPT"
        const val ACTION_REJECT = "pl.devdraw.phonebridge.REJECT"
        const val ACTION_UPLOAD = "pl.devdraw.phonebridge.UPLOAD"
        const val ACTION_CANCEL = "pl.devdraw.phonebridge.CANCEL"
        const val ACTION_RECONNECT = "pl.devdraw.phonebridge.RECONNECT"
        const val EXTRA_ID = "id"
        const val EXTRA_DESTINATION = "destination"
        const val EXTRA_DISMISS = "dismissNotification"

        private const val BUFFER_SIZE = 256 * 1024
        private val OCTET_STREAM = "application/octet-stream".toMediaType()

        private val statusFlow = MutableStateFlow<ConnectionStatus>(ConnectionStatus.Stopped)
        val status: StateFlow<ConnectionStatus> = statusFlow

        /** Oferty czekające na decyzję użytkownika (czyta je ReceiveActivity). */
        val pendingOffers = ConcurrentHashMap<String, Offer>()

        fun send(context: Context, action: String, configure: Intent.() -> Unit = {}) {
            val intent = Intent(context, BridgeService::class.java).setAction(action).apply(configure)
            ContextCompat.startForegroundService(context, intent)
        }
    }

    private class ActiveTransfer(val job: Job) {
        @Volatile
        var remoteId: String? = null
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val transfers = ConcurrentHashMap<String, ActiveTransfer>()
    private val reconnectNow = Channel<Unit>(Channel.CONFLATED)
    private lateinit var prefs: Prefs
    private lateinit var notifications: Notifications
    private var connectionJob: Job? = null
    private var wakeLock: PowerManager.WakeLock? = null
    private var clientCache: Pair<String, OkHttpClient>? = null
    private val screenshots by lazy { ScreenshotWatcher(this, ::onScreenshot) }

    @Volatile
    private var webSocket: WebSocket? = null

    @Volatile
    private var activeHost: String? = null

    private val networkCallback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) {
            reconnectNow.trySend(Unit)
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        prefs = Prefs(this)
        notifications = Notifications(this)
        startInForeground()
        getSystemService(ConnectivityManager::class.java).registerDefaultNetworkCallback(networkCallback)
        wakeLock = getSystemService(PowerManager::class.java)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "PhoneBridge:transfer")
            .apply { setReferenceCounted(true) }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        // przy każdym startForegroundService trzeba ponownie wywołać startForeground
        startInForeground()
        val id = intent?.getStringExtra(EXTRA_ID)
        when (intent?.action) {
            ACTION_STOP -> {
                prefs.keepConnected = false
                stopConnection()
            }
            ACTION_ACCEPT -> if (id != null) accept(id, Destination.decode(intent.getStringExtra(EXTRA_DESTINATION)))
            ACTION_REJECT -> if (id != null) reject(id)
            ACTION_CANCEL -> if (id != null) cancelTransfer(id)
            ACTION_UPLOAD -> {
                intent.getIntExtra(EXTRA_DISMISS, 0).takeIf { it != 0 }?.let { notifications.cancel(it) }
                upload(intent.clipData?.let { clip -> (0 until clip.itemCount).mapNotNull { clip.getItemAt(it).uri } }.orEmpty())
            }
            ACTION_RECONNECT -> {
                stopConnection()
                reconnectNow.trySend(Unit)
            }
        }
        ensureConnection()
        updateScreenshotWatcher()
        prefs.server?.let { ShareTargets.publish(this, it) }
        stopIfIdle()
        return START_STICKY
    }

    private fun updateScreenshotWatcher() {
        if (prefs.server != null && prefs.offerScreenshots && ScreenshotWatcher.hasPermission(this)) screenshots.register()
        else screenshots.unregister()
    }

    private fun onScreenshot(uri: Uri, imageId: Long) {
        val server = prefs.server ?: return
        scope.launch {
            val preview = runCatching { contentResolver.loadThumbnail(uri, android.util.Size(720, 720), null) }.getOrNull()
            notifications.showScreenshot(imageId, uri, preview, server.name)
        }
    }

    override fun onDestroy() {
        getSystemService(ConnectivityManager::class.java).unregisterNetworkCallback(networkCallback)
        screenshots.unregister()
        webSocket?.cancel()
        scope.cancel()
        if (prefs.server == null) statusFlow.value = ConnectionStatus.NotPaired
        else statusFlow.value = ConnectionStatus.Stopped
        super.onDestroy()
    }

    private fun startInForeground() {
        val type = if (Build.VERSION.SDK_INT >= 34) ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE
        else ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC
        ServiceCompat.startForeground(this, Notifications.STATUS_ID, notifications.status(statusText()), type)
    }

    private fun statusText(): String = when (val s = statusFlow.value) {
        is ConnectionStatus.Connected -> "Połączono z ${s.serverName}"
        is ConnectionStatus.Offline -> "Szukam komputera ${s.serverName}…"
        ConnectionStatus.Searching -> "Łączenie z komputerem…"
        else -> if (transfers.isEmpty()) "Gotowy" else "Przesyłanie plików…"
    }

    private fun setStatus(status: ConnectionStatus) {
        statusFlow.value = status
        notifications.updateStatus(statusText())
    }

    private fun stopIfIdle() {
        if (prefs.server == null) statusFlow.value = ConnectionStatus.NotPaired
        val needed = transfers.isNotEmpty() || (prefs.server != null && prefs.keepConnected)
        if (!needed) stopSelf()
    }

    private fun httpClient(server: PairedServer): OkHttpClient {
        clientCache?.let { (fp, client) -> if (fp == server.fingerprint) return client }
        return Http.pinned(server.fingerprint).also { clientCache = server.fingerprint to it }
    }

    // ---------- kanał sterujący ----------

    private fun ensureConnection() {
        if (prefs.server == null || !prefs.keepConnected || connectionJob?.isActive == true) return
        connectionJob = scope.launch { connectionLoop() }
    }

    private fun stopConnection() {
        connectionJob?.cancel()
        connectionJob = null
        webSocket?.cancel()
        statusFlow.value = if (prefs.server == null) ConnectionStatus.NotPaired else ConnectionStatus.Stopped
    }

    private suspend fun connectionLoop() {
        var delayMs = 2_000L
        while (currentCoroutineContext().isActive) {
            val server = prefs.server ?: break
            setStatus(ConnectionStatus.Searching)
            if (runChannel(server)) delayMs = 2_000L
            setStatus(ConnectionStatus.Offline(server.name))
            withTimeoutOrNull(delayMs) { reconnectNow.receive() }
            delayMs = (delayMs * 2).coerceAtMost(60_000L)
        }
    }

    /** Szuka komputera: najpierw zapamiętane adresy, potem broadcast w sieci lokalnej. */
    private suspend fun findHost(server: PairedServer): String? {
        val candidates = (listOfNotNull(activeHost) + server.hosts).distinct()
        for (host in candidates) {
            if (Discovery.isReachable(host, server.port)) return host
        }
        val found = Discovery.search(this).firstOrNull { it.fingerprint == server.fingerprint } ?: return null
        prefs.rememberHost(found.host, found.port)
        return found.host
    }

    /** Otwiera WebSocket i czeka na jego zamknięcie. Zwraca true, jeśli połączenie zostało nawiązane. */
    private suspend fun runChannel(server: PairedServer): Boolean {
        val host = findHost(server) ?: return false
        val current = prefs.server ?: return false
        val opened = CompletableDeferred<Boolean>()
        val closed = CompletableDeferred<Unit>()
        val request = Request.Builder().url("${Protocol.baseUrl(host, current.port)}/v1/ws").build()
        val socket = httpClient(current).newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                webSocket.send(JSONObject().put("type", "hello").put("deviceName", deviceName(this@BridgeService))
                    .put("protocol", Protocol.VERSION).toString())
                opened.complete(true)
            }

            override fun onMessage(webSocket: WebSocket, text: String) = handleMessage(text, current)

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                webSocket.close(1000, null)
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                closed.complete(Unit)
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                if (response?.code == 403) {
                    // komputer usunął parowanie
                    prefs.server = null
                }
                opened.complete(false)
                closed.complete(Unit)
            }
        })
        try {
            if (withTimeoutOrNull(15_000) { opened.await() } != true) return false
            webSocket = socket
            activeHost = host
            prefs.rememberHost(host, current.port)
            setStatus(ConnectionStatus.Connected(current.name))
            closed.await()
            return true
        } finally {
            socket.cancel()
            if (webSocket === socket) webSocket = null
        }
    }

    private fun handleMessage(text: String, server: PairedServer) {
        val json = runCatching { JSONObject(text) }.getOrNull() ?: return
        when (json.optString("type")) {
            "hello" -> {
                val name = json.optString("serverName").filter { !it.isISOControl() }.take(64)
                if (name.isNotEmpty() && name != server.name) prefs.server = prefs.server?.copy(name = name)
            }
            "offer" -> {
                val offer = Protocol.parseOffer(json) ?: return
                if (transfers.containsKey(offer.id) || pendingOffers.containsKey(offer.id)) return
                val destination = prefs.destination
                // autoakceptacja tylko do folderu, do którego nadal mamy dostęp – inaczej zwykłe pytanie
                if (prefs.autoAccept && DestinationWriter.isUsable(this, destination)) {
                    sendControl("accept", offer.id)
                    launchTransfer(offer.id) { download(offer, destination) }
                } else if (pendingOffers.putIfAbsent(offer.id, offer) == null) {
                    showOffer(offer, server)
                }
            }
            "cancel" -> {
                val id = json.optString("id")
                if (pendingOffers.remove(id) != null) notifications.cancelOffer(id)
                transfers.entries.firstOrNull { it.key == id || it.value.remoteId == id }
                    ?.value?.job?.cancel(CancellationException("Anulowano na komputerze"))
            }
        }
    }

    private fun showOffer(offer: Offer, server: PairedServer) {
        val destination = prefs.destination.takeIf { DestinationWriter.isUsable(this, it) } ?: Destination.Downloads
        notifications.showOffer(offer, server.name, destination, DestinationWriter.displayName(this, destination))
        if (PhoneBridgeApp.isInForeground) {
            startActivity(ReceiveActivity.intent(this, offer.id).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        }
    }

    private fun sendControl(type: String, id: String) {
        webSocket?.send(JSONObject().put("type", type).put("id", id).toString())
    }

    private fun accept(id: String, destination: Destination) {
        val offer = pendingOffers.remove(id)
        notifications.cancelOffer(id)
        if (offer == null) {
            notifications.finished(id, "Nie można odebrać plików", "Oferta wygasła albo została anulowana na komputerze.")
            return
        }
        prefs.destination = destination
        sendControl("accept", id)
        launchTransfer(id) { download(offer, destination) }
    }

    private fun reject(id: String) {
        pendingOffers.remove(id)
        notifications.cancelOffer(id)
        sendControl("reject", id)
    }

    private fun cancelTransfer(id: String) {
        val transfer = transfers[id] ?: return
        transfer.job.cancel(CancellationException("Anulowano"))
    }

    // ---------- transfery ----------

    private fun launchTransfer(id: String, block: suspend (ActiveTransfer) -> Unit) {
        lateinit var transfer: ActiveTransfer
        val job = scope.launch(start = kotlinx.coroutines.CoroutineStart.LAZY) {
            wakeLock?.acquire(6 * 60 * 60 * 1000L)
            try {
                block(transfer)
            } catch (e: CancellationException) {
                notifications.finished(id, "Przerwano przesyłanie", e.message ?: "Anulowano")
            } catch (e: Exception) {
                notifications.finished(id, "Błąd przesyłania", describe(e))
            } finally {
                transfers.remove(id)
                if (wakeLock?.isHeld == true) wakeLock?.release()
                withContext(Dispatchers.Main) { stopIfIdle() }
            }
        }
        transfer = ActiveTransfer(job)
        transfers[id] = transfer
        job.start()
    }

    private fun describe(e: Exception): String = when (e) {
        is HttpException -> if (e.code == 403) "Komputer nie rozpoznaje tego telefonu – sparuj ponownie." else e.message.orEmpty()
        is javax.net.ssl.SSLException -> "Błąd bezpiecznego połączenia z komputerem."
        is java.net.ConnectException, is java.net.SocketTimeoutException -> "Komputer przestał odpowiadać."
        else -> e.message ?: e.javaClass.simpleName
    }

    private suspend fun download(offer: Offer, destination: Destination) {
        val server = prefs.server ?: throw IOException("Telefon nie jest sparowany")
        val host = activeHost ?: findHost(server) ?: throw IOException("Nie znaleziono komputera ${server.name}")
        val base = Protocol.baseUrl(host, server.port)
        val client = httpClient(server)
        val writer = DestinationWriter(this, destination)
        val progress = Progress(offer.totalBytes) { done ->
            notifications.progress(offer.id, "Odbieranie z ${server.name}", "${Format.bytes(done)} z ${Format.bytes(offer.totalBytes)}", done, offer.totalBytes)
        }
        progress.report(force = true)
        var failed = 0

        for ((index, file) in offer.files.withIndex()) {
            currentCoroutineContext().ensureActive()
            val target = writer.create(FileNames.segments(file.name))
            try {
                val hash = client.newCall(Request.Builder().url("$base/v1/outgoing/${offer.id}/$index").build()).await().use { response ->
                    if (!response.isSuccessful) throw HttpException(response.code)
                    val body = response.body ?: throw IOException("Pusta odpowiedź")
                    target.open().use { output -> copyWithHash(body.byteStream(), output, file.size, progress) }
                }
                val ok = Http.postJson(client, "$base/v1/outgoing/${offer.id}/$index/done", JSONObject().put("sha256", hash)).optBoolean("ok")
                if (ok) target.commit() else {
                    target.delete()
                    failed++
                }
            } catch (e: Throwable) {
                withContext(NonCancellable) { target.delete() }
                throw e
            }
        }

        val where = DestinationWriter.displayName(this, destination)
        if (failed == 0) notifications.finished(offer.id, "Odebrano ${Format.files(offer.files.size)}", "Zapisano w: $where")
        else notifications.finished(offer.id, "Odebrano z błędami", "$failed z ${offer.files.size} plików nie przeszło weryfikacji i zostało usuniętych.")
    }

    private fun upload(uris: List<Uri>) {
        if (uris.isEmpty()) return
        val id = UUID.randomUUID().toString()
        launchTransfer(id) { transfer -> uploadFiles(id, transfer, uris) }
    }

    private suspend fun uploadFiles(id: String, transfer: ActiveTransfer, uris: List<Uri>) {
        val server = prefs.server ?: throw IOException("Telefon nie jest sparowany")
        val title = "Wysyłanie do ${server.name}"
        notifications.progress(id, title, "Przygotowanie…", 0, 0, indeterminate = true)
        val files = uris.map { Sources.describe(contentResolver, it) }
        val total = files.sumOf { it.size }

        val host = activeHost ?: findHost(server) ?: throw IOException("Nie znaleziono komputera ${server.name} w sieci. Czy jest włączony i w tej samej sieci Wi-Fi?")
        val base = Protocol.baseUrl(host, server.port)
        val client = httpClient(server)

        notifications.progress(id, title, "Czekam na akceptację na komputerze…", 0, 0, indeterminate = true)
        val entries = JSONArray(files.map { JSONObject().put("name", it.name).put("size", it.size) })
        val offer = Http.postJson(client, "$base/v1/incoming", JSONObject().put("files", entries))
        if (!offer.optBoolean("accepted")) {
            val reason = when (offer.optString("reason")) {
                "timeout" -> "Nikt nie odpowiedział na komputerze."
                "insufficient_space" -> "Za mało miejsca na dysku komputera."
                else -> "Odrzucono na komputerze."
            }
            notifications.finished(id, "Nie wysłano plików", reason)
            return
        }
        val remoteId = offer.getString("id")
        transfer.remoteId = remoteId

        val progress = Progress(total) { done ->
            notifications.progress(id, title, "${Format.bytes(done)} z ${Format.bytes(total)}", done, total)
        }
        var failed = 0
        try {
            for ((index, file) in files.withIndex()) {
                currentCoroutineContext().ensureActive()
                val body = HashingBody(file, progress)
                client.newCall(Request.Builder().url("$base/v1/incoming/$remoteId/$index").put(body).build()).await().use { response ->
                    if (!response.isSuccessful) throw HttpException(response.code)
                }
                val hash = body.sha256 ?: throw IOException("Nie policzono sumy kontrolnej")
                val commit = Http.postJson(client, "$base/v1/incoming/$remoteId/$index/commit", JSONObject().put("sha256", hash))
                if (!commit.optBoolean("ok")) failed++
            }
        } catch (e: CancellationException) {
            scope.launch { runCatching { Http.postJson(client, "$base/v1/incoming/$remoteId/cancel", JSONObject()) } }
            throw e
        }

        if (failed == 0) notifications.finished(id, "Wysłano ${Format.files(files.size)}", "Pliki zapisano na komputerze ${server.name}.")
        else notifications.finished(id, "Wysłano z błędami", "$failed z ${files.size} plików nie przeszło weryfikacji.")
    }

    /** Ciało PUT strumieniowane z ContentResolvera; SHA-256 liczony w trakcie wysyłania. */
    private inner class HashingBody(private val file: SourceFile, private val progress: Progress) : RequestBody() {
        @Volatile
        var sha256: String? = null

        override fun contentType() = OCTET_STREAM
        override fun contentLength() = file.size
        override fun isOneShot() = true

        override fun writeTo(sink: BufferedSink) {
            val input = contentResolver.openInputStream(file.uri) ?: throw IOException("Nie można odczytać pliku")
            input.use { sha256 = copyBlocking(it, sink.outputStream(), file.size, progress) }
        }
    }

    private suspend fun copyWithHash(input: InputStream, output: OutputStream, expected: Long, progress: Progress): String {
        val job = currentCoroutineContext()[Job]
        return copyBlocking(input, output, expected, progress) { job?.isActive != false }
    }

    private fun copyBlocking(input: InputStream, output: OutputStream, expected: Long, progress: Progress, active: () -> Boolean = { true }): String {
        val digest = MessageDigest.getInstance("SHA-256")
        val buffer = ByteArray(BUFFER_SIZE)
        var total = 0L
        while (true) {
            if (!active()) throw CancellationException("Anulowano")
            val read = input.read(buffer)
            if (read < 0) break
            total += read
            if (total > expected) throw IOException("Plik jest większy niż zadeklarowano")
            output.write(buffer, 0, read)
            digest.update(buffer, 0, read)
            progress.add(read.toLong())
        }
        if (total != expected) throw IOException("Plik zmienił się podczas przesyłania")
        output.flush()
        return Hex.encode(digest.digest())
    }

    /** Ogranicza odświeżanie powiadomienia (Android dławi częstsze aktualizacje). */
    private class Progress(private val total: Long, private val onReport: (Long) -> Unit) {
        @Volatile
        private var done = 0L
        @Volatile
        private var lastReport = 0L

        fun add(bytes: Long) {
            done += bytes
            report()
        }

        fun report(force: Boolean = false) {
            val now = System.currentTimeMillis()
            if (force || now - lastReport >= 700 || done >= total) {
                lastReport = now
                onReport(done)
            }
        }
    }
}

fun deviceName(context: Context): String =
    android.provider.Settings.Global.getString(context.contentResolver, android.provider.Settings.Global.DEVICE_NAME)
        ?.takeIf { it.isNotBlank() } ?: Build.MODEL
