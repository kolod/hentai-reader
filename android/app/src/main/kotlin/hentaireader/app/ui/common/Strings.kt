package hentaireader.app.ui.common

import android.annotation.SuppressLint
import android.content.Context
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.platform.LocalContext
import java.text.NumberFormat
import java.time.Instant
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.util.Locale

/**
 * UI strings by key, for the contract vocabulary whose keys are built from values
 * (specs/003-windows-host/contracts/l10n-keys.md: `Filter_<id>`, `Sort_<value>`, `Type_<value>`,
 * `Namespace_<ns>`, `Error_<Kind>_Title`, `Setting_<id>_<value>`), plus locale formatting. Port of
 * the Windows Strings helper. Those resources are kept from shrinking by res/raw/keep.xml.
 */
class Strings(private val context: Context) {
    val locale: Locale get() = context.resources.configuration.locales[0]

    @SuppressLint("DiscouragedApi")
    private fun id(key: String, type: String) = context.resources.getIdentifier(key, type, context.packageName)

    /** The string for [key], or null when it does not exist. */
    fun find(key: String): String? = id(key, "string").takeIf { it != 0 }?.let { context.getString(it) }

    fun get(key: String, vararg args: Any): String {
        val id = id(key, "string")
        if (id == 0) return key
        return if (args.isEmpty()) context.getString(id) else context.getString(id, *args)
    }

    /** Plural form of [key] for [n], with [n] formatted for the locale as the first argument. */
    fun plural(key: String, n: Long, vararg more: Any): String {
        val id = id(key, "plurals")
        if (id == 0) return "$n"
        return context.resources.getQuantityString(id, n.coerceAtMost(Int.MAX_VALUE.toLong()).toInt(), number(n), *more)
    }

    fun number(n: Number): String = NumberFormat.getIntegerInstance(locale).format(n)

    fun decimal(n: Double, digits: Int = 1): String =
        NumberFormat.getNumberInstance(locale).apply { maximumFractionDigits = digits; minimumFractionDigits = digits }.format(n)

    fun filterLabel(id: String) = get("Filter_$id")
    fun sortLabel(value: String) = get("Sort_$value")
    fun typeLabel(value: String) = get("Type_$value")
    fun settingLabel(id: String) = get("Setting_$id")

    /** Resource keys cannot hold '-', so "e-hentai" becomes "Setting_site_e_hentai". */
    fun settingValueLabel(id: String, value: String) = get("Setting_${id}_${value.replace('-', '_')}")
    fun errorTitle(kind: String) = get("Error_${kind}_Title")
    fun errorBody(kind: String) = get("Error_${kind}_Body")

    /** Known tag namespaces are translated; unknown ones are shown as the source gives them. */
    fun namespaceLabel(ns: String?): String = if (ns.isNullOrEmpty()) get("Namespace_other") else find("Namespace_$ns") ?: ns

    /** Native name of an ISO 639-1 language ("日本語", "Deutsch"). */
    fun languageName(iso: String): String {
        val l = Locale.forLanguageTag(iso)
        val native = l.getDisplayName(l)
        return if (native.isEmpty() || native == iso) iso else native.replaceFirstChar { it.titlecase(l) }
    }

    fun date(iso: String): String = runCatching {
        DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM).withLocale(locale).format(OffsetDateTime.parse(iso).atZoneSameInstant(ZoneId.systemDefault()))
    }.getOrDefault(iso)

    /** "just now", "5 minutes ago", "3 hours ago", "yesterday", else the date (history, library). */
    fun relative(at: Instant, now: Instant = Instant.now()): String {
        val zone = ZoneId.systemDefault()
        val minutes = java.time.Duration.between(at, now).toMinutes()
        val day = at.atZone(zone).toLocalDate()
        val today = now.atZone(zone).toLocalDate()
        return when {
            minutes < 1 -> get("Time_JustNow")
            minutes < 60 -> plural("Time_MinutesAgo", minutes)
            day == today -> plural("Time_HoursAgo", minutes / 60)
            day == today.minusDays(1) -> get("Time_Yesterday")
            else -> DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM).withLocale(locale).format(day)
        }
    }
}

@Composable
fun rememberStrings(): Strings {
    val context = LocalContext.current
    return remember(context) { Strings(context) }
}
