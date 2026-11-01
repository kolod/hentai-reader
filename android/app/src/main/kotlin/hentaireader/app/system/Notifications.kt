package hentaireader.app.system

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import androidx.core.app.NotificationCompat
import hentaireader.app.MainActivity
import hentaireader.app.R
import hentaireader.app.downloads.DownloadService
import hentaireader.app.ui.common.Strings
import hentaireader.host.data.DownloadRow
import hentaireader.host.data.DownloadState

/**
 * The download notification (contracts/android-ui.md "Download notification"): one low-importance
 * channel; an ongoing notification with the current gallery, "12 / 40", the queue and Pause /
 * Resume; a tap opens Downloads.
 */
object Notifications {
    const val CHANNEL = "downloads"
    const val ONGOING_ID = 1
    const val EXTRA_OPEN = "open"
    const val OPEN_DOWNLOADS = "downloads"

    fun ensureChannel(context: Context) {
        val manager = context.getSystemService(NotificationManager::class.java)
        if (manager.getNotificationChannel(CHANNEL) == null) {
            manager.createNotificationChannel(NotificationChannel(CHANNEL, context.getString(R.string.Notify_Channel_Downloads), NotificationManager.IMPORTANCE_LOW))
        }
    }

    /** The ongoing notification for the current queue. */
    fun ongoing(context: Context, rows: List<DownloadRow>): android.app.Notification {
        ensureChannel(context)
        val strings = Strings(context)
        val current = rows.firstOrNull { it.state == DownloadState.DOWNLOADING || it.state == DownloadState.ASSEMBLING }
            ?: rows.firstOrNull { it.state == DownloadState.QUEUED }
        val queued = rows.count { it.state == DownloadState.QUEUED && it !== current }
        val paused = current == null && rows.any { it.state == DownloadState.PAUSED }
        val builder = NotificationCompat.Builder(context, CHANNEL)
            .setSmallIcon(android.R.drawable.stat_sys_download)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setSilent(true)
            .setContentIntent(openDownloads(context))
        if (current != null) {
            var text = context.getString(R.string.Notify_Progress, strings.number(current.pagesDone), strings.number(current.pageCount))
            if (queued > 0) text += " · " + context.resources.getQuantityString(R.plurals.Notify_MoreQueued, queued, strings.number(queued))
            current.errorKind?.let { reason -> reasonText(context, reason)?.let { text += " · $it" } }
            builder.setContentTitle(current.title)
                .setContentText(text)
                .setProgress(maxOf(1, current.pageCount), current.pagesDone, current.pageCount == 0)
                .addAction(0, context.getString(R.string.Notify_Pause), action(context, DownloadService.ACTION_PAUSE))
        } else {
            builder.setContentTitle(context.getString(R.string.Notify_Channel_Downloads))
            if (paused) builder.addAction(0, context.getString(R.string.Notify_Resume), action(context, DownloadService.ACTION_RESUME))
        }
        return builder.build()
    }

    /** A finished or stopped download (dismissible). */
    fun finished(context: Context, row: DownloadRow) {
        ensureChannel(context)
        val text = if (row.state == DownloadState.DONE) R.string.Notify_Done else R.string.Notify_Stopped
        val n = NotificationCompat.Builder(context, CHANNEL)
            .setSmallIcon(if (row.state == DownloadState.DONE) android.R.drawable.stat_sys_download_done else android.R.drawable.stat_notify_error)
            .setContentTitle(row.title)
            .setContentText(context.getString(text))
            .setAutoCancel(true)
            .setContentIntent(openDownloads(context))
            .build()
        runCatching { context.getSystemService(NotificationManager::class.java).notify((row.id % Int.MAX_VALUE).toInt() + 100, n) }
    }

    fun reasonText(context: Context, reason: String): String? = when (reason) {
        "waiting_wifi" -> context.getString(R.string.Downloads_WaitingWifi)
        "stopped_by_system" -> context.getString(R.string.Downloads_StoppedBySystem)
        else -> null
    }

    private fun openDownloads(context: Context) = PendingIntent.getActivity(
        context, 0,
        Intent(context, MainActivity::class.java).putExtra(EXTRA_OPEN, OPEN_DOWNLOADS).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )

    private fun action(context: Context, action: String) = PendingIntent.getService(
        context, action.hashCode(), Intent(context, DownloadService::class.java).setAction(action), PendingIntent.FLAG_IMMUTABLE,
    )
}
