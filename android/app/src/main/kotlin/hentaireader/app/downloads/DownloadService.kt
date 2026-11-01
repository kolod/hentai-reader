package hentaireader.app.downloads

import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import hentaireader.app.services
import hentaireader.app.system.Notifications
import hentaireader.host.data.DownloadState
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.debounce
import kotlinx.coroutines.launch

/**
 * Keeps downloads running in the background (007 FR-022, research R12): a foreground service of
 * type dataSync that exists only while the queue has work. It shows the ongoing notification, takes
 * Pause / Resume from it, and stops itself when the queue is idle. When the system ends it (the
 * daily dataSync limit on Android 15+), running downloads are parked and continue on the next start.
 */
class DownloadService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private var watcher: Job? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        ServiceCompat.startForeground(this, Notifications.ONGOING_ID, Notifications.ongoing(this, emptyList()), TYPE)
        val queue = scope.launch {
            val q = services.downloads()
            when (intent?.action) {
                ACTION_PAUSE -> services.downloadRepository().list().filter { it.state == DownloadState.DOWNLOADING || it.state == DownloadState.QUEUED }.forEach { q.pause(it.id) }
                ACTION_RESUME -> services.downloadRepository().list().filter { it.state == DownloadState.PAUSED }.forEach { q.resume(it.id) }
            }
            update()
        }
        if (watcher == null) {
            watcher = scope.launch {
                queue.join()
                @OptIn(kotlinx.coroutines.FlowPreview::class)
                services.downloads().changes.debounce(300).collect { row ->
                    if (row.state == DownloadState.DONE && row.pagesDone >= 0 || row.state == DownloadState.FAILED || row.state == DownloadState.LIMITED) {
                        Notifications.finished(this@DownloadService, row)
                    }
                    update()
                }
            }
        }
        return START_NOT_STICKY
    }

    /** Shows the queue while it has work; otherwise the service goes (paused ones wait in Downloads). */
    private suspend fun update() {
        if (services.downloads().hasWork()) {
            getSystemService(NotificationManager::class.java).notify(Notifications.ONGOING_ID, Notifications.ongoing(this, services.downloadRepository().list()))
        } else {
            ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
            stopSelf()
        }
    }

    override fun onTimeout(startId: Int, fgsType: Int) {
        scope.launch {
            services.downloads().stopAll()
            ServiceCompat.stopForeground(this@DownloadService, ServiceCompat.STOP_FOREGROUND_REMOVE)
            stopSelf()
        }
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }

    companion object {
        const val ACTION_PAUSE = "hentaireader.app.PAUSE"
        const val ACTION_RESUME = "hentaireader.app.RESUME"
        private val TYPE = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC else 0

        /** Starts (or pokes) the service; allowed while the app is in the foreground. */
        fun start(context: Context) {
            runCatching { ContextCompat.startForegroundService(context, Intent(context, DownloadService::class.java)) }
        }
    }
}
