package com.advancedweighbridge.mobile

import android.view.Gravity
import android.view.View
import android.widget.LinearLayout
import android.widget.ScrollView
import com.advancedweighbridge.core.Display
import com.advancedweighbridge.core.Period
import com.advancedweighbridge.core.Periods
import com.advancedweighbridge.core.SummaryReport

/** Totals of completed transactions for a period, grouped by day, site, customer, supplier, material or vehicle. */
class ReportsPage(private val home: HomeActivity) : Page {
    private val periods = listOf(
        Period.TODAY to R.string.period_today, Period.YESTERDAY to R.string.period_yesterday, Period.LAST_7_DAYS to R.string.period_7_days,
        Period.THIS_MONTH to R.string.period_this_month, Period.LAST_MONTH to R.string.period_last_month,
    )
    private val groupings = listOf(
        "day" to R.string.group_day, "material" to R.string.group_material, "customer" to R.string.group_customer,
        "supplier" to R.string.group_supplier, "vehicle" to R.string.group_vehicle, "site" to R.string.group_site,
    )
    private var period = 0
    private var grouping = 1
    private var loaded = false
    private val result: LinearLayout
    override val view: View

    init {
        val c = home
        val root = c.column()
        val filters = root.add(c.row().apply { setPadding(c.dp(12), c.dp(8), c.dp(12), c.dp(8)); setBackgroundColor(c.getColor(R.color.surface_variant)) })
        filters.add(c.choice(periods.map { c.getString(it.second) }, period) { period = it; refresh() }, width = 0, weight = 1f)
        filters.add(c.choice(groupings.map { c.getString(it.second) }, grouping) { grouping = it; refresh() }, width = 0, weight = 1f)
        result = c.column(16)
        root.add(ScrollView(c).apply { addView(result) }, height = 0, weight = 1f)
        view = root
    }

    override fun onShow() {
        if (!loaded) refresh()
    }

    override fun refresh() {
        val range = Periods.range(periods[period].first)
        val group = groupings[grouping].first
        result.removeAllViews()
        result.add(home.text(home.getString(R.string.loading), TextKind.BODY))
        home.io(work = { home.api.summary(group, range.fromUtc!!, range.toUtc!!, home.site) }, error = { show(null, it.message) }) {
            loaded = true
            show(it, null)
        }
    }

    private fun show(report: SummaryReport?, error: String?) {
        val c = home
        result.removeAllViews()
        if (report == null) {
            result.add(c.text(error ?: c.getString(R.string.error_generic), TextKind.BODY, color = c.getColor(R.color.state_error)))
            return
        }
        val totals = result.add(c.card())
        totals.add(c.text(c.getString(R.string.report_total_net), TextKind.CAPTION, color = c.getColor(R.color.text_secondary)))
        totals.add(c.text(Display.kg(report.totalNetG), TextKind.HEADLINE, bold = true))
        totals.add(c.text(c.getString(R.string.report_total_count, report.totalCount), TextKind.BODY))
        totals.add(c.text("${Display.time(report.from)} – ${Display.time(report.to)}", TextKind.CAPTION, color = c.getColor(R.color.text_secondary)), topDp = 4)

        if (report.rows.isEmpty()) {
            result.add(c.text(c.getString(R.string.report_empty), TextKind.BODY), topDp = 16)
            return
        }
        val table = result.add(c.card(), topDp = 12)
        table.add(line(c.getString(groupings[grouping].second), c.getString(R.string.report_count), c.getString(R.string.report_net), header = true))
        for (r in report.rows) table.add(line(r.key.ifBlank { "—" }, r.count.toString(), Display.kg(r.netG), header = false), topDp = 6)
        result.add(c.text(c.getString(R.string.report_note), TextKind.CAPTION, color = c.getColor(R.color.text_secondary)), topDp = 12)
    }

    private fun line(key: String, count: String, net: String, header: Boolean): View {
        val c = home
        val kind = if (header) TextKind.CAPTION else TextKind.BODY
        val color = if (header) c.getColor(R.color.text_secondary) else null
        return c.row().apply {
            add(c.text(key, kind, bold = header, color = color), width = 0, weight = 0.5f)
            add(c.text(count, kind, bold = header, color = color).apply { gravity = Gravity.END }, width = 0, weight = 0.18f)
            add(c.text(net, kind, bold = true, color = color).apply { gravity = Gravity.END }, width = 0, weight = 0.32f)
        }
    }
}
