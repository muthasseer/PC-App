package com.advancedweighbridge.mobile

import android.graphics.Typeface
import android.view.Gravity
import android.view.View
import android.widget.ScrollView
import com.advancedweighbridge.core.ApiException
import com.advancedweighbridge.core.Display
import com.advancedweighbridge.core.LiveState
import com.advancedweighbridge.core.LiveView
import com.advancedweighbridge.core.SiteStatus
import com.advancedweighbridge.core.WeighbridgeStatus
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

/**
 * Status of every site and weighbridge. Weights are read from the cloud copy of the site data and are marked LIVE
 * only while the site's last heartbeat, plus the time since this phone last refreshed, is within 90 seconds.
 * Otherwise the tile says NOT LIVE and how old the value is.
 */
class DashboardPage(private val home: HomeActivity) : Page {
    private val list = home.column(12)
    override val view: View = ScrollView(home).apply { addView(list) }

    private var statuses: List<SiteStatus> = emptyList()
    private var fetchedAt: Instant? = null
    private var lastError: String? = null
    private var loop: Job? = null
    private val clock = DateTimeFormatter.ofPattern("HH:mm:ss").withZone(ZoneId.systemDefault())

    override fun onShow() {
        loop?.cancel()
        loop = home.scope.launch {
            while (isActive) {
                fetch()
                // Re-evaluate freshness between refreshes so a value turns NOT LIVE even if refreshing stops working.
                repeat(REFRESH_SECONDS / RENDER_SECONDS) {
                    delay(RENDER_SECONDS * 1000L)
                    render()
                }
            }
        }
    }

    override fun onHide() {
        loop?.cancel()
        loop = null
    }

    override fun refresh() {
        home.scope.launch { fetch() }
    }

    private suspend fun fetch() {
        try {
            val site = home.site
            statuses = withContext(Dispatchers.IO) { home.api.status(site) }
            fetchedAt = Instant.now()
            lastError = null
        } catch (e: ApiException) {
            if (home.sessionLost(e)) return
            lastError = e.message
        }
        render()
    }

    private fun render() {
        val c = home
        list.removeAllViews()
        val now = Instant.now()
        val fetched = fetchedAt
        val banner = when {
            lastError != null -> c.getString(R.string.dashboard_refresh_failed, lastError)
            fetched == null -> c.getString(R.string.loading)
            else -> c.getString(R.string.dashboard_updated, clock.format(fetched), REFRESH_SECONDS)
        }
        list.add(c.text(banner, TextKind.CAPTION, color = c.getColor(if (lastError != null) R.color.state_error else R.color.text_secondary)))
        if (fetched == null) return
        if (statuses.isEmpty()) {
            list.add(c.text(c.getString(R.string.no_sites), TextKind.BODY), topDp = 16)
            return
        }
        for (s in statuses) {
            list.add(siteHeader(s, fetched, now), topDp = 12)
            for (wb in s.weighbridges) list.add(tile(s, wb, fetched, now), topDp = 8)
            if (s.weighbridges.isEmpty()) list.add(c.text(c.getString(R.string.no_weighbridges), TextKind.BODY, color = c.getColor(R.color.text_secondary)), topDp = 8)
        }
    }

    private fun siteHeader(s: SiteStatus, fetched: Instant, now: Instant): View {
        val c = home
        val card = c.card()
        val top = card.add(c.row())
        top.add(c.text("${s.site.name} (${s.site.code})", TextKind.TITLE), width = 0, weight = 1f)
        val age = s.heartbeatAgeSeconds?.plus(java.time.Duration.between(fetched, now).toMillis() / 1000.0)
        val online = s.site.online && age != null && age <= LiveView.STALE_SECONDS
        top.add(c.badge(c.getString(if (online) R.string.site_online else R.string.site_offline), if (online) R.color.state_stable else R.color.state_not_live), width = WRAP)
        card.add(c.text(c.getString(R.string.last_contact, Texts.age(c, age)) + (s.site.appVersion?.let { " · v$it" } ?: ""), TextKind.CAPTION, color = c.getColor(R.color.text_secondary)), topDp = 4)
        s.health?.let { h ->
            val parts = mutableListOf(
                c.getString(R.string.health_sync, h.syncPending, h.syncFailed),
                c.getString(R.string.health_backup, Display.time(h.lastBackupUtc)),
                c.getString(R.string.health_disk, String.format(java.util.Locale.ROOT, "%.1f", h.diskFreeGb)),
            )
            card.add(c.text(parts.joinToString(" · "), TextKind.CAPTION, color = c.getColor(R.color.text_secondary)), topDp = 2)
            for (w in h.warnings) card.add(c.text("⚠ $w", TextKind.BODY, color = c.getColor(R.color.state_motion)), topDp = 4)
        }
        return card
    }

    private fun tile(s: SiteStatus, wb: WeighbridgeStatus, fetched: Instant, now: Instant): View {
        val c = home
        val v = LiveView.at(s, wb, fetched, now)
        val card = c.card()
        val head = card.add(c.row())
        head.add(c.text("${wb.name} (${wb.code})", TextKind.SUBHEAD, bold = true), width = 0, weight = 1f)
        head.add(c.badge(Texts.liveLabel(c, v, wb), stateColor(v.state)), width = WRAP)

        val weight = c.text(v.weight, TextKind.DISPLAY, bold = true).apply {
            typeface = Typeface.create(Typeface.MONOSPACE, Typeface.BOLD)
            gravity = Gravity.END
            contentDescription = c.getString(R.string.weight_description, v.weight, Texts.liveLabel(c, v, wb))
        }
        if (v.state == LiveState.NOT_LIVE) {
            // A delayed value stays visible for reference but is plainly marked and dimmed.
            weight.setTextColor(c.getColor(R.color.text_disabled))
            card.add(c.text(c.getString(R.string.last_known_value), TextKind.CAPTION, color = c.getColor(R.color.text_secondary)).apply { gravity = Gravity.END }, topDp = 8)
        } else {
            weight.setTextColor(c.getColor(stateColor(v.state)))
        }
        card.add(weight, topDp = if (v.state == LiveState.NOT_LIVE) 0 else 8)
        Texts.liveNote(c, v, wb)?.let { card.add(c.text(it, TextKind.BODY, color = c.getColor(R.color.text_secondary)), topDp = 4) }

        val footer = mutableListOf(c.getString(R.string.mode_label, Texts.mode(c, wb.operatingMode)))
        wb.automationState?.takeIf { wb.operatingMode == "Automatic" }?.let { footer += it }
        if (wb.pendingTransactions > 0) footer += c.getString(R.string.pending_second_weighing, wb.pendingTransactions)
        card.add(c.text(footer.joinToString(" · "), TextKind.CAPTION, color = c.getColor(R.color.text_secondary)), topDp = 8)
        return card
    }

    private companion object {
        const val REFRESH_SECONDS = 10
        const val RENDER_SECONDS = 5
    }
}
