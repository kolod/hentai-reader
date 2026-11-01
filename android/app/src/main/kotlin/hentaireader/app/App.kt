package hentaireader.app

import android.app.Application
import android.content.Context

/** Holds the [AppServices] for the whole process. */
class App : Application() {
    val services: AppServices by lazy { AppServices(this) }

    override fun onCreate() {
        super.onCreate()
        services.start()
    }
}

val Context.services: AppServices get() = (applicationContext as App).services
