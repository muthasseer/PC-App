package com.advancedweighbridge.mobile

import android.content.Intent
import android.content.pm.PackageManager
import android.os.Bundle
import android.view.ContextThemeWrapper
import android.view.Gravity
import android.view.Menu
import android.view.MenuItem
import android.view.View
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.Spinner
import android.widget.TextView
import android.widget.Toolbar
import com.advancedweighbridge.core.ApiClient
import com.advancedweighbridge.core.Permission
import com.advancedweighbridge.core.Site
import com.advancedweighbridge.core.UserInfo

/** One section of the home screen. Pages are created once per screen and told when they become visible. */
interface Page {
    val view: View
    val usesSite: Boolean get() = true
    fun onShow()
    fun onHide() = Unit
    fun refresh()
    fun onSiteChanged() = refresh()
}

/** Main screen: toolbar, optional site filter, the current page and the tab bar. */
class HomeActivity : BaseActivity() {
    lateinit var api: ApiClient
        private set
    val user: UserInfo get() = api.user ?: error("signed out")

    /** Selected site code, or null for all sites the user may see. */
    var site: String? = null
        private set
    var sites: List<Site> = emptyList()
        private set

    private lateinit var toolbar: Toolbar
    private lateinit var siteBar: LinearLayout
    private lateinit var content: FrameLayout
    private lateinit var tabBar: LinearLayout
    private val pages = LinkedHashMap<String, Page>()
    private val tabs = HashMap<String, TextView>()
    private var current: String? = null
    private var resumed = false
    var onPermissionResult: ((Boolean) -> Unit)? = null

    private data class Tab(val key: String, val title: Int, val permission: String?, val create: () -> Page)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val client = app.api
        if (client?.session == null) {
            // Process restarted: the in-memory session is gone; the start screen renews it from the stored token.
            startActivity(Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK))
            finish()
            return
        }
        api = client
        site = app.prefs.site?.takeIf { it in user.sites || user.role == "Administrator" }

        val root = column()
        toolbar = Toolbar(ContextThemeWrapper(this, android.R.style.ThemeOverlay_Material_Dark_ActionBar)).apply {
            setBackgroundColor(getColor(R.color.brand))
            popupTheme = android.R.style.ThemeOverlay_Material_Light
            title = getString(R.string.app_name)
            subtitle = "${user.displayName} · ${api.base.host}"
        }
        root.add(toolbar)
        setActionBar(toolbar)

        siteBar = root.add(row().apply { setPadding(dp(16), dp(4), dp(16), dp(4)); setBackgroundColor(getColor(R.color.surface_variant)) })
        siteBar.visibility = View.GONE
        content = root.add(FrameLayout(this), height = 0, weight = 1f)
        tabBar = root.add(row().apply { setBackgroundColor(getColor(R.color.card)); elevation = dp(8).toFloat() })

        val all = listOf(
            Tab(TAB_DASHBOARD, R.string.tab_dashboard, Permission.DASHBOARD) { DashboardPage(this) },
            Tab(TAB_TRANSACTIONS, R.string.tab_transactions, Permission.HISTORY) { TransactionsPage(this) },
            Tab(TAB_REPORTS, R.string.tab_reports, Permission.REPORTS) { ReportsPage(this) },
            Tab(TAB_ALERTS, R.string.tab_alerts, Permission.DASHBOARD) { AlertsPage(this) },
            Tab(TAB_ACCOUNT, R.string.tab_account, null) { AccountPage(this) },
        )
        for (tab in all.filter { it.permission == null || user.can(it.permission) }) {
            val label = text(getString(tab.title), TextKind.CAPTION).apply {
                gravity = Gravity.CENTER
                maxLines = 2
                setPadding(dp(4), dp(12), dp(4), dp(12))
                setBackgroundResource(selectableBackground())
                setOnClickListener { show(tab.key) }
                contentDescription = getString(tab.title)
            }
            tabBar.add(label, width = 0, weight = 1f)
            tabs[tab.key] = label
            pages[tab.key] = tab.create()
        }

        setContentView(root)
        edgeToEdge(root, toolbar)
        show(savedInstanceState?.getString(STATE_TAB) ?: intent.getStringExtra(EXTRA_TAB) ?: pages.keys.first())
        loadSites()
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        intent.getStringExtra(EXTRA_TAB)?.let { show(it) }
    }

    override fun onResume() {
        super.onResume()
        resumed = true
        current?.let { pages[it]?.onShow() }
    }

    override fun onPause() {
        resumed = false
        current?.let { pages[it]?.onHide() }
        super.onPause()
    }

    override fun onSaveInstanceState(outState: Bundle) {
        super.onSaveInstanceState(outState)
        outState.putString(STATE_TAB, current)
    }

    fun show(key: String) {
        val target = if (pages.containsKey(key)) key else pages.keys.first()
        if (target == current) return
        if (resumed) current?.let { pages[it]?.onHide() }
        current = target
        val page = pages.getValue(target)
        content.removeAllViews()
        content.addView(page.view, FrameLayout.LayoutParams(MATCH, MATCH))
        for ((k, label) in tabs) {
            val selected = k == target
            label.setTextColor(getColor(if (selected) R.color.brand_text else R.color.text_secondary))
            label.setTypeface(null, if (selected) android.graphics.Typeface.BOLD else android.graphics.Typeface.NORMAL)
            label.isSelected = selected
        }
        siteBar.visibility = if (page.usesSite && sites.size > 1) View.VISIBLE else View.GONE
        if (resumed) page.onShow()
    }

    private fun loadSites() {
        io(work = { api.sites() }, error = {}) { list ->
            sites = list
            if (site != null && list.none { it.code == site }) site = null
            siteBar.removeAllViews()
            if (list.size > 1) {
                siteBar.add(text(getString(R.string.site), TextKind.BODY, color = getColor(R.color.text_secondary)), width = WRAP)
                val names = listOf(getString(R.string.all_sites)) + list.map { "${it.name} (${it.code})" }
                val spinner: Spinner = choice(names, list.indexOfFirst { it.code == site } + 1) { index ->
                    site = if (index == 0) null else list[index - 1].code
                    app.prefs.site = site
                    current?.let { pages[it]?.onSiteChanged() }
                }
                siteBar.add(spinner, width = 0, weight = 1f)
            }
            siteBar.visibility = if (current?.let { pages[it]?.usesSite } == true && list.size > 1) View.VISIBLE else View.GONE
        }
    }

    override fun onCreateOptionsMenu(menu: Menu): Boolean {
        menu.add(0, MENU_REFRESH, 0, R.string.refresh).setIcon(R.drawable.ic_refresh).setShowAsAction(MenuItem.SHOW_AS_ACTION_IF_ROOM)
        return true
    }

    override fun onOptionsItemSelected(item: MenuItem): Boolean {
        if (item.itemId == MENU_REFRESH) {
            current?.let { pages[it]?.refresh() }
            return true
        }
        return super.onOptionsItemSelected(item)
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == REQUEST_NOTIFICATIONS) {
            onPermissionResult?.invoke(grantResults.isNotEmpty() && grantResults[0] == PackageManager.PERMISSION_GRANTED)
            onPermissionResult = null
        }
    }

    companion object {
        const val EXTRA_TAB = "tab"
        const val TAB_DASHBOARD = "dashboard"
        const val TAB_TRANSACTIONS = "transactions"
        const val TAB_REPORTS = "reports"
        const val TAB_ALERTS = "alerts"
        const val TAB_ACCOUNT = "account"
        const val REQUEST_NOTIFICATIONS = 7
        private const val STATE_TAB = "tab"
        private const val MENU_REFRESH = 1
    }
}
