package com.advancedweighbridge.mobile

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.view.KeyEvent
import android.view.View
import android.view.inputmethod.EditorInfo
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import com.advancedweighbridge.core.ApiClient
import com.advancedweighbridge.core.ApiException
import com.advancedweighbridge.core.LoginOutcome
import com.advancedweighbridge.core.Permission
import com.advancedweighbridge.core.UserInfo

/**
 * Sign-in to the AWMS cloud server: server address, user name, password, the authenticator code when the account
 * uses two-factor authentication, and a forced password change when the administrator requires one.
 */
class LoginActivity : BaseActivity() {
    private lateinit var server: EditText
    private lateinit var username: EditText
    private lateinit var password: EditText
    private lateinit var code: EditText
    private lateinit var credentials: LinearLayout
    private lateinit var change: LinearLayout
    private lateinit var newPassword: EditText
    private lateinit var repeatPassword: EditText
    private lateinit var error: TextView
    private lateinit var info: TextView
    private lateinit var submit: Button
    private lateinit var openWeb: Button
    private var api: ApiClient? = null
    private var mustChange = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val scroll = ScrollView(this).apply { isFillViewport = true }
        val page = column()
        scroll.addView(page)

        val header = page.add(column(24).apply { setBackgroundColor(getColor(R.color.brand)) })
        header.add(text(getString(R.string.app_name), TextKind.HEADLINE, color = getColor(R.color.on_brand)))
        header.add(text(getString(R.string.login_subtitle), TextKind.BODY, color = getColor(R.color.on_brand_secondary)), topDp = 4)

        val form = page.add(column(20))
        info = form.add(text("", TextKind.BODY, color = getColor(R.color.text_secondary)))
        intent.getStringExtra(EXTRA_MESSAGE)?.let { info.text = it } ?: run { info.visibility = View.GONE }

        credentials = form.add(column(), topDp = 8)
        server = credentials.add(input(getString(R.string.server_address), Inputs.URL, app.prefs.serverUrl ?: "https://"))
        credentials.add(text(getString(R.string.server_hint), TextKind.CAPTION, color = getColor(R.color.text_secondary)))
        username = credentials.add(input(getString(R.string.username), Inputs.NAME, app.prefs.lastUsername.orEmpty(), View.AUTOFILL_HINT_USERNAME), topDp = 12)
        password = credentials.add(input(getString(R.string.password), Inputs.PASSWORD, autofill = View.AUTOFILL_HINT_PASSWORD), topDp = 8)
        code = credentials.add(input(getString(R.string.auth_code), Inputs.CODE), topDp = 8)
        code.visibility = View.GONE

        change = form.add(column(), topDp = 8)
        change.add(text(getString(R.string.must_change_password), TextKind.SUBHEAD, bold = true))
        change.add(text(getString(R.string.password_rules), TextKind.CAPTION, color = getColor(R.color.text_secondary)), topDp = 4)
        newPassword = change.add(input(getString(R.string.new_password), Inputs.PASSWORD), topDp = 8)
        repeatPassword = change.add(input(getString(R.string.repeat_password), Inputs.PASSWORD), topDp = 8)
        change.visibility = View.GONE

        error = form.add(text("", TextKind.BODY, color = getColor(R.color.state_error)), topDp = 12)
        error.visibility = View.GONE
        submit = form.add(button(getString(R.string.sign_in)) { submit() }, topDp = 12)
        openWeb = form.add(button(getString(R.string.open_web_dashboard), primary = false) { openWebDashboard() })
        openWeb.visibility = View.GONE
        form.add(text(getString(R.string.login_footer), TextKind.CAPTION, color = getColor(R.color.text_secondary)), topDp = 24)

        for (last in listOf(password, code, repeatPassword)) {
            last.imeOptions = EditorInfo.IME_ACTION_DONE
            last.setOnEditorActionListener { _, action, event -> submitOnEnter(action, event) }
        }

        setContentView(scroll)
        edgeToEdge(scroll, header)
        if (username.text.isNotEmpty()) password.requestFocus()
    }

    /** The keyboard's Done key or a hardware Enter key submits the form (once: the key-up is consumed too). */
    private fun submitOnEnter(action: Int, event: KeyEvent?): Boolean {
        if (action == EditorInfo.IME_ACTION_DONE) {
            submit()
            return true
        }
        if (event?.keyCode != KeyEvent.KEYCODE_ENTER) return false
        if (event.action == KeyEvent.ACTION_DOWN) submit()
        return true
    }

    private fun showError(message: String?) {
        error.text = message
        error.visibility = if (message.isNullOrBlank()) View.GONE else View.VISIBLE
    }

    private fun busy(on: Boolean) {
        submit.isEnabled = !on
        submit.text = getString(if (on) R.string.please_wait else if (mustChange) R.string.change_password else R.string.sign_in)
    }

    private fun submit() {
        showError(null)
        if (mustChange) return changePassword()
        val client = try {
            app.connect(server.text.toString())
        } catch (e: ApiException) {
            return showError(e.message)
        }
        val user = username.text.toString().trim()
        val pass = password.text.toString()
        if (user.isEmpty() || pass.isEmpty()) return showError(getString(R.string.enter_credentials))
        val totp = code.text.toString().takeIf { code.visibility == View.VISIBLE }
        api = client
        busy(true)
        io(work = { client.login(user, pass, totp) }, error = { busy(false); showError(it.message) }) { outcome ->
            busy(false)
            when (outcome) {
                is LoginOutcome.NeedsCode -> {
                    code.visibility = View.VISIBLE
                    code.requestFocus()
                    if (totp != null) showError(outcome.message) else info.apply { text = outcome.message; visibility = View.VISIBLE }
                }
                is LoginOutcome.Failed -> showError(outcome.message)
                is LoginOutcome.Success -> {
                    app.prefs.serverUrl = client.base.toString()
                    app.prefs.lastUsername = user
                    signedIn(outcome.session.user)
                }
            }
        }
    }

    private fun signedIn(user: UserInfo) {
        when {
            user.mustChangePassword -> {
                mustChange = true
                credentials.visibility = View.GONE
                change.visibility = View.VISIBLE
                info.visibility = View.GONE
                busy(false)
                newPassword.requestFocus()
            }
            user.mfaSetupRequired -> {
                // The cloud requires administrators to use two-factor authentication; it is set up in the web dashboard.
                showError(getString(R.string.mfa_setup_required))
                openWeb.visibility = View.VISIBLE
                signOutQuietly()
            }
            !user.can(Permission.DASHBOARD) -> {
                showError(getString(R.string.no_mobile_permission))
                signOutQuietly()
            }
            else -> openHome()
        }
    }

    private fun changePassword() {
        val client = api ?: return
        val first = newPassword.text.toString()
        if (first.isEmpty()) return showError(getString(R.string.enter_new_password))
        if (first != repeatPassword.text.toString()) return showError(getString(R.string.passwords_differ))
        busy(true)
        io(work = { client.changePassword(password.text.toString(), first) }, error = { busy(false); showError(it.message) }) { session ->
            mustChange = false
            password.setText("")
            newPassword.setText("")
            repeatPassword.setText("")
            busy(false)
            signedIn(session.user)
        }
    }

    private fun signOutQuietly() {
        val client = api ?: return
        io(work = { client.logout() }, error = {}) {}
    }

    private fun openWebDashboard() {
        val url = app.prefs.serverUrl ?: server.text.toString()
        runCatching { startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url))) }.onFailure { toast(getString(R.string.no_browser)) }
    }

    companion object {
        const val EXTRA_MESSAGE = "message"
    }
}
