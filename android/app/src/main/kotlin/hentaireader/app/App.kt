package hentaireader.app

import android.app.Application
import android.content.ComponentCallbacks2
import android.content.Context

/** Holds the [AppServices] for the whole process. */
class App : Application() {
    val services: AppServices by lazy { AppServices(this) }

    override fun onCreate() {
        super.onCreate()
        services.start()
    }

    /** Memory pressure (007 FR-017, T076); the full budget comes back when the app is shown. */
    @Suppress("DEPRECATION")   // the RUNNING_* levels still arrive on older Android versions
    override fun onTrimMemory(level: Int) {
        super.onTrimMemory(level)
        when {
            level >= ComponentCallbacks2.TRIM_MEMORY_UI_HIDDEN -> services.trimMemory(hidden = true, low = level >= ComponentCallbacks2.TRIM_MEMORY_BACKGROUND)
            level >= ComponentCallbacks2.TRIM_MEMORY_RUNNING_LOW -> services.trimMemory(hidden = false, low = true)
        }
    }
}

val Context.services: AppServices get() = (applicationContext as App).services
