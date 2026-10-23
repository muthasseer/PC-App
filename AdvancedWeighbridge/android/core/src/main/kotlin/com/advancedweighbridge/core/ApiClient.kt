package com.advancedweighbridge.core

import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.IOException
import java.util.concurrent.TimeUnit

/** An error the user should see: the server's message, or a plain description of a network problem. */
class ApiException(message: String, val status: Int = 0, val mfaRequired: Boolean = false) : Exception(message) {
    val isAuthentication get() = status == 401
    val isNetwork get() = status == 0
}

/** Persists the refresh token (the Android app stores it encrypted with the Android Keystore). */
interface TokenStore {
    fun load(): String?
    fun save(refreshToken: String?)
}

sealed class LoginOutcome {
    data class Success(val session: Session) : LoginOutcome()
    data class NeedsCode(val message: String) : LoginOutcome()
    data class Failed(val message: String) : LoginOutcome()
}

/**
 * Blocking client for the AWMS cloud API (call it from a background thread / Dispatchers.IO). Access tokens are kept in
 * memory only; an expired access token is renewed once with the stored refresh token, transparently.
 */
class ApiClient(baseUrl: String, private val tokens: TokenStore, client: OkHttpClient? = null) {
    val base: HttpUrl = validate(baseUrl)
    private val http = client ?: OkHttpClient.Builder()
        .connectTimeout(15, TimeUnit.SECONDS)
        .readTimeout(30, TimeUnit.SECONDS)
        .build()
    private val lock = Any()

    @Volatile var session: Session? = null
        private set

    val user: UserInfo? get() = session?.user

    companion object {
        private val JSON = "application/json; charset=utf-8".toMediaType()

        /** HTTPS is required; plain HTTP is accepted only for the emulator host and this device (development). */
        fun validate(url: String): HttpUrl {
            val trimmed = url.trim().trimEnd('/') + "/"
            val parsed = trimmed.toHttpUrlOrNull() ?: throw ApiException("The server address is not a valid URL.")
            val local = parsed.host in setOf("10.0.2.2", "localhost", "127.0.0.1")
            if (!parsed.isHttps && !local) throw ApiException("The server address must start with https:// (encrypted connection).")
            return parsed
        }
    }

    // ------------------------------------------------------------------ authentication

    fun login(username: String, password: String, code: String?): LoginOutcome {
        val body = Json.obj("username" to username.trim(), "password" to password, "totpCode" to code?.trim()?.ifEmpty { null })
        return try {
            val s = Session.from(send("POST", "auth/login", body, authenticated = false))
            accept(s)
            LoginOutcome.Success(s)
        } catch (e: ApiException) {
            when {
                e.mfaRequired -> LoginOutcome.NeedsCode(e.message ?: "Enter the authentication code.")
                else -> LoginOutcome.Failed(e.message ?: "Sign-in failed.")
            }
        }
    }

    /** Restores the session from the stored refresh token. Returns false when the user must sign in again. */
    fun resume(): Boolean = try {
        refresh()
    } catch (e: ApiException) {
        if (!e.isNetwork) tokens.save(null)
        if (e.isNetwork) throw e
        false
    }

    fun logout() {
        val refresh = session?.refreshToken ?: tokens.load()
        try {
            if (refresh != null && session != null) send("POST", "auth/logout", Json.obj("refreshToken" to refresh))
        } catch (_: ApiException) {
            // signing out locally is enough when the server cannot be reached
        } finally {
            session = null
            tokens.save(null)
        }
    }

    fun changePassword(current: String, new: String): Session {
        val s = Session.from(send("POST", "me/password", Json.obj("currentPassword" to current, "newPassword" to new)))
        accept(s)
        return s
    }

    fun me(): UserInfo = UserInfo.from(send("GET", "me"))

    private fun accept(s: Session) {
        session = s
        tokens.save(s.refreshToken)
    }

    private fun refresh(): Boolean = synchronized(lock) {
        val token = tokens.load() ?: return false
        return try {
            accept(Session.from(send("POST", "auth/refresh", Json.obj("refreshToken" to token), authenticated = false)))
            true
        } catch (e: ApiException) {
            if (e.isAuthentication) {
                tokens.save(null)
                session = null
                false
            } else {
                throw e
            }
        }
    }

    // ------------------------------------------------------------------ data

    fun sites(): List<Site> = send("GET", "sites").list.map { Site.from(it) }

    fun status(site: String? = null): List<SiteStatus> = send("GET", "status", query = mapOf("site" to site)).list.map { SiteStatus.from(it) }

