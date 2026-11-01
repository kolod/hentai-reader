package hentaireader.app

import android.content.Context
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.runtime.getValue
import androidx.compose.runtime.rememberCoroutineScope
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import hentaireader.app.system.AppLocale
import hentaireader.app.system.Immersive
import hentaireader.app.system.Notifications
import hentaireader.app.ui.gate.AdultGateScreen
import hentaireader.app.ui.theme.HentaiReaderTheme
import hentaireader.host.Preferences
import java.io.File
import kotlinx.coroutines.launch

/** The only activity: full screen, the app's own language, the adult gate, then the navigation. */
class MainActivity : ComponentActivity() {
    override fun attachBaseContext(newBase: Context) {
        val language = Preferences.load(File(newBase.noBackupFilesDir, "preferences.json")).language
        super.attachBaseContext(AppLocale.wrap(newBase, language))
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Immersive.apply(this)
        val services = services
        intent?.getStringExtra(Notifications.EXTRA_OPEN)?.let { services.openRequests.tryEmit(it) }
        setContent {
            HentaiReaderTheme {
                val prefs by services.prefs.collectAsStateWithLifecycle()
                val scope = rememberCoroutineScope()
                if (!prefs.adultConfirmed) {
                    AdultGateScreen(
                        onConfirm = {
                            scope.launch {
                                services.updatePrefs { it.copy(adultConfirmed = true) }
                                services.start()
                            }
                        },
                        onDecline = { finishAndRemoveTask() },
                    )
                } else {
                    AppNavigation(services)
                }
            }
        }
    }

    override fun onNewIntent(intent: android.content.Intent) {
        super.onNewIntent(intent)
        intent.getStringExtra(Notifications.EXTRA_OPEN)?.let { services.openRequests.tryEmit(it) }
    }

    override fun onResume() {
        super.onResume()
        Immersive.hideBars(this)
    }

    override fun onStart() {
        super.onStart()
        services.shown(true)
    }

    override fun onStop() {
        super.onStop()
        services.shown(false)
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) Immersive.hideBars(this)
    }
}
