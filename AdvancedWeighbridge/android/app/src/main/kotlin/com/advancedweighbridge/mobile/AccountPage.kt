package com.advancedweighbridge.mobile

import android.Manifest
import android.app.AlertDialog
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.view.View
import android.widget.ScrollView
import android.widget.Switch
import com.advancedweighbridge.core.Permission

/** The signed-in user, alert notifications, password change, web dashboard link and sign-out. */
class AccountPage(private val home: HomeActivity) : Page {
    private val content = home.column(16)
    override val view: View = ScrollView(home).apply { addView(content) }
    override val usesSite get() = false
    private var alerts: Switch? = null

    override fun onShow() = render()

    override fun refresh() = render()

    private fun render() {
        val c = home
        val u = home.user
        content.removeAllViews()
        val card = content.add(c.card())
        card.add(c.text(u.displayName, TextKind.TITLE))
        card.add(c.field(c.getString(R.string.username), u.username), topDp = 8)
        card.add(c.field(c.getString(R.string.role), u.role))
        card.add(c.field(c.getString(R.string.sites), if (u.role == "Administrator") c.getString(R.string.all_sites) else u.sites.joinToString(", ")))
        card.add(c.field(c.getString(R.string.server_address), home.api.base.toString()))
        card.add(c.field(c.getString(R.string.two_factor), c.getString(if (u.mfaEnabled) R.string.two_factor_on else R.string.two_factor_off)))
        val rights = listOfNotNull(
            c.getString(R.string.right_dashboard).takeIf { u.can(Permission.DASHBOARD) },
            c.getString(R.string.right_history).takeIf { u.can(Permission.HISTORY) },
            c.getString(R.string.right_reports).takeIf { u.can(Permission.REPORTS) },
            c.getString(R.string.right_cancel).takeIf { u.can(Permission.CANCEL) },
        )
        card.add(c.field(c.getString(R.string.access), rights.joinToString(", ")))

        val notify = content.add(c.card(), topDp = 12)
        val toggle = notify.add(Switch(c).apply {
            text = c.getString(R.string.alerts_toggle)
            isChecked = home.app.prefs.alertsEnabled && notificationsAllowed()
            setOnCheckedChangeListener { _, on -> if (on) enableAlerts() else disableAlerts() }
        })
        alerts = toggle
        notify.add(c.text(c.getString(R.string.alerts_toggle_hint), TextKind.CAPTION, color = c.getColor(R.color.text_secondary)), topDp = 4)

        val actions = content.add(c.column(), topDp = 12)
        actions.add(c.button(c.getString(R.string.change_password), primary = false) { changePassword() })
        actions.add(c.button(c.getString(R.string.open_web_dashboard), primary = false) { openWeb() })
        actions.add(c.button(c.getString(R.string.sign_out)) { signOut() }, topDp = 8)

        val about = content.add(c.card(), topDp = 16)
        about.add(c.text(c.getString(R.string.about_title, version()), TextKind.SUBHEAD, bold = true))
        about.add(c.text(c.getString(R.string.about_text), TextKind.BODY, color = c.getColor(R.color.text_secondary)), topDp = 4)
    }

    private fun version(): String = try {
        home.packageManager.getPackageInfo(home.packageName, 0).versionName ?: "?"
    } catch (_: PackageManager.NameNotFoundException) {
        "?"
    }

    private fun notificationsAllowed(): Boolean =
        Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU || home.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED

    private fun enableAlerts() {
        if (!notificationsAllowed() && Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            home.onPermissionResult = { granted -> if (granted) startAlerts() else { alerts?.isChecked = false; home.toast(home.getString(R.string.alerts_denied)) } }
            home.requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), HomeActivity.REQUEST_NOTIFICATIONS)
            return
        }
        startAlerts()
    }

    private fun startAlerts() {
        // Start from the newest alert that exists now, so enabling does not replay old ones.
        home.io(work = { home.api.notifications(limit = 1).firstOrNull()?.id ?: 0L }, error = { alerts?.isChecked = false; home.toast(it.message) }) { newest ->
            val prefs = home.app.prefs
            if (prefs.lastAlertId < newest) prefs.lastAlertId = newest
            prefs.alertsEnabled = true
            AlertJobService.schedule(home)
            home.toast(home.getString(R.string.alerts_enabled))
        }
    }

    private fun disableAlerts() {
        home.app.prefs.alertsEnabled = false
        AlertJobService.cancel(home)
    }

    private fun changePassword() {
        val c = home
        val form = c.column(20)
        val current = form.add(c.input(c.getString(R.string.current_password), Inputs.PASSWORD))
        val first = form.add(c.input(c.getString(R.string.new_password), Inputs.PASSWORD), topDp = 8)
        val second = form.add(c.input(c.getString(R.string.repeat_password), Inputs.PASSWORD), topDp = 8)
        form.add(c.text(c.getString(R.string.password_rules), TextKind.CAPTION, color = c.getColor(R.color.text_secondary)), topDp = 8)
        val dialog = AlertDialog.Builder(c)
            .setTitle(R.string.change_password)
            .setView(form)
            .setPositiveButton(R.string.save, null)
            .setNegativeButton(R.string.cancel, null)
            .create()
        dialog.setOnShowListener {
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
                val newPassword = first.text.toString()
                when {
                    current.text.isEmpty() || newPassword.isEmpty() -> c.toast(c.getString(R.string.enter_new_password))
                    newPassword != second.text.toString() -> c.toast(c.getString(R.string.passwords_differ))
                    else -> c.io(work = { c.api.changePassword(current.text.toString(), newPassword) }) {
                        dialog.dismiss()
                        c.toast(c.getString(R.string.password_changed))
                    }
                }
            }
        }
        dialog.show()
    }

    private fun openWeb() {
        runCatching { home.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(home.api.base.toString()))) }.onFailure { home.toast(home.getString(R.string.no_browser)) }
    }

    private fun signOut() {
        AlertDialog.Builder(home)
            .setMessage(R.string.sign_out_confirm)
            .setPositiveButton(R.string.sign_out) { _, _ ->
                disableAlerts()
                home.io(work = { home.api.logout() }, error = { home.openLogin() }) { home.openLogin() }
            }
            .setNegativeButton(R.string.cancel, null)
            .show()
    }
}
