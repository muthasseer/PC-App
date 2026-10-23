package com.advancedweighbridge.mobile

import android.content.Intent
import android.view.View
import android.view.ViewGroup
import android.view.inputmethod.EditorInfo
import android.widget.BaseAdapter
import android.widget.Button
import android.widget.CheckBox
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ListView
import android.widget.TextView
import com.advancedweighbridge.core.Display
import com.advancedweighbridge.core.Page as ResultPage
import com.advancedweighbridge.core.Period
import com.advancedweighbridge.core.Periods
import com.advancedweighbridge.core.TransactionQuery
import com.advancedweighbridge.core.TransactionSummary
import kotlinx.coroutines.Job

/** Searchable list of transactions (synchronised tickets). Tapping one opens its details and ticket. */
class TransactionsPage(private val home: HomeActivity) : Page {
    private val periods = listOf(Period.TODAY to R.string.period_today, Period.LAST_7_DAYS to R.string.period_7_days, Period.LAST_30_DAYS to R.string.period_30_days, Period.ALL to R.string.period_all)
    private val statuses = listOf(null to R.string.status_all, "Pending" to R.string.status_pending, "Completed" to R.string.status_completed, "Cancelled" to R.string.status_cancelled)
    private var period = 1
    private var status = 0
    private val items = ArrayList<TransactionSummary>()
    private var last: ResultPage<TransactionSummary>? = null
    private var loading: Job? = null
    private var loaded = false

    private val search: EditText
    private val manualOnly: CheckBox
    private val summary: TextView
    private val more: Button
    private val adapter = Adapter()
    override val view: View

    init {
        val c = home
        val root = c.column()
        val filters = root.add(c.column(12).apply { setBackgroundColor(c.getColor(R.color.surface_variant)) })
        val searchRow = filters.add(c.row())
        search = searchRow.add(c.input(c.getString(R.string.search_hint), Inputs.TEXT), width = 0, weight = 1f)
        search.imeOptions = EditorInfo.IME_ACTION_SEARCH
        search.setOnEditorActionListener { _, action, _ -> (action == EditorInfo.IME_ACTION_SEARCH).also { if (it) reload() } }
        searchRow.add(c.button(c.getString(R.string.search)) { reload() }, width = WRAP)
        val choices = filters.add(c.row())
        choices.add(c.choice(periods.map { c.getString(it.second) }, period) { period = it; reload() }, width = 0, weight = 1f)
        choices.add(c.choice(statuses.map { c.getString(it.second) }, status) { status = it; reload() }, width = 0, weight = 1f)
        manualOnly = filters.add(CheckBox(c).apply {
            text = c.getString(R.string.manual_only)
            setOnCheckedChangeListener { _, _ -> reload() }
        })
        summary = root.add(c.text("", TextKind.CAPTION, color = c.getColor(R.color.text_secondary)).apply { setPadding(c.dp(16), c.dp(6), c.dp(16), c.dp(6)) })

        val listView = ListView(c).apply {
            dividerHeight = c.dp(1)
            setOnItemClickListener { _, _, position, _ ->
                items.getOrNull(position)?.let { home.startActivity(Intent(home, TransactionActivity::class.java).putExtra(TransactionActivity.EXTRA_UID, it.uid)) }
            }
        }
        more = c.button(c.getString(R.string.load_more), primary = false) { load(append = true) }
        listView.addFooterView(more, null, false)
        listView.adapter = adapter
        root.add(listView, height = 0, weight = 1f)
        view = root
    }

    override fun onShow() {
        if (!loaded) reload()
    }

    override fun refresh() = reload()

    private fun reload() = load(append = false)

    private fun load(append: Boolean) {
        loading?.cancel()
        val range = Periods.range(periods[period].first)
        val query = TransactionQuery(
            site = home.site, fromUtc = range.fromUtc, toUtc = range.toUtc, text = search.text.toString().trim().ifEmpty { null },
            status = statuses[status].first, manualOnly = manualOnly.isChecked, page = if (append) (last?.page ?: 0) + 1 else 1,
        )
        summary.text = home.getString(R.string.loading)
        loading = home.io(work = { home.api.transactions(query) }, error = { summary.text = it.message }) { page ->
            loaded = true
            if (!append) items.clear()
            items.addAll(page.items)
            last = page
            adapter.notifyDataSetChanged()
            summary.text = home.getString(R.string.transactions_count, items.size, page.total)
            more.visibility = if (page.hasMore) View.VISIBLE else View.GONE
        }
    }

    private inner class Adapter : BaseAdapter() {
        override fun getCount() = items.size
        override fun getItem(position: Int): Any = items[position]
        override fun getItemId(position: Int) = position.toLong()

        override fun getView(position: Int, convertView: View?, parent: ViewGroup?): View {
            val row = convertView as? Row ?: Row(home)
            row.bind(items[position])
            return row
        }
    }

    /** One list row: ticket, status, vehicle and parties, net weight and time; manual weights are labelled. */
    private inner class Row(c: HomeActivity) : LinearLayout(c) {
        private val ticket: TextView
        private val state: TextView
        private val manual: TextView
        private val parties: TextView
        private val net: TextView
        private val time: TextView

        init {
            orientation = LinearLayout.VERTICAL
            setPadding(c.dp(16), c.dp(10), c.dp(16), c.dp(10))
            val top = add(c.row())
            ticket = top.add(c.text("", TextKind.SUBHEAD, bold = true), width = 0, weight = 1f)
            manual = top.add(c.badge(c.getString(R.string.manual_weight), R.color.state_motion), width = WRAP)
            state = top.add(c.badge("", R.color.state_info), width = WRAP).apply { (layoutParams as ViewGroup.MarginLayoutParams).marginStart = c.dp(6) }
            parties = add(c.text("", TextKind.BODY))
            val bottom = add(c.row())
            net = bottom.add(c.text("", TextKind.SUBHEAD, bold = true), width = 0, weight = 1f)
            time = bottom.add(c.text("", TextKind.CAPTION, color = c.getColor(R.color.text_secondary)), width = WRAP)
        }

        fun bind(t: TransactionSummary) {
            val c = home
            ticket.text = "${t.ticketNo} · ${t.vehicleReg}"
            manual.visibility = if (t.hasManualWeight) View.VISIBLE else View.GONE
            state.text = Texts.status(c, t.status)
            state.background = c.rounded(c.getColor(Texts.statusColor(t.status)), radiusDp = 6)
            parties.text = listOfNotNull(t.customer ?: t.supplier, t.material, t.siteCode).joinToString(" · ")
            net.text = if (t.netG != null) c.getString(R.string.net_value, Display.kg(t.netG)) else c.getString(R.string.first_weight_value, Display.kg(t.grossG ?: t.tareG))
            time.text = Display.time(t.completedUtc ?: t.createdUtc)
        }
    }
}
