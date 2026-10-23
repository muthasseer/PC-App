package com.advancedweighbridge.core

import java.text.DecimalFormat
import java.text.DecimalFormatSymbols
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale

/** Formatting and display rules shared by every screen. */
object Display {
    private val kgFormat = DecimalFormat("#,##0", DecimalFormatSymbols(Locale.US))
    private val local = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm").withZone(ZoneId.systemDefault())

    fun kg(grams: Long?): String = if (grams == null) "—" else kgFormat.format(grams / 1000.0) + " kg"

    fun time(isoUtc: String?): String = isoUtc?.let { runCatching { local.format(Instant.parse(normalize(it))) }.getOrNull() } ?: "—"

    fun age(seconds: Double?): String = when {
        seconds == null -> "never"
        seconds < 90 -> "${seconds.toLong()} s ago"
        seconds < 5400 -> "${(seconds / 60).toLong()} min ago"
        seconds < 172800 -> "${(seconds / 3600).toLong()} h ago"
        else -> "${(seconds / 86400).toLong()} days ago"
    }

    fun ageOf(isoUtc: String?, now: Instant = Instant.now()): Double? =
        isoUtc?.let { runCatching { Duration.between(Instant.parse(normalize(it)), now).toMillis() / 1000.0 }.getOrNull() }

    /** The server writes "+00:00"; java.time also accepts that, but older Android desugaring prefers "Z". */
    private fun normalize(s: String) = if (s.endsWith("+00:00")) s.removeSuffix("+00:00") + "Z" else s

    fun isoUtc(instant: Instant): String = DateTimeFormatter.ISO_INSTANT.format(instant)
}

enum class LiveState { LIVE_STABLE, LIVE_MOTION, NOT_LIVE, DISCONNECTED, NOT_SHARED }

/** What a weighbridge tile shows. A value is only presented as live when the data is fresh. */
data class LiveView(val state: LiveState, val weight: String, val label: String, val note: String?) {
    val isLive get() = state == LiveState.LIVE_STABLE || state == LiveState.LIVE_MOTION

    companion object {
        /** The server already flags stale heartbeats; the client applies the same limit to the age it computes itself. */
        const val STALE_SECONDS = 90.0

        fun of(site: SiteStatus, wb: WeighbridgeStatus, heartbeatAgeSeconds: Double? = site.heartbeatAgeSeconds): LiveView {
            val stale = site.liveDataStale || heartbeatAgeSeconds == null || heartbeatAgeSeconds > STALE_SECONDS || wb.displayStatus == "NotLive"
            val weight = Display.kg(wb.liveG)
            return when {
                stale -> LiveView(LiveState.NOT_LIVE, weight, "NOT LIVE", "Last known value, ${Display.age(heartbeatAgeSeconds)} — not a current reading")
                wb.connection != "Connected" -> LiveView(LiveState.DISCONNECTED, "—", wb.connection.uppercase(Locale.ROOT), wb.lastError)
                wb.liveG == null -> LiveView(LiveState.NOT_SHARED, "—", "LIVE WEIGHT NOT SHARED", "The site does not send the live weight")
                wb.stable == true -> LiveView(LiveState.LIVE_STABLE, weight, "STABLE", null)
                else -> LiveView(LiveState.LIVE_MOTION, weight, "MOTION", null)
            }
        }
    }
}
