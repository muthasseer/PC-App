package com.advancedweighbridge.mobile

import android.annotation.TargetApi
import android.app.Activity
import android.content.Intent
import android.os.Build
import android.util.Log
import android.view.View
import android.view.WindowInsets
import android.widget.Toast
import com.advancedweighbridge.core.ApiException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.MainScope
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** Common plumbing: a coroutine scope tied to the screen, background API calls, session loss and system-bar insets. */
abstract class BaseActivity : Activity() {
    val scope = MainScope()
    val app: AwmsApp get() = AwmsApp.of(this)

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }

    /**
     * Runs [work] off the UI thread and passes its result to [done] on the UI thread. API errors go to [error]
     * (a short message by default); a session that can no longer be renewed returns to the sign-in screen.
     */
    fun <T> io(work: () -> T, error: (ApiException) -> Unit = { toast(it.message) }, done: (T) -> Unit): Job = scope.launch {
        val result = try {
            withContext(Dispatchers.IO) { work() }
        } catch (e: ApiException) {
            if (!sessionLost(e)) error(e)
            return@launch
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            Log.e("Awms", "Unexpected error", e)
            error(ApiException(getString(R.string.error_unexpected, e.message ?: e.javaClass.simpleName)))
            return@launch
        }
        done(result)
    }

    /** Returns true (and opens the sign-in screen) when [e] means the stored session has ended. */
    fun sessionLost(e: ApiException): Boolean {
        if (e.status != 401 || e.mfaRequired) return false
        if (this is LoginActivity) return false
        startActivity(Intent(this, LoginActivity::class.java).putExtra(LoginActivity.EXTRA_MESSAGE, getString(R.string.session_ended)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK))
        finish()
        return true
    }

    fun toast(message: String?) {
        Toast.makeText(this, message ?: getString(R.string.error_generic), Toast.LENGTH_LONG).show()
    }

    /**
     * Draws behind the system bars (required from Android 15) and pads the content instead: [top] receives the status
     * bar height, [root] the rest (navigation bar, keyboard, display cut-outs).
     */
    fun edgeToEdge(root: View, top: View = root) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) applyInsets(root, top)
    }

    @TargetApi(Build.VERSION_CODES.R)
    @Suppress("DEPRECATION") // needed on Android 11-14; Android 15+ always draws edge to edge
    private fun applyInsets(root: View, top: View) {
        window.setDecorFitsSystemWindows(false)
        val topBase = top.paddingTop
        root.setOnApplyWindowInsetsListener { _, insets ->
            val bars = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.ime() or WindowInsets.Type.displayCutout())
            if (top === root) {
                root.setPadding(bars.left, topBase + bars.top, bars.right, bars.bottom)
            } else {
                top.setPadding(top.paddingLeft, topBase + bars.top, top.paddingRight, top.paddingBottom)
                root.setPadding(bars.left, 0, bars.right, bars.bottom)
            }
            WindowInsets.CONSUMED
        }
        root.requestApplyInsets()
    }

    fun openHome(tab: String? = null) {
        startActivity(Intent(this, HomeActivity::class.java).apply {
            tab?.let { putExtra(HomeActivity.EXTRA_TAB, it) }
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK)
        })
        finish()
    }

    fun openLogin(message: String? = null) {
        startActivity(Intent(this, LoginActivity::class.java).apply {
            message?.let { putExtra(LoginActivity.EXTRA_MESSAGE, it) }
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK)
        })
        finish()
    }
}
