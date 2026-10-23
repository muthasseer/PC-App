package com.advancedweighbridge.mobile

import android.content.Intent
import android.graphics.Typeface
import android.view.View
import android.view.ViewGroup
import android.widget.BaseAdapter
import android.widget.LinearLayout
import android.widget.ListView
import android.widget.TextView
import com.advancedweighbridge.core.AppNotification
import com.advancedweighbridge.core.Display
import java.util.UUID

/** Alerts raised by the sites (manual weights, automation faults, failed backups, remote request results, ...). */
class AlertsPage(private val home: HomeActivity) : Page {
    private val items = ArrayList<AppNotification>()
    private val status: TextView
    private val adapter = Adapter()
    override val view: View
    override val usesSite get() = false

    init {
        val c = home
        val root = c.column()
        val bar = root.add(c.row().apply { setPadding(c.dp(16), c.dp(4), c.dp(8), c.dp(4)); setBackgroundColor(c.getColor(R.color.surface_variant)) })
        status = bar.add(c.text("", TextKind.CAPTION, color = c.getColor(R.color.text_secondary)), width = 0, weight = 1f)
        bar.add(c.button(c.getString(R.string.mark_all_read), primary = false) { markAllRead() }, width = WRAP)
        val list = ListView(c).apply {
            dividerHeight = c.dp(1)
            setOnItemClickListener { _, _, position, _ -> open(items[position]) }
        }
        list.adapter = adapter
        list.emptyView = root.add(c.text(c.getString(R.string.no_alerts), TextKind.BODY).apply { setPadding(c.dp(16), c.dp(16), c.dp(16), c.dp(16)) })
        root.add(list, height = 0, weight = 1f)
        view = root
    }

    override fun onShow() = refresh()

    override fun refresh() {
        status.text = home.getString(R.string.loading)
        home.io(work = { home.api.notifications(limit = 100) }, error = { status.text = it.message }) { list ->
            items.clear()
            items.addAll(list)
            adapter.notifyDataSetChanged()
            status.text = home.getString(R.string.alerts_unread, list.count { !it.isRead })
            list.maxOfOrNull { it.id }?.let { max -> if (max > home.app.prefs.lastAlertId) home.app.prefs.lastAlertId = max }
        }
    }

    private fun markAllRead() {
        home.io(work = { home.api.markAllRead() }) { refresh() }
    }

    private fun open(n: AppNotification) {
        val uid = n.entityId?.takeIf { runCatching { UUID.fromString(it) }.isSuccess } ?: return
        home.startActivity(Intent(home, TransactionActivity::class.java).putExtra(TransactionActivity.EXTRA_UID, uid))
    }

    private inner class Adapter : BaseAdapter() {
        override fun getCount() = items.size
        override fun getItem(position: Int): Any = items[position]
        override fun getItemId(position: Int) = items[position].id

        override fun getView(position: Int, convertView: View?, parent: ViewGroup?): View {
            val row = convertView as? Row ?: Row(home)
            row.bind(items[position])
            return row
        }
    }

    private inner class Row(c: HomeActivity) : LinearLayout(c) {
        private val severity: TextView
        private val title: TextView
        private val message: TextView
        private val meta: TextView

        init {
            orientation = LinearLayout.VERTICAL
            setPadding(c.dp(16), c.dp(10), c.dp(16), c.dp(10))
            val top = add(c.row())
            severity = top.add(c.badge("", R.color.state_info), width = WRAP)
            title = top.add(c.text("", TextKind.SUBHEAD).apply { setPadding(c.dp(8), 0, 0, 0) }, width = 0, weight = 1f)
            message = add(c.text("", TextKind.BODY), topDp = 4)
            meta = add(c.text("", TextKind.CAPTION, color = c.getColor(R.color.text_secondary)), topDp = 2)
        }

        fun bind(n: AppNotification) {
            val c = home
            severity.text = Texts.severity(c, n.severity)
            severity.background = c.rounded(c.getColor(Texts.severityColor(n.severity)), radiusDp = 6)
            title.text = if (n.isRead) n.title else "● ${n.title}"
            title.setTypeface(null, if (n.isRead) Typeface.NORMAL else Typeface.BOLD)
            message.text = n.message
            meta.text = "${n.siteCode} · ${Display.time(n.atUtc)}"
        }
    }
}