    fun transactions(q: TransactionQuery): Page<TransactionSummary> {
        val j = send(
            "GET", "transactions",
            query = mapOf(
                "site" to q.site, "from" to q.fromUtc, "to" to q.toUtc, "q" to q.text, "status" to q.status,
                "manualOnly" to if (q.manualOnly) "true" else null, "page" to q.page.toString(), "pageSize" to q.pageSize.toString(),
            ),
        )
        return Page(j["items"].list.map { TransactionSummary.from(it) }, j["total"].long ?: 0, j["page"].int ?: 1, j["pageSize"].int ?: q.pageSize)
    }

    fun transaction(uid: String): TransactionDetail = TransactionDetail.from(send("GET", "transactions/$uid"))

    fun ticketPdf(uid: String): ByteArray = sendBytes("transactions/$uid/ticket.pdf")

    fun requestCancel(uid: String, reason: String): RemoteCommand = RemoteCommand.from(send("POST", "transactions/$uid/cancel", Json.obj("reason" to reason)))

    fun summary(groupBy: String, fromUtc: String, toUtc: String, site: String?): SummaryReport =
        SummaryReport.from(send("GET", "reports/summary", query = mapOf("groupBy" to groupBy, "from" to fromUtc, "to" to toUtc, "site" to site)))

    fun notifications(afterId: Long? = null, unreadOnly: Boolean = false, limit: Int = 100): List<AppNotification> =
        send("GET", "notifications", query = mapOf("afterId" to afterId?.toString(), "unreadOnly" to if (unreadOnly) "true" else null, "limit" to limit.toString()))
            .list.map { AppNotification.from(it) }

    fun markAllRead() {
        send("POST", "notifications/read", Json.obj("ids" to null))
    }

    // ------------------------------------------------------------------ transport

    private fun url(path: String, query: Map<String, String?>): HttpUrl {
        val b = base.newBuilder().addPathSegments("api/v1/$path")
        for ((k, v) in query) if (!v.isNullOrBlank()) b.addQueryParameter(k, v)
        return b.build()
    }

    private fun send(method: String, path: String, body: Json? = null, query: Map<String, String?> = emptyMap(), authenticated: Boolean = true): Json {
        val text = execute(method, url(path, query), body, authenticated, retry = true)
        return if (text.isBlank()) Json.Null else try {
            Json.parse(text)
        } catch (e: JsonException) {
            throw ApiException("The server sent an unexpected answer.")
        }
    }

    private fun sendBytes(path: String): ByteArray {
        val request = build("GET", url(path, emptyMap()), null, true)
        return call(request) { it.body?.bytes() ?: ByteArray(0) } ?: run {
            if (!refresh()) throw ApiException("Your session has expired. Sign in again.", 401)
            call(build("GET", url(path, emptyMap()), null, true)) { it.body?.bytes() ?: ByteArray(0) } ?: throw ApiException("Your session has expired. Sign in again.", 401)
        }
    }

    private fun execute(method: String, url: HttpUrl, body: Json?, authenticated: Boolean, retry: Boolean): String {
        if (authenticated && session == null && !refresh()) throw ApiException("Sign in required.", 401)
        val result = call(build(method, url, body, authenticated)) { it.body?.string().orEmpty() }
        if (result != null) return result
        // 401 with an access token: renew it once and repeat the request.
        if (authenticated && retry && refresh()) return execute(method, url, body, authenticated, retry = false)
        throw ApiException("Your session has expired. Sign in again.", 401)
    }

    private fun build(method: String, url: HttpUrl, body: Json?, authenticated: Boolean): Request {
        val b = Request.Builder().url(url).header("Accept", "application/json")
        if (authenticated) session?.let { b.header("Authorization", "Bearer ${it.accessToken}") }
        val payload = body?.stringify()?.toRequestBody(JSON)
        b.method(method, payload ?: if (method == "POST") ByteArray(0).toRequestBody(JSON) else null)
        return b.build()
    }

    /** Returns the mapped body, null for an authenticated request answered with 401, or throws [ApiException]. */
    private fun <T> call(request: Request, map: (okhttp3.Response) -> T): T? {
        try {
            http.newCall(request).execute().use { response ->
                if (response.isSuccessful) return map(response)
                val text = response.body?.string().orEmpty()
                val error = try { Json.parse(text) } catch (_: JsonException) { Json.Null }
                val message = error["error"].str ?: "The server answered ${response.code}."
                if (response.code == 401 && request.header("Authorization") != null && !error["mfaRequired"].isTrue()) return null
                throw ApiException(message, response.code, error["mfaRequired"].bool == true)
            }
        } catch (e: IOException) {
            throw ApiException("The server cannot be reached. Check the internet connection. (${e.message})")
        }
    }

    private fun Json.isTrue() = bool == true
}
