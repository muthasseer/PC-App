package com.advancedweighbridge.mobile

import android.app.Application
import android.content.Context
import com.advancedweighbridge.core.ApiClient

/** Process-wide state: settings, the encrypted session token and the one API client shared by screens and the alert job. */
class AwmsApp : Application() {
    lateinit var prefs: Prefs
        private set
    lateinit var tokens: KeystoreTokenStore
        private set

    @Volatile
    private var client: ApiClient? = null

    override fun onCreate() {
        super.onCreate()
        prefs = Prefs(this)
        tokens = KeystoreTokenStore(this)
        Alerts.createChannel(this)
        if (prefs.alertsEnabled) AlertJobService.schedule(this)
    }

    /** The client for the server the user last signed in to, or null before the first sign-in. */
    val api: ApiClient?
        get() = client ?: prefs.serverUrl?.let { url -> runCatching { connect(url) }.getOrNull() }

    /**
     * Returns the client for [url] (validated: https, or a local development address). Switching to another server
     * forgets the stored session of the previous one.
     */
    @Synchronized
    fun connect(url: String): ApiClient {
        val normalized = ApiClient.validate(url).toString()
        client?.let { if (it.base.toString() == normalized) return it }
        if (prefs.serverUrl != null && prefs.serverUrl != normalized) {
            tokens.save(null)
            prefs.lastAlertId = -1
        }
        return ApiClient(normalized, tokens).also { client = it }
    }

    companion object {
        fun of(context: Context) = context.applicationContext as AwmsApp
    }
}

/** Plain (non-secret) settings. The session token is kept separately, encrypted, by [KeystoreTokenStore]. */
class Prefs(context: Context) {
    private val p = context.getSharedPreferences("settings", Context.MODE_PRIVATE)

    var serverUrl: String?
        get() = p.getString("serverUrl", null)
        set(v) = p.edit().putString("serverUrl", v).apply()

    var lastUsername: String?
        get() = p.getString("lastUsername", null)
        set(v) = p.edit().putString("lastUsername", v).apply()

    var site: String?
        get() = p.getString("site", null)
        set(v) = p.edit().putString("site", v).apply()

    var alertsEnabled: Boolean
        get() = p.getBoolean("alertsEnabled", false)
        set(v) = p.edit().putBoolean("alertsEnabled", v).apply()

    /** Highest notification id already seen by the alert job; -1 until the first check sets a starting point. */
    var lastAlertId: Long
        get() = p.getLong("lastAlertId", -1)
        set(v) = p.edit().putLong("lastAlertId", v).apply()
}
