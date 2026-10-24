package com.advancedweighbridge.mobile

import android.os.Bundle
import android.view.Gravity
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView

/** Start-up: restores the encrypted session when there is one, otherwise opens the sign-in screen. */
class MainActivity : BaseActivity() {
    private lateinit var message: TextView
    private lateinit var progress: ProgressBar
    private lateinit var actions: LinearLayout

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val api = app.api
        when {
            api == null || !app.tokens.hasToken() -> openLogin()
            api.session != null -> openHome(intent.getStringExtra(HomeActivity.EXTRA_TAB))
            else -> {
                buildUi(api.base.host)
                resume()
            }
        }
    }

    private fun buildUi(host: String) {
        val root = column(24).apply { gravity = Gravity.CENTER }
        root.add(text(getString(R.string.app_name), TextKind.HEADLINE).apply { gravity = Gravity.CENTER })
        progress = root.add(ProgressBar(this), width = WRAP, topDp = 24)
        message = root.add(text(getString(R.string.connecting, host), TextKind.BODY).apply { gravity = Gravity.CENTER }, topDp = 16)
        actions = root.add(row().apply { gravity = Gravity.CENTER }, topDp = 16)
        actions.add(button(getString(R.string.retry)) { resume() }, width = WRAP)
        actions.add(button(getString(R.string.sign_in_again), primary = false) { openLogin() }, width = WRAP)
        actions.visibility = android.view.View.GONE
        setContentView(root)
        edgeToEdge(root)
    }

    private fun resume() {
        val api = app.api ?: return openLogin()
        progress.visibility = android.view.View.VISIBLE
        actions.visibility = android.view.View.GONE
        message.text = getString(R.string.connecting, api.base.host)
        io(
            work = { api.resume() },
            error = {
                // The server cannot be reached: keep the stored session and offer to try again.
                progress.visibility = android.view.View.GONE
                actions.visibility = android.view.View.VISIBLE
                message.text = getString(R.string.cannot_reach, it.message)
            },
        ) { ok ->
            if (ok) openHome(intent.getStringExtra(HomeActivity.EXTRA_TAB)) else openLogin(getString(R.string.session_ended))
        }
    }
}
