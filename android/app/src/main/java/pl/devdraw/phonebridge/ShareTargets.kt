package pl.devdraw.phonebridge

import android.content.Context
import android.content.Intent
import androidx.core.content.pm.ShortcutInfoCompat
import androidx.core.content.pm.ShortcutManagerCompat
import androidx.core.graphics.drawable.IconCompat

/**
 * Sparowany komputer jako cel „Direct Share” – pojawia się w górnym rzędzie systemowego „Udostępnij”
 * (oraz w menu po przytrzymaniu ikony aplikacji).
 */
object ShareTargets {
    private const val ID = "pc"
    private const val CATEGORY = "pl.devdraw.phonebridge.category.SHARE_TARGET"

    fun publish(context: Context, server: PairedServer) {
        val shortcut = ShortcutInfoCompat.Builder(context, ID)
            .setShortLabel(server.name)
            .setLongLabel("Wyślij na ${server.name}")
            .setIcon(IconCompat.createWithResource(context, R.mipmap.ic_shortcut_pc))
            .setIntent(Intent(context, MainActivity::class.java).setAction(Intent.ACTION_VIEW))
            .setCategories(setOf(CATEGORY))
            .setLongLived(true)
            .build()
        runCatching { ShortcutManagerCompat.pushDynamicShortcut(context, shortcut) }
    }

    fun remove(context: Context) {
        runCatching { ShortcutManagerCompat.removeLongLivedShortcuts(context, listOf(ID)) }
    }
}
