package hentaireader.app.system

import android.app.LocaleManager
import android.content.Context
import android.content.res.Configuration
import android.os.Build
import android.os.LocaleList
import android.text.format.Formatter
import hentaireader.host.Preferences
import java.text.NumberFormat
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.util.Locale

/**
 * The interface language chosen in the app, independent of the system (Constitution IX, 007 FR-034).
 * Applied by wrapping the activity's base context; a change recreates the activity on the same
 * screen. On Android 13+ the choice is also given to the system so its settings show it.
 */
object AppLocale {
    /** The locale for a `Preferences.Language` value ("" = the system's). */
    fun of(language: String): Locale? = language.takeIf { it.isNotEmpty() }?.let { Locale.forLanguageTag(it) }

    fun wrap(base: Context, language: String): Context {
        val locale = of(language) ?: return base
        Locale.setDefault(locale)
        val config = Configuration(base.resources.configuration)
        config.setLocales(LocaleList(locale))
        return base.createConfigurationContext(config)
    }

    /** Tells the system (Android 13+) so per-app language settings stay in sync. */
    fun publish(context: Context, language: String) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            val manager = context.getSystemService(LocaleManager::class.java) ?: return
            manager.applicationLocales = of(language)?.let { LocaleList(it) } ?: LocaleList.getEmptyLocaleList()
        }
    }

    /** The languages offered in Settings (the six of Constitution IX), native names. */
    fun choices(): List<Pair<String, String>> =
        Preferences.LANGUAGES.map { tag -> tag to Locale.forLanguageTag(tag).let { it.getDisplayName(it).replaceFirstChar { c -> c.titlecase(it) } } }
}

/** Numbers, dates and sizes in the context's locale (007 FR-039). */
class Formats(private val context: Context) {
    private val locale: Locale get() = context.resources.configuration.locales[0]

    fun number(n: Number): String = NumberFormat.getIntegerInstance(locale).format(n)

    fun size(bytes: Long): String = Formatter.formatShortFileSize(context, bytes)

    fun dateTime(at: Instant): String =
        DateTimeFormatter.ofLocalizedDateTime(FormatStyle.MEDIUM, FormatStyle.SHORT).withLocale(locale).format(at.atZone(ZoneId.systemDefault()))

    fun relative(at: Instant, now: Long = System.currentTimeMillis()): String =
        android.text.format.DateUtils.getRelativeTimeSpanString(at.toEpochMilli(), now, android.text.format.DateUtils.MINUTE_IN_MILLIS).toString()
}
