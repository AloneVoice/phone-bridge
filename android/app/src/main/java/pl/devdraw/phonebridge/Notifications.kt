package pl.devdraw.phonebridge

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat

class Notifications(private val context: Context) {

    companion object {
        const val STATUS_ID = 1
        private const val CH_STATUS = "status"
        private const val CH_OFFERS = "offers"
        private const val CH_TRANSFERS = "transfers"
        private const val CH_SCREENSHOTS = "screenshots"

        fun createChannels(context: Context) {
            val manager = context.getSystemService(NotificationManager::class.java)
            manager.createNotificationChannels(listOf(
                NotificationChannel(CH_STATUS, "Połączenie z komputerem", NotificationManager.IMPORTANCE_MIN).apply {
                    description = "Stałe powiadomienie, dzięki któremu telefon może odbierać pliki z komputera"
                    setShowBadge(false)
                },
                NotificationChannel(CH_OFFERS, "Pliki do odebrania", NotificationManager.IMPORTANCE_HIGH).apply {
                    description = "Pytanie o odbiór plików wysłanych z komputera"
                },
                NotificationChannel(CH_TRANSFERS, "Postęp przesyłania", NotificationManager.IMPORTANCE_LOW).apply {
                    setShowBadge(false)
                },
                NotificationChannel(CH_SCREENSHOTS, "Zrzuty ekranu", NotificationManager.IMPORTANCE_HIGH).apply {
                    description = "Propozycja wysłania świeżego zrzutu ekranu na komputer"
                    setSound(null, null)
                    enableVibration(false)
                    setShowBadge(false)
                },
            ))
        }

        fun canNotify(context: Context): Boolean =
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
    }

    private val manager = NotificationManagerCompat.from(context)

    private fun builder(channel: String) = NotificationCompat.Builder(context, channel)
        .setColor(ContextCompat.getColor(context, R.color.brand_coral))

    private fun notify(id: Int, notification: Notification) {
        if (canNotify(context)) manager.notify(id, notification)
    }

    private fun openApp(): PendingIntent = PendingIntent.getActivity(
        context, 0, Intent(context, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)

    private fun serviceAction(action: String, id: String, requestCode: Int, destination: Destination? = null): PendingIntent {
        val intent = Intent(context, BridgeService::class.java).setAction(action)
            .putExtra(BridgeService.EXTRA_ID, id)
        destination?.let { intent.putExtra(BridgeService.EXTRA_DESTINATION, it.encode()) }
        return PendingIntent.getForegroundService(context, requestCode, intent,
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
    }

    fun status(text: String): Notification =
        builder(CH_STATUS)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle("PhoneBridge")
            .setContentText(text)
            .setOngoing(true)
            .setSilent(true)
            .setContentIntent(openApp())
            .setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
            .build()

    fun updateStatus(text: String) = notify(STATUS_ID, status(text))

    fun showOffer(offer: Offer, serverName: String, destination: Destination, destinationName: String) {
        val id = offerNotificationId(offer.id)
        val details = PendingIntent.getActivity(context, id,
            ReceiveActivity.intent(context, offer.id), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val summary = if (offer.files.size == 1) offer.files[0].name.substringAfterLast('/') else Format.files(offer.files.size)
        val notification = NotificationCompat.Builder(context, CH_OFFERS)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle("$serverName wysyła pliki")
            .setContentText("$summary · ${Format.bytes(offer.totalBytes)}")
            .setStyle(NotificationCompat.BigTextStyle().bigText(
                "$summary · ${Format.bytes(offer.totalBytes)}\nStuknij, aby wybrać miejsce zapisu."))
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_MESSAGE)
            .setContentIntent(details)
            .setAutoCancel(true)
            .setTimeoutAfter(130_000)
            .addAction(0, "Odrzuć", serviceAction(BridgeService.ACTION_REJECT, offer.id, id + 1))
            .addAction(0, "Zapisz w: $destinationName", serviceAction(BridgeService.ACTION_ACCEPT, offer.id, id + 2, destination))
            .build()
        notify(id, notification)
    }

    fun cancelOffer(offerId: String) = manager.cancel(offerNotificationId(offerId))

    fun showScreenshot(imageId: Long, uri: android.net.Uri, preview: android.graphics.Bitmap?, serverName: String) {
        val id = screenshotNotificationId(imageId)
        val send = Intent(context, BridgeService::class.java).setAction(BridgeService.ACTION_UPLOAD)
            .putExtra(BridgeService.EXTRA_DISMISS, id)
        send.clipData = android.content.ClipData.newRawUri("zrzut", uri)
        val sendIntent = PendingIntent.getForegroundService(context, id, send,
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)

        val builder = builder(CH_SCREENSHOTS)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle("Zrzut ekranu")
            .setContentText("Wysłać na $serverName?")
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setAutoCancel(true)
            .setTimeoutAfter(120_000)
            .setContentIntent(sendIntent)
            .addAction(0, "Wyślij na komputer", sendIntent)
        if (preview != null) {
            builder.setLargeIcon(preview)
                .setStyle(NotificationCompat.BigPictureStyle().bigPicture(preview).bigLargeIcon(null as android.graphics.Bitmap?))
        }
        notify(id, builder.build())
    }

    fun cancel(notificationId: Int) = manager.cancel(notificationId)

    private fun screenshotNotificationId(imageId: Long) = ("shot:$imageId").hashCode() and 0x3FFFFFFF or 0x20000000

    fun progress(transferId: String, title: String, text: String, done: Long, total: Long, indeterminate: Boolean = false) {
        val percent = if (total > 0) (done * 1000 / total).toInt() else 0
        val notification = NotificationCompat.Builder(context, CH_TRANSFERS)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(title)
            .setContentText(text)
            .setOngoing(true)
            .setSilent(true)
            .setOnlyAlertOnce(true)
            .setProgress(1000, percent, indeterminate)
            .addAction(0, "Anuluj", serviceAction(BridgeService.ACTION_CANCEL, transferId, transferNotificationId(transferId) + 1))
            .build()
        notify(transferNotificationId(transferId), notification)
    }

    fun finished(transferId: String, title: String, text: String) {
        val notification = NotificationCompat.Builder(context, CH_TRANSFERS)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(title)
            .setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(text))
            .setAutoCancel(true)
            .setContentIntent(openApp())
            .build()
        notify(transferNotificationId(transferId), notification)
    }

    private fun offerNotificationId(id: String) = ("offer:$id").hashCode() and 0x3FFFFFFF
    private fun transferNotificationId(id: String) = ("transfer:$id").hashCode() and 0x3FFFFFFF or 0x40000000
}
